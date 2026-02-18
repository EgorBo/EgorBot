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

def install_platform_deps():
    """Install build dependencies via the system package manager."""
    is_helix = os.environ.get("HELIX_WORKITEM_PAYLOAD") is not None
    chk = not is_helix  # check=False on Helix so failures don't abort

    if shutil.which("apt"):
        common.run("sudo apt update", check=chk)
        common.run("sudo apt install -y git zip ninja-build", check=chk)

        # Install perf if enabled and not already available
        if common.CFG.perf_enabled and not shutil.which("perf"):
            common.post_log("perf not found, installing linux-tools...")
            common.run("sudo apt install -y linux-tools-common linux-tools-generic linux-cloud-tools-generic",
                       check=False)
            common.run(
                "bash -c 'ln -s /usr/lib/linux-tools/$(ls /usr/lib/linux-tools/ "
                "| grep -v common | head -n 1) /usr/lib/linux-tools/$(uname -r) || true'",
                check=False,
            )
    elif shutil.which("tdnf"):
        common.run("sudo tdnf install -y git zip ninja-build", check=chk)
        common.run("sudo tdnf update -y", check=chk)
    elif shutil.which("dnf"):
        common.run("sudo dnf install -y git zip ninja-build", check=chk)


# ═══════════════════════════════════════════════════════════════════════════════
#  Perf profiling (Linux only)
# ═══════════════════════════════════════════════════════════════════════════════

def run_perf_profiling():
    """Record perf data, generate flamegraphs, annotate hot assembly."""
    if common.CFG.bench_use_dotnet_performance:
        common.post_log("[PERF] Profiling is not supported for dotnet/performance benchmarks, skipping")
        return

    # Relax perf restrictions (may fail on Helix without root)
    common.run("sudo sysctl -w kernel.perf_event_paranoid=-1", check=False)
    common.run("sudo sysctl -w kernel.kptr_restrict=0", check=False)

    # Ensure perf is on PATH
    if not shutil.which("perf"):
        for p in sorted(Path("/usr/lib").glob("linux-tools-*/perf")):
            parent = str(p.parent)
            if parent not in os.environ.get("PATH", ""):
                os.environ["PATH"] = parent + os.pathsep + os.environ["PATH"]
            break
        for p in sorted(Path("/usr/lib").glob("linux-tools/*/perf")):
            parent = str(p.parent)
            if parent not in os.environ.get("PATH", ""):
                os.environ["PATH"] = parent + os.pathsep + os.environ["PATH"]
            break

    if not shutil.which("perf"):
        common.post_log("[PERF] perf not found even after PATH fix, skipping profiling")
        return

    common.post_log(f"[PERF] perf found at: {shutil.which('perf')}")

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
    high_freq = int(common.CFG.perf_record_freq) if common.CFG.perf_record_freq else 1999
    low_freq = 299

    perf_out_dir = common.ARTIFACTS_DIR / "perf"
    perf_out_dir.mkdir(parents=True, exist_ok=True)

    for label, corerun_path in run_entries:
        for bdnline in benchmarks:
            bdnline_escaped = re_mod.sub(r'[^a-zA-Z0-9]', '_', bdnline)
            bench_dir = perf_out_dir / f"PerfBench__{bdnline_escaped}"
            bench_dir.mkdir(parents=True, exist_ok=True)

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

            if corerun_path:
                bench_cmd = [
                    str(corerun_path), str(bench_dll),
                    "--filter", bdnline, "-i",
                    "--noForcedGCs", "--noOverheadEvaluation", "--disableLogFile",
                    "--maxWarmupCount", "8",
                    "--minIterationCount", "15000000", "--maxIterationCount", "20000000",
                    "-a", str(bdn_artifacts),
                ]
                target_process = "corerun"
            else:
                bench_cmd = [
                    "dotnet", str(bench_dll),
                    "--filter", bdnline, "-i",
                    "--noForcedGCs", "--noOverheadEvaluation", "--disableLogFile",
                    "--maxWarmupCount", "8",
                    "--minIterationCount", "15000000", "--maxIterationCount", "20000000",
                    "-a", str(bdn_artifacts),
                ]
                target_process = "dotnet"

            proc = subprocess.Popen(
                bench_cmd, env=perf_env, cwd=common.DIR_BENCHAPP,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )

            common.post_log(f"[PERF]   Waiting 40s for warmup (PID={proc.pid})...")
            time.sleep(40)

            if proc.poll() is not None:
                common.post_log(f"[PERF]   Process exited early (code {proc.returncode}), skipping")
                continue

            pid = proc.pid
            perf_data = bench_dir / "perf.data"
            perf_small = bench_dir / "perf_small.data"

            # High-frequency perf record
            common.post_log(f"[PERF]   Recording high-freq (-F {high_freq}) for 5s...")
            common.run(f"perf record {perf_record_args} -k 1 -g -F {high_freq} -p {pid} -o {perf_data} sleep 5",
                       check=False)
            time.sleep(2)

            # Low-frequency perf record (for speedscope)
            common.post_log(f"[PERF]   Recording low-freq (-F {low_freq}) for 3s...")
            common.run(f"perf record {perf_record_args} -k 1 -g -F {low_freq} -p {pid} -o {perf_small} sleep 3",
                       check=False)
            time.sleep(2)

            # Perf stat
            stats_file = bench_dir / f"{label}.stats"
            common.run(f"perf stat -o {stats_file} -p {pid} sleep 6", check=False)

            # List perf counters
            perf_list_file = bench_dir / f"{label}.perf_list.txt"
            common.run("perf list", check=False, stdout_file=perf_list_file)

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
            common.run(f"perf inject --input {perf_data} --jit --output {perfjit}", check=False)
            common.run(f"perf inject --input {perf_small} --jit --output {perfjit_small}", check=False)

            # Function report
            functions_file = bench_dir / f"{label}_functions.txt"
            common.run(f"perf report --input {perfjit} --no-children --percent-limit 2 --stdio",
                       check=False, stdout_file=functions_file)

            # Hot assembly annotation
            asm_file = bench_dir / f"{label}.asm"
            common.run(f"perf annotate --stdio2 -i {perfjit} --percent-limit 2 -M intel",
                       check=False, stdout_file=asm_file)

            # Flamegraph (interactive SVG)
            svg_file = bench_dir / f"{label}_flamegraph.svg"
            common.run(f"perf script -i {perfjit} | "
                       f"{flamegraph_dir}/stackcollapse-perf.pl | "
                       f"{flamegraph_dir}/flamegraph.pl",
                       check=False, stdout_file=svg_file)

            # Speedscope (collapsed stacks)
            speedscope_file = bench_dir / f"speedscope_{label}_{common.CFG.job_id}.speedscope"
            common.run(f"perf script -i {perfjit_small} | "
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
