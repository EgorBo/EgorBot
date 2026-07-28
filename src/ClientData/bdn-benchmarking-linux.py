#!/usr/bin/env python3
"""
Linux-specific helpers for the EgorBot agent.

Exports:
    install_platform_deps()   — apt / tdnf / dnf dependency installation
    run_perf_profiling()      — perf record / flamegraph generation
"""

import glob as globmod
import os
import re as re_mod
import shutil
import subprocess
import time
from pathlib import Path

# ── Injected by common module's load_platform_module() ──────────────────────
common = None  # type: ignore

# Absolute path to the perf binary (resolved during install_platform_deps)
PERF_BIN: str = ""


# ═══════════════════════════════════════════════════════════════════════════════
#  setup_platform (optional — Linux needs HOME set)
# ═══════════════════════════════════════════════════════════════════════════════

def setup_platform():
    """Ensure HOME is set (cloud-init on some distros runs without it)."""
    if not os.environ.get("HOME"):
        os.environ["HOME"] = str(Path.home()) if Path.home() != Path("/") else "/root"


# ═══════════════════════════════════════════════════════════════════════════════
#  Dependency installation
# ═══════════════════════════════════════════════════════════════════════════════

def _sudo() -> str:
    """Return 'sudo' if available and we're not already root, else ''."""
    if os.getuid() == 0:
        return ""
    return "sudo " if shutil.which("sudo") else ""


def install_platform_deps():
    """Install build dependencies via the system package manager."""
    is_helix = os.environ.get("HELIX_WORKITEM_PAYLOAD") is not None
    chk = not is_helix  # check=False on Helix so failures don't abort
    sudo = _sudo()

    if shutil.which("apt"):
        common.run(f"{sudo}apt update", check=chk)
        common.run(f"{sudo}apt install -y git zip ninja-build", check=chk)

        # Install perf if enabled and not already available
        if common.CFG.perf_enabled and not _system_perf_works():
            _build_perf_from_source()
    elif shutil.which("tdnf"):
        common.run(f"{sudo}tdnf install -y git zip ninja-build", check=chk)
        common.run(f"{sudo}tdnf update -y", check=chk)
    elif shutil.which("dnf"):
        common.run(f"{sudo}dnf install -y git zip ninja-build", check=chk)


def _system_perf_works() -> bool:
    """True if a working system-wide perf is already installed.

    Distro wrappers (e.g. Ubuntu's linux-tools stub) are on PATH but fail when the
    matching kernel package is missing, so actually run it instead of trusting PATH.
    """
    perf = shutil.which("perf")
    if not perf:
        return False
    try:
        result = subprocess.run([perf, "--version"], capture_output=True, timeout=60)
        return result.returncode == 0
    except Exception:
        return False


# ═══════════════════════════════════════════════════════════════════════════════
#  Build perf from source
# ═══════════════════════════════════════════════════════════════════════════════

def _build_perf_from_source():
    """Compile perf from the kernel source tree and set PERF_BIN."""
    global PERF_BIN
    common.post_log("perf not found, building from source...")

    # Install build dependencies
    sudo = _sudo()
    common.run(
        f"{sudo}apt update && {sudo}apt install -y "
        "build-essential git flex bison pkg-config "
        "libelf-dev libdw-dev libtraceevent-dev "
        "python3-dev libslang2-dev libperl-dev "
        "libunwind-dev libcap-dev libzstd-dev "
        "libnuma-dev libbabeltrace-dev binutils-dev "
        "libiberty-dev libaudit-dev libdebuginfod-dev "
        "systemtap-sdt-dev libbpf-dev libssl-dev",
        check=False,
    )

    # Clone a shallow copy of the kernel tree
    linux_src = common.WORK_DIR / "linux"
    if not linux_src.is_dir():
        common.run(
            f'git clone --depth 1 https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git "{linux_src}"',
        )

    perf_src = linux_src / "tools" / "perf"
    common.run("make clean || true", cwd=perf_src, check=False)
    result = common.run(f"make -j$(nproc)", cwd=perf_src, check=False)
    if result.returncode != 0:
        common.post_log("WARNING: perf build failed")
        return

    built_perf = perf_src / "perf"
    if built_perf.is_file():
        PERF_BIN = str(built_perf)
        common.run(f'"{PERF_BIN}" version --build-options', check=False)
        common.post_log(f"perf built successfully: {PERF_BIN}")
    else:
        common.post_log("WARNING: perf binary not found after build")


