#!/usr/bin/env python3
"""
macOS-specific helpers for the EgorBot agent.

Exports:
    setup_platform()                — ensure Homebrew is on PATH
    install_platform_deps()         — brew install ninja
    prepare_runtime_for_profiling() — apply the temporary Samply jitdump fix
    validate_profiler_core_root()   — require runtime native symbol sidecars
    run_perf_profiling()            — Samply recording and report generation
"""

import glob as globmod
import os
import re
import shutil
import sys
import time
from pathlib import Path

# ── Injected by common module's load_platform_module() ──────────────────────
common = None  # type: ignore

SAMPLY_RUNTIME_PATCH_COMMIT = "c39a74945b15ced3f47bd24acd09933db1f918a2"
SAMPLY_TOP_FUNCTIONS = 20
SAMPLY_PROFILE_TIMEOUT_SECONDS = 20 * 60
SAMPLY_MEASUREMENT_ITERATIONS = 60
SAMPLY_WARMUP_ITERATIONS = 8

SAMPLY_JITDUMP_DECLARATION = """#if defined(TARGET_OSX)
// The runtime globally aliases open to open$NOCANCEL, but Samply discovers jitdump files by
// interposing the regular open symbol. Bind this call directly to that symbol.
extern "C" int JitDumpOpen(const char* path, int flags, ...) __asm("_open");
#else
#define JitDumpOpen open
#endif
"""


def setup_platform():
    """Ensure Homebrew is on PATH for macOS (Helix machines may not have it)."""
    for brew_dir in ("/opt/homebrew/bin", "/usr/local/bin"):
        if (
            os.path.isfile(os.path.join(brew_dir, "brew"))
            and brew_dir not in os.environ.get("PATH", "")
        ):
            os.environ["PATH"] = brew_dir + os.pathsep + os.environ.get("PATH", "")


def install_platform_deps():
    """Install build dependencies via Homebrew."""
    common.run("brew install ninja", check=False)


def prepare_runtime_for_profiling(runtime_dir: Path):
    """Apply the unmerged CoreCLR fix which lets Samply discover jitdump files."""
    source = (
        runtime_dir
        / "src"
        / "coreclr"
        / "pal"
        / "src"
        / "misc"
        / "perfjitdump.cpp"
    )
    if not source.is_file():
        raise RuntimeError(f"Samply runtime patch target is missing: {source}")

    text = source.read_text(encoding="utf-8")
    has_declaration = 'extern "C" int JitDumpOpen' in text
    has_call = "JitDumpOpen(jitdumpPath" in text
    if has_declaration and has_call:
        common.post_log(
            f"[SAMPLY] Runtime already contains the jitdump discovery fix "
            f"({SAMPLY_RUNTIME_PATCH_COMMIT})"
        )
        return
    if has_declaration != has_call:
        raise RuntimeError(
            f"Runtime has a partial Samply jitdump fix in {source}; refusing to build"
        )

    declaration_anchor = "SET_DEFAULT_DEBUG_CHANNEL(MISC);"
    if declaration_anchor not in text:
        raise RuntimeError(
            f"Could not find the declaration anchor in {source}; "
            f"the Samply patch no longer applies cleanly"
        )
    text = text.replace(
        declaration_anchor,
        SAMPLY_JITDUMP_DECLARATION + "\n" + declaration_anchor,
        1,
    )

    open_pattern = re.compile(
        r"\bresult\s*=\s*open\(\s*jitdumpPath\s*,\s*"
        r"O_CREAT\s*\|\s*O_TRUNC\s*\|\s*O_RDWR\s*\|\s*O_CLOEXEC\s*,\s*"
        r"S_IRUSR\s*\|\s*S_IWUSR\s*\);"
    )
    text, replacements = open_pattern.subn(
        "result = JitDumpOpen(jitdumpPath, "
        "O_CREAT|O_TRUNC|O_RDWR|O_CLOEXEC, S_IRUSR|S_IWUSR);",
        text,
        count=1,
    )
    if replacements != 1:
        raise RuntimeError(
            f"Could not find the jitdump open call in {source}; "
            f"the Samply patch no longer applies cleanly"
        )

    source.write_text(text, encoding="utf-8")
    relative_source = source.relative_to(runtime_dir)
    common.post_log(
        f"[SAMPLY] Applied runtime jitdump discovery fix "
        f"{SAMPLY_RUNTIME_PATCH_COMMIT} to {relative_source}"
    )
    check = common.run(
        ["git", "diff", "--check", "--", str(relative_source)],
        cwd=runtime_dir,
        shell=False,
        check=False,
    )
    if check.returncode != 0:
        raise RuntimeError("The temporary Samply runtime patch failed git diff --check")
    common.run(
        ["git", "diff", "--", str(relative_source)],
        cwd=runtime_dir,
        shell=False,
        check=False,
    )