def _perf() -> str:
    """Return the absolute path to the perf binary, or 'perf' as fallback."""
    return PERF_BIN if PERF_BIN else (shutil.which("perf") or "perf")


# ═══════════════════════════════════════════════════════════════════════════════
#  Perf profiling (Linux only)
# ═══════════════════════════════════════════════════════════════════════════════

def run_perf_profiling():
    """Record perf data, generate flamegraphs, annotate hot assembly."""
    if common.CFG.bench_use_dotnet_performance:
        common.post_log("[PERF] Profiling is not supported for dotnet/performance benchmarks, skipping")
        return

    # Relax perf restrictions
    sudo = _sudo()
    common.run(f"{sudo}sysctl -w kernel.perf_event_paranoid=-1", check=False)
    common.run(f"{sudo}sysctl -w kernel.kptr_restrict=0", check=False)

    perf = _perf()
    if not PERF_BIN and not _system_perf_works():
        # Nothing usable yet (e.g. --skip_deps, a non-apt distro, or the deps marker
        # already existed) — try building it now rather than silently dropping the
        # profiling the user explicitly asked for.
        common.post_log("[PERF] No usable perf found, attempting to build it now...")
        _build_perf_from_source()
        perf = _perf()

    if not PERF_BIN and not _system_perf_works():
        common.post_log("[PERF] ERROR: perf is not available on this machine — "
                        "profiling was requested but cannot run. No profiling artifacts will be produced.")
        return

    common.post_log(f"[PERF] using perf: {perf}")

    # Clone FlameGraph repo
    flamegraph_dir = common.DIR_BENCHAPP / "FlameGraph"
    if not flamegraph_dir.is_dir():
        common.run(f'git clone --depth 1 https://github.com/brendangregg/FlameGraph "{flamegraph_dir}"')

    # Build/publish the benchmark app for profiling.
    # When we have core_roots (corerun), we do a *framework-dependent* build
    # because corerun provides its own runtime — mixing with a self-contained
    # publish causes crashes (SIGABRT / exit code -6).
    # When there are no core_roots, we do self-contained publish.
    rid = f"{common.TARGET_OS}-{common.TARGET_ARCH}"
    has_coreruns = bool(sorted(globmod.glob(
        str(common.CORE_ROOTS_DIR / "*" / common.make_exe("corerun"))
    )))

    if has_coreruns:
        result = common.run(f"dotnet build -c Release -f {common.CFG.bench_tfm}",
                            cwd=common.DIR_BENCHAPP, check=False)
        if result.returncode != 0:
            common.post_log("[PERF] Failed to build benchmark app, skipping profiling")
            return
        bench_dll = common.DIR_BENCHAPP / "bin" / "Release" / common.CFG.bench_tfm / "benchapp.dll"
    else:
        result = common.run(f"dotnet publish -c Release -r {rid} -f {common.CFG.bench_tfm} --sc",
                            cwd=common.DIR_BENCHAPP, check=False)
        if result.returncode != 0:
            common.post_log("[PERF] Failed to publish benchmark app, skipping profiling")
            return
        bench_dll = common.DIR_BENCHAPP / "bin" / "Release" / common.CFG.bench_tfm / rid / "publish" / "benchapp.dll"

    if not bench_dll.exists():
        common.post_log(f"[PERF] Benchmark DLL not found at {bench_dll}, skipping")
        return

    # Copy NuGet.config from runtime repo if available
    runtime_nuget = common.WORK_DIR / "runtime" / "NuGet.config"
    if runtime_nuget.exists():
        shutil.copy2(runtime_nuget, common.DIR_BENCHAPP / "NuGet.config")

    # Read benchmark list
    all_benchmarks_file = common.WORK_DIR / "all_benchmarks.txt"
    benchmarks = [l.strip() for l in all_benchmarks_file.read_text().splitlines() if l.strip()]

    if len(benchmarks) > 5:
        common.post_log(f"[PERF] Too many benchmarks ({len(benchmarks)} > 5) for profiling, skipping")
        return

    # Gather core_root paths
    corerun_paths = sorted(globmod.glob(
        str(common.CORE_ROOTS_DIR / "*" / common.make_exe("corerun"))
    ))
    if not corerun_paths:
        run_entries = [("default", None)]
    else:
        run_entries = [(Path(p).parent.name, p) for p in corerun_paths]

    perf_record_args = common.CFG.perf_record_args or "-e cpu-clock"
    high_freq = int(common.CFG.perf_record_freq) if common.CFG.perf_record_freq else 4999
    low_freq = 299

    perf_out_dir = common.ARTIFACTS_DIR / "perf"
    common.ensure_dirs(perf_out_dir)

    # Dump the full list of perf events supported by this machine once, so it
    # is uploaded as an artifact and users can pick events for -perf_events.
    perf_events_file = perf_out_dir / "perf_events.txt"
    common.post_log(f"[PERF] Dumping supported perf events to {perf_events_file.name}...")
    common.run(f"{perf} list", check=False, stdout_file=perf_events_file)

    for label, corerun_path in run_entries:
        for bdnline in benchmarks:
            bdnline_escaped = re_mod.sub(r'[^a-zA-Z0-9]', '_', bdnline)
            bench_dir = perf_out_dir / f"PerfBench__{bdnline_escaped}"
            common.ensure_dirs(bench_dir)

            common.post_log(f"[PERF] Profiling: {label} / {bdnline}")

            common.kill_process_by_name("corerun")
            common.kill_process_by_name("dotnet")
            time.sleep(3)

            perf_env = {
                **os.environ,
                "DOTNET_JitEnableOptionalRelocs": "0",
                "DOTNET_JitStdOutFile": "",
                "DOTNET_PerfMapShowOptimizationTiers": "1",
                "DOTNET_PerfMapStubGranularity": "3",
                "DOTNET_JitFramed": "1",
                "DOTNET_PerfMapEnabled": "1",
                "DOTNET_EnableWriteXorExecute": "0",
            }

            bdn_artifacts = bench_dir / "bdn_scratch"

            # NOTE: keep this argument list in sync with the BenchmarkDotNet version
            # used by benchapp.csproj -- an unknown argument makes BDN print its usage
            # and exit with code 0, which silently disables profiling.
            # Overhead evaluation is opt-in (--evaluateOverhead) in BDN 0.16+, so we
            # simply don't ask for it here.
            bdn_args = [
                "--filter", bdnline, "-i",
                "--noForcedGCs", "--disableLogFile",
                "--maxWarmupCount", "8",
                "--minIterationCount", "15000000", "--maxIterationCount", "20000000",
                "-a", str(bdn_artifacts),
            ]

            if corerun_path:
                bench_cmd = [str(corerun_path), str(bench_dll)] + bdn_args
                target_process = "corerun"
            else:
                bench_cmd = ["dotnet", str(bench_dll)] + bdn_args
                target_process = "dotnet"

            # Keep the output around so early exits can actually be diagnosed.
            bench_log = bench_dir / f"{label}_bdn_profiling_run.log"
            bench_log_handle = open(bench_log, "w", encoding="utf-8", errors="replace")
            try:
                proc = subprocess.Popen(
                    bench_cmd, env=perf_env, cwd=common.DIR_BENCHAPP,
                    stdout=bench_log_handle, stderr=subprocess.STDOUT,
                )

                common.post_log(f"[PERF]   Waiting 30s for warmup (PID={proc.pid})...")
                time.sleep(30)

                early_exit = proc.poll() is not None
            finally:
                bench_log_handle.close()

            if early_exit:
                common.post_log(f"[PERF]   Process exited early (code {proc.returncode}), skipping")
                try:
                    tail = bench_log.read_text(encoding="utf-8", errors="replace").strip().splitlines()[:30]
                    if tail:
                        common.post_log("[PERF]   Output of the failed run:\n" + "\n".join(tail))
                except Exception:
                    pass
                continue

            pid = proc.pid
            perf_data = bench_dir / "perf.data"
            perf_small = bench_dir / "perf_small.data"

            # High-frequency perf record
            common.post_log(f"[PERF]   Recording high-freq (-F {high_freq}) for 5s...")
            common.run(f"{perf} record {perf_record_args} -k 1 -g -F {high_freq} -p {pid} -o {perf_data} sleep 5",
                       check=False)
            time.sleep(2)

            # Low-frequency perf record (for speedscope)
            common.post_log(f"[PERF]   Recording low-freq (-F {low_freq}) for 3s...")
            common.run(f"{perf} record {perf_record_args} -k 1 -g -F {low_freq} -p {pid} -o {perf_small} sleep 3",
                       check=False)
            time.sleep(2)

            # Perf stat — use explicit portable events to avoid "topdown" PMU errors on VMs/AMD
            stats_file = bench_dir / f"{label}.stats"
            stat_events = "task-clock,cycles,instructions,branches,branch-misses,cache-misses,cache-references,context-switches,cpu-migrations,page-faults"
            common.run(f"{perf} stat -e {stat_events} -o {stats_file} -p {pid} sleep 6", check=False)

            # Kill the benchmark process
            common.post_log("[PERF]   Killing benchmark process...")
            try:
                proc.kill()
                proc.wait(timeout=10)
            except Exception:
                pass
            common.kill_process_by_name(target_process)
            time.sleep(2)

            # Symbolize with perf inject
            perfjit = bench_dir / "perfjit.data"
            perfjit_small = bench_dir / "perfjit_small.data"
            common.run(f"{perf} inject --input {perf_data} --jit --output {perfjit}", check=False)
            common.run(f"{perf} inject --input {perf_small} --jit --output {perfjit_small}", check=False)

            # Function report
            functions_file = bench_dir / f"{label}_functions.txt"
            common.run(f"{perf} report --input {perfjit} --no-children --percent-limit 2 --stdio",
                       check=False, stdout_file=functions_file)

            # Hot assembly annotation
            asm_file = bench_dir / f"{label}.asm"
            common.run(f"{perf} annotate --stdio2 -i {perfjit} --percent-limit 2 -M intel",
                       check=False, stdout_file=asm_file)

            # Flamegraph (interactive SVG)
            svg_file = bench_dir / f"{label}_flamegraph.svg"
            common.run(f"{perf} script -i {perfjit} | "
                       f"{flamegraph_dir}/stackcollapse-perf.pl | "
                       f"{flamegraph_dir}/flamegraph.pl",
                       check=False, stdout_file=svg_file)

            # Speedscope (collapsed stacks)
            speedscope_file = bench_dir / f"speedscope_{label}_{common.CFG.job_id}.speedscope"
            common.run(f"{perf} script -i {perfjit_small} | "
                       f"{flamegraph_dir}/stackcollapse-perf.pl",
                       check=False, stdout_file=speedscope_file)

            # Clean up large binary perf data files
            for f in [perf_data, perf_small, perfjit, perfjit_small]:
                if f.exists():
                    try:
                        f.unlink()
                    except Exception:
                        pass

            # Clean up BDN scratch directory
            if bdn_artifacts.exists():
                shutil.rmtree(bdn_artifacts, ignore_errors=True)

    common.post_log("[PERF] Profiling completed ✓")