def validate_profiler_core_root(core_root: Path) -> bool:
    """Require the runtime binaries and native symbols needed by Samply."""
    required = [
        "corerun",
        "libcoreclr.dylib",
        "libcoreclr.dylib.dwarf",
        "libclrjit.dylib",
        "libclrjit.dylib.dwarf",
        "libclrgc.dylib",
        "libclrgc.dylib.dwarf",
    ]
    missing = [name for name in required if not (core_root / name).is_file()]
    if missing:
        common.post_log(
            f"[SAMPLY] ERROR: Core_Root {core_root} is missing required files: "
            f"{', '.join(missing)}"
        )
        return False

    sizes = ", ".join(
        f"{name}={(core_root / name).stat().st_size}" for name in required
    )
    common.post_log(f"[SAMPLY] Core_Root validated: {core_root} ({sizes})")
    return True


def _safe_filename(value: str) -> str:
    return re.sub(r"[^a-zA-Z0-9_.-]", "_", value)


def _log_profile_directory(profile_dir: Path):
    files = []
    if profile_dir.is_dir():
        for path in sorted(profile_dir.iterdir()):
            if path.is_file():
                files.append(f"{path.name} ({path.stat().st_size} bytes)")
    common.post_log(
        f"[SAMPLY] Profile directory contents: "
        f"{', '.join(files) if files else '(empty)'}"
    )


def _log_status_file(status_file: Path):
    if not status_file.is_file():
        common.post_log(f"[SAMPLY] No run status file was produced at {status_file}")
        return {}
    text = status_file.read_text(encoding="utf-8", errors="replace").strip()
    common.post_log(f"[SAMPLY] Run status:\n{text or '(empty)'}")
    values = {}
    for line in text.splitlines():
        key, separator, value = line.partition("=")
        if separator:
            values[key] = value
    return values


def run_perf_profiling():
    """Record macOS mixed-stack profiles with Samply and generate final reports."""
    if common.CFG.bench_use_dotnet_performance:
        common.post_log(
            "[SAMPLY] Profiling is not supported for dotnet/performance benchmarks, "
            "skipping"
        )
        return

    profiler_script = Path(__file__).parent / "profile-samply.sh"
    report_script = Path(__file__).parent / "samply-report.py"
    if not profiler_script.is_file() or not report_script.is_file():
        common.post_log(
            f"[SAMPLY] ERROR: profiler payload is incomplete: "
            f"{profiler_script}, {report_script}"
        )
        return

    corerun_paths = sorted(
        globmod.glob(str(common.CORE_ROOTS_DIR / "*" / common.make_exe("corerun")))
    )
    if not corerun_paths:
        common.post_log(
            "[SAMPLY] ERROR: profiling requires patched Core_Root builds; "
            "no corerun executables were found"
        )
        return

    invalid_core_roots = [
        str(Path(path).parent)
        for path in corerun_paths
        if not validate_profiler_core_root(Path(path).parent)
    ]
    if invalid_core_roots:
        common.post_log(
            f"[SAMPLY] ERROR: cannot profile invalid Core_Roots: "
            f"{', '.join(invalid_core_roots)}"
        )
        return

    build = common.run(
        ["dotnet", "build", "-c", "Release", "-f", common.CFG.bench_tfm],
        cwd=common.DIR_BENCHAPP,
        shell=False,
        check=False,
    )
    if build.returncode != 0:
        common.post_log("[SAMPLY] Failed to build benchmark app, skipping profiling")
        return

    runtime_nuget = common.WORK_DIR / "runtime" / "NuGet.config"
    if runtime_nuget.is_file():
        shutil.copy2(runtime_nuget, common.DIR_BENCHAPP / "NuGet.config")

    bench_dll = (
        common.DIR_BENCHAPP
        / "bin"
        / "Release"
        / common.CFG.bench_tfm
        / "benchapp.dll"
    )
    if not bench_dll.is_file():
        common.post_log(f"[SAMPLY] Benchmark DLL not found at {bench_dll}, skipping")
        return

    all_benchmarks_file = common.WORK_DIR / "all_benchmarks.txt"
    if not all_benchmarks_file.is_file():
        common.post_log(
            f"[SAMPLY] Benchmark list not found at {all_benchmarks_file}, skipping"
        )
        return
    benchmarks = [
        line.strip()
        for line in all_benchmarks_file.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(benchmarks) > 5:
        common.post_log(
            f"[SAMPLY] Too many benchmarks ({len(benchmarks)} > 5) for profiling, "
            "skipping"
        )
        return

    if common.CFG.perf_record_args:
        common.post_log(
            f"[SAMPLY] Ignoring Linux-only perf record arguments: "
            f"{common.CFG.perf_record_args}"
        )
    if common.CFG.perf_stat_events:
        common.post_log(
            f"[SAMPLY] Ignoring Linux-only perf stat events on macOS: "
            f"{common.CFG.perf_stat_events}"
        )

    artifact_root = common.ARTIFACTS_DIR / "perf"
    raw_root = common.WORK_DIR / "samply_profiles"
    common.ensure_dirs(artifact_root, raw_root)
    tools_dir = common.WORK_DIR / "runtime" / "artifacts" / "tools"
    completed = 0
    expected = len(corerun_paths) * len(benchmarks)

    common.post_log(
        f"[SAMPLY] Starting {expected} direct-launch profile run(s): "
        f"{len(corerun_paths)} runtime(s), {len(benchmarks)} benchmark(s), "
        f"{SAMPLY_WARMUP_ITERATIONS} warmups, "
        f"{SAMPLY_MEASUREMENT_ITERATIONS} measured iterations"
    )
    common.post_log(
        f"[SAMPLY] Samply cache: {tools_dir}; raw profiles: {raw_root}; "
        f"final artifacts: {artifact_root}"
    )

    for corerun_path in corerun_paths:
        label = Path(corerun_path).parent.name
        label_filename = _safe_filename(label)
        for benchmark in benchmarks:
            benchmark_filename = _safe_filename(benchmark)
            artifact_dir = artifact_root / f"PerfBench__{benchmark_filename}"
            profile_dir = raw_root / benchmark_filename / label_filename
            if profile_dir.exists():
                shutil.rmtree(profile_dir)
            common.ensure_dirs(artifact_dir, profile_dir)

            common.post_log(f"[SAMPLY] Profiling: {label} / {benchmark}")
            common.kill_process_by_name("corerun")
            common.kill_process_by_name("dotnet")
            time.sleep(3)

            common.sync_roslyn_into_core_root(
                Path(corerun_path).parent, bench_dll.parent
            )
            bdn_artifacts = profile_dir / "bdn_scratch"
            bdn_args = [
                "--filter",
                benchmark,
                "-i",
                "--noForcedGCs",
                "--disableLogFile",
                "--warmupCount",
                str(SAMPLY_WARMUP_ITERATIONS),
                "--iterationCount",
                str(SAMPLY_MEASUREMENT_ITERATIONS),
                "-a",
                str(bdn_artifacts),
            ]
            command = [
                "bash",
                str(profiler_script),
                str(corerun_path),
                str(bench_dll),
                *bdn_args,
            ]
            profile_env = {
                "PROFILE_OUT": str(profile_dir),
                "TOP": str(SAMPLY_TOP_FUNCTIONS),
                "PYTHON_BIN": sys.executable,
                "SAMPLY_TOOLS_DIR": str(tools_dir),
                "SAMPLY_PROFILE_NAME": f"{label} / {benchmark}",
            }
            result = common.run(
                command,
                cwd=common.DIR_BENCHAPP,
                env=profile_env,
                shell=False,
                check=False,
                timeout_seconds=SAMPLY_PROFILE_TIMEOUT_SECONDS,
            )

            common.kill_process_by_name("corerun")
            time.sleep(2)
            status_file = profile_dir / "run-status.txt"
            run_status = _log_status_file(status_file)
            _log_profile_directory(profile_dir)

            speedscope_source = profile_dir / "flamegraph.speedscope.json"
            assembly_source = profile_dir / "annotated-asm.txt"
            outputs_exist = speedscope_source.is_file() and assembly_source.is_file()
            report_succeeded = (
                run_status.get("report_exit") == "0"
                if "report_exit" in run_status
                else result.returncode == 0
            )
            outputs_valid = outputs_exist and report_succeeded
            if outputs_valid:
                speedscope_dest = (
                    artifact_dir / f"{label_filename}.flamegraph.speedscope.json"
                )
                assembly_dest = artifact_dir / f"{label_filename}.annotated-asm.txt"
                shutil.copy2(speedscope_source, speedscope_dest)
                shutil.copy2(assembly_source, assembly_dest)
                completed += 1
                common.post_log(
                    f"[SAMPLY] Final reports copied for {label} / {benchmark}: "
                    f"{speedscope_dest.name} ({speedscope_dest.stat().st_size} bytes), "
                    f"{assembly_dest.name} ({assembly_dest.stat().st_size} bytes)"
                )
            elif not outputs_exist:
                common.post_log(
                    f"[SAMPLY] ERROR: final reports are missing for {label} / {benchmark}"
                )
            else:
                common.post_log(
                    f"[SAMPLY] ERROR: reports for {label} / {benchmark} failed "
                    "validation and will not be uploaded"
                )

            if result.returncode != 0:
                common.post_log(
                    f"[SAMPLY] WARNING: profile wrapper exited with code "
                    f"{result.returncode} for {label} / {benchmark}"
                )
                if status_file.is_file():
                    shutil.copy2(
                        status_file,
                        artifact_dir / f"{label_filename}.samply-diagnostics.txt",
                    )
            elif outputs_valid:
                shutil.rmtree(profile_dir, ignore_errors=True)

    common.post_log(
        f"[SAMPLY] Profiling completed: "
        f"{completed}/{expected} runs produced both reports"
    )
