#!/usr/bin/env python3
"""
Cross-platform agent for the EgorBot benchmark service.

Builds dotnet/runtime core_roots for specified commits/PRs and runs BDN
microbenchmarks against them.  Requires only the Python 3 standard library.

Utility functions live in ``bdn-benchmarking-common-helpers.py``.
Platform-specific helpers live in ``bdn-benchmarking-{windows,linux,macos}.py``.
"""

import argparse
import glob as globmod
import importlib.util
import os
import re as re_mod
import shlex
import shutil
import subprocess
import sys
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional


# =============================================================================
#  Load the helpers module from the same directory
# =============================================================================

def _load_helpers():
    script_dir = Path(__file__).parent
    mod_path = script_dir / "bdn-benchmarking-common-helpers.py"
    spec = importlib.util.spec_from_file_location("bdn_benchmarking_helpers", mod_path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod

_helpers = _load_helpers()
_helpers.set_common_ref(sys.modules[__name__])

# Re-export helper functions so that:
#   1) Pipeline code in this module can call them without a prefix.
#   2) Platform modules (which receive this module as ``common``) can call
#      common.run(), common.post_log(), etc.
run                   = _helpers.run
post_log              = _helpers.post_log
download              = _helpers.download
read_lines            = _helpers.read_lines
zip_directory         = _helpers.zip_directory
sed_replace           = _helpers.sed_replace
ensure_dirs           = _helpers.ensure_dirs
copy_glob             = _helpers.copy_glob
detect_platform       = _helpers.detect_platform
is_unix               = _helpers.is_unix
make_exe              = _helpers.make_exe
make_script           = _helpers.make_script
dotnet_install_cmd    = _helpers.dotnet_install_cmd
load_platform_module  = _helpers.load_platform_module
load_sibling_module   = _helpers.load_sibling_module
kill_process_by_name  = _helpers.kill_process_by_name
start_callback_sender = _helpers.start_callback_sender
stop_callback_sender  = _helpers.stop_callback_sender
send_results          = _helpers.send_results


def __getattr__(name):
    """Fall back to the helpers module for any attribute not explicitly
    defined here (e.g. dynamic globals accessed by platform modules)."""
    try:
        return getattr(_helpers, name)
    except AttributeError:
        raise AttributeError(f"module {__name__!r} has no attribute {name!r}") from None


# =============================================================================
# Example usage:
#   (1) With custom benchmark snippet:
#       python bdn-benchmarking-common.py --job_tag my_test1 \
#           --gh_commits_and_prs "PR_12345;main" --bench_code_file ./MyBenchmark.cs
#
#   (2) With dotnet/performance benchmarks for a27de4a and its previous commits:
#       python bdn-benchmarking-common.py --job_tag my_test2 \
#           --gh_commits_and_prs "a27de4a;a27de4a~1;a27de4a~2"
#
# BDN arguments are read from BDN_ARGS.rsp file from current dir.
# =============================================================================

@dataclass
class Config:
    work_dir: str
    job_tag: str
    benchmark_kind: str
    gh_commits_and_prs: List[str]
    bench_code_file: str
    bench_csproj_file: str
    bench_tfm: str
    runtime_build_args: str
    bdn_args_file: str
    perf_enabled: bool
    perf_record_args: str
    perf_record_freq: str
    perf_stat_events: str
    callback_url: str
    job_id: str
    skip_deps: bool
    attempts: int
    orchard_warmup: int
    orchard_rounds: int
    orchard_round_duration: int
    orchard_processes: int
    orchard_connections: int

    @property
    def is_orchard(self) -> bool:
        """OrchardCore CMS macro-benchmark instead of the BDN pipeline."""
        return self.benchmark_kind == "orchard"

    @property
    def bench_use_dotnet_performance(self) -> bool:
        """Use dotnet/performance benchmark suite when bench_code_file is empty.
        We'll rely on --filter arg from BDN_ARGS.rsp."""
        return self.bench_code_file == ""

    @staticmethod
    def parse_args(argv: Optional[List[str]] = None) -> "Config":
        """Parse CLI arguments (falling back to env vars, then defaults)."""
        p = argparse.ArgumentParser(
            description="Build dotnet/runtime core_roots and run BDN benchmarks.",
            formatter_class=argparse.RawDescriptionHelpFormatter,
        )

        p.add_argument("--work_dir", default=os.path.dirname(os.path.abspath(__file__)),
                        help="Working directory (default: script directory)")
        p.add_argument("--job_tag", default="test",
                        help="Name to distinguish artifacts (default: test)")
        p.add_argument("--benchmark_kind", choices=["bdn", "orchard"], default="bdn",
                        help="bdn = BenchmarkDotNet microbenchmarks, "
                             "orchard = OrchardCore CMS throughput benchmark (Linux only)")
        p.add_argument("--gh_commits_and_prs",
                        default="",
                        help='Semicolon-separated commits/PRs to compare. PRs prefixed with "PR_", '
                             'e.g. PR_12345;main  (default: empty)')
        p.add_argument("--bench_code_file",
                        default="",
                        help="Local path to .cs benchmark file (empty = use dotnet/performance)")
        p.add_argument("--bench_csproj_file",
                        default="",
                        help="Local path to .csproj template for custom benchmarks")
        p.add_argument("--bench_tfm", default="net11.0",
                        help="Target framework moniker (default: net11.0)")
        p.add_argument("--runtime_build_args", default="/p:NoPgoOptimize=true",
                        help='Extra args for build.sh/build.cmd (default: /p:NoPgoOptimize=true)')
        p.add_argument("--bdn_args_file",
                        default="",
                        help="Local path to BDN arguments .rsp file (default: downloads a default one)")
        p.add_argument("--perf_enabled", type=int, choices=[0, 1],
                        default=0,
                        help="1 = enable perf recording (default: 0)")
        p.add_argument("--perf_record_args", default="",
                        help="Extra args for perf record")
        p.add_argument("--perf_record_freq", default="4999",
                        help="perf record -F frequency (default: 4999)")
        p.add_argument("--perf_stat_events", default="",
                        help="Comma-separated events for 'perf stat -e' (default: a generic set). "
                             "See the perf_events.txt artifact for what the machine supports.")
        p.add_argument("--callback_url", default="",
                        help="Base URL of EgorBot service for posting logs/results (e.g. http://host:5000/api/internal)")
        p.add_argument("--job_id", default="",
                        help="Job ID assigned by the EgorBot service")
        p.add_argument("--skip_deps", type=int, choices=[0, 1],
                        default=0,
                        help="1 = skip dependency and .NET SDK installation (default: 0)")
        p.add_argument("--attempts", type=int, default=1,
                        help="Number of times to run all benchmarks (default: 1)")
        p.add_argument("--orchard_warmup", type=int, default=90,
                        help="[orchard] Warmup load duration in seconds (default: 90)")
        p.add_argument("--orchard_rounds", type=int, default=3,
                        help="[orchard] Measured intervals per server process (default: 3)")
        p.add_argument("--orchard_round_duration", type=int, default=15,
                        help="[orchard] Duration of one measured interval in seconds (default: 15)")
        p.add_argument("--orchard_processes", type=int, default=2,
                        help="[orchard] Server restarts per runtime — captures process-to-process "
                             "noise (default: 2)")
        p.add_argument("--orchard_connections", type=int, default=0,
                        help="[orchard] Load generator connections (default: 0 = 8 per app core)")

        args = p.parse_args(argv)

        return Config(
            work_dir=args.work_dir,
            job_tag=args.job_tag,
            benchmark_kind=args.benchmark_kind,
            gh_commits_and_prs=[s.strip() for s in args.gh_commits_and_prs.split(";") if s.strip()],
            bench_code_file=args.bench_code_file,
            bench_csproj_file=args.bench_csproj_file,
            bench_tfm=args.bench_tfm,
            runtime_build_args=args.runtime_build_args,
            bdn_args_file=args.bdn_args_file,
            perf_enabled=bool(args.perf_enabled),
            perf_record_args=args.perf_record_args,
            perf_record_freq=args.perf_record_freq,
            perf_stat_events=args.perf_stat_events,
            callback_url=args.callback_url,
            job_id=args.job_id,
            skip_deps=bool(args.skip_deps),
            attempts=max(1, args.attempts),
            orchard_warmup=max(1, args.orchard_warmup),
            orchard_rounds=max(1, args.orchard_rounds),
            orchard_round_duration=max(1, args.orchard_round_duration),
            orchard_processes=max(1, args.orchard_processes),
            orchard_connections=max(0, args.orchard_connections),
        )


# -- Module-level globals (set in setup_environment) -------------------------
WORK_DIR: Path
ARTIFACTS_DIR: Path
DIR_BENCHAPP: Path
CORE_ROOTS_DIR: Path
TARGET_OS: str
TARGET_ARCH: str
CFG: Config
_platform_mod = None


# =============================================================================
#  Stage 1 -- Environment setup
# =============================================================================

def setup_environment(cfg: Config):
    """Detect platform, create working directories, set global .NET env vars."""
    global WORK_DIR, ARTIFACTS_DIR, DIR_BENCHAPP, CORE_ROOTS_DIR
    global TARGET_OS, TARGET_ARCH, CFG, _platform_mod

    CFG = _helpers.CFG = cfg
    WORK_DIR = _helpers.WORK_DIR = Path(cfg.work_dir).resolve()
    os.chdir(WORK_DIR)

    TARGET_OS, TARGET_ARCH = detect_platform()
    _helpers.TARGET_OS = TARGET_OS
    _helpers.TARGET_ARCH = TARGET_ARCH

    # Load platform-specific module
    _platform_mod = load_platform_module(TARGET_OS)
    _helpers._platform_mod = _platform_mod
    if _platform_mod and hasattr(_platform_mod, "setup_platform"):
        _platform_mod.setup_platform()

    ARTIFACTS_DIR  = _helpers.ARTIFACTS_DIR  = WORK_DIR / "artifacts"
    DIR_BENCHAPP   = _helpers.DIR_BENCHAPP   = WORK_DIR / "benchapp"
    CORE_ROOTS_DIR = _helpers.CORE_ROOTS_DIR = WORK_DIR / "core_roots"
    ensure_dirs(ARTIFACTS_DIR, DIR_BENCHAPP, CORE_ROOTS_DIR)

    # Some global env vars for .NET
    os.environ["DOTNET_JitEnableOptionalRelocs"] = "0"  # Improve consistency of measurements
    os.environ["DOTNET_EnableWriteXorExecute"]   = "0"  # Not sure this affects consistency, improves perf record
    os.environ["PERFLAB_TARGET_FRAMEWORKS"]      = cfg.bench_tfm


# =============================================================================
#  Stage 2 -- Install dependencies
# =============================================================================

def install_dependencies():
    # On local runs (callback to localhost), don't kill dotnet -- it would kill the web server!
    if not CFG.callback_url or "localhost" not in CFG.callback_url:
        kill_process_by_name("dotnet")
    else:
        post_log("Skipping dotnet kill (local mode, would kill the web server)")

    marker = WORK_DIR / ".deps_installed"
    if marker.exists():
        post_log("Dependencies already installed, skipping installation")
        return

    post_log(f"Installing dependencies for {TARGET_OS}...")

    if _platform_mod and hasattr(_platform_mod, "install_platform_deps"):
        _platform_mod.install_platform_deps()
    else:
        post_log(f"WARNING: No platform module or install_platform_deps for {TARGET_OS}")

    marker.touch()


# =============================================================================
#  Stage 3 -- Install .NET SDKs
# =============================================================================

def install_dotnet_sdks():
    script_name = "dotnet-install.ps1" if TARGET_OS == "windows" else "dotnet-install.sh"
    script_path = WORK_DIR / script_name

    if not script_path.exists():
        post_log(f"Downloading {script_name}...")
        download(f"https://dot.net/v1/{script_name}", script_path)
        if TARGET_OS != "windows":
            script_path.chmod(0o755)

        post_log("Installing .NET 11.0 daily...")
        install_dir = str(WORK_DIR / ".dotnet")
        run(dotnet_install_cmd(script_path, "--channel", "11.0", "--quality", "daily",
                               "--install-dir", install_dir))
        post_log("Installing .NET 10.0...")
        run(dotnet_install_cmd(script_path, "--channel", "10.0",
                               "--install-dir", install_dir))
    else:
        post_log("dotnet-install script already present, skipping download")

    dotnet_root = str(WORK_DIR / ".dotnet")
    os.environ["DOTNET_ROOT"] = dotnet_root
    os.environ["PATH"] = os.pathsep.join([
        dotnet_root,
        os.path.join(dotnet_root, "tools"),
        os.environ.get("PATH", ""),
    ])
    # Don't allow NuGet to use HOME directory for cache:
    os.environ["NUGET_PLUGINS_CACHE_PATH"] = os.path.join(dotnet_root, "NUGET_PLUGINS_CACHE_PATH")
    os.environ["NUGET_PACKAGES"]           = os.path.join(dotnet_root, "NUGET_PACKAGES")
    os.environ["NUGET_HTTP_CACHE_PATH"]    = os.path.join(dotnet_root, "NUGET_HTTP_CACHE_PATH")
    os.environ["NUGET_SCRATCH"]            = os.path.join(dotnet_root, "NUGET_SCRATCH")
    os.environ["DOTNET_NUGET_SIGNATURE_VERIFICATION"] = "false"

    # Create nuget.config with dotnet CI feeds (needed on Helix / isolated machines)
    nuget_config = WORK_DIR / "nuget.config"
    if not nuget_config.exists():
        nuget_config.write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n'
            '<configuration>\n'
            '  <packageSources>\n'
            '    <clear />\n'
            '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />\n'
            '    <add key="dotnet-public" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json" />\n'
            '    <add key="dotnet-libraries" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-libraries/nuget/v3/index.json" />\n'
            '    <add key="dotnet11" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet11/nuget/v3/index.json" />\n'
            '  </packageSources>\n'
            '</configuration>\n',
            encoding="utf-8",
        )
        post_log("Created nuget.config with dotnet CI feeds")


# =============================================================================
#  Stage 4 -- Build & prepare benchmarks
# =============================================================================

def build_benchmarks(bench_args: List[str]):
    if CFG.bench_use_dotnet_performance:
        post_log("Building benchmarks from dotnet/performance repo...")
        _build_dotnet_performance_benchmarks(bench_args)
    else:
        post_log("Building custom benchmarks...")
        _build_custom_benchmarks(bench_args)

    # Validate the discovered benchmark list
    all_benchmarks = WORK_DIR / "all_benchmarks.txt"
    text = all_benchmarks.read_text(encoding="utf-8")
    lines = [l for l in text.splitlines() if l.strip()]

    for line in lines[-100:]:
        print(line)

    if "USAGE:" in text:
        print("\u274c Benchmark discovery failed")
        send_results(success=False, exit_code=1)
    if len(lines) > 50:
        print(text)
        print(f"\u274c Too many benchmarks discovered: {len(lines)}.")
        send_results(success=False, exit_code=1)
    if len(lines) == 0:
        print("\u274c No benchmarks discovered with the provided arguments.")
        send_results(success=False, exit_code=1)

    print(f"Discovered {len(lines)} benchmarks.")


def _build_dotnet_performance_benchmarks(bench_args: List[str]):
    """Clone dotnet/performance and build the MicroBenchmarks suite."""
    perf_dir = WORK_DIR / "performance"
    if not perf_dir.is_dir():
        run(f'git clone --no-tags --single-branch --depth 1 '
            f'https://github.com/dotnet/performance "{perf_dir}"')

    # Install SDK version from performance repo's global.json
    script = WORK_DIR / ("dotnet-install.ps1" if TARGET_OS == "windows" else "dotnet-install.sh")
    run(dotnet_install_cmd(script, "--jsonfile",
                           str(perf_dir / "global.json"), "-i", str(WORK_DIR / ".dotnet")))

    sed_replace(
        perf_dir / "src" / "Directory.Build.props",
        "<TreatWarningsAsErrors>True</TreatWarningsAsErrors>",
        "<TreatWarningsAsErrors>False</TreatWarningsAsErrors>",
    )

    micro_dir = perf_dir / "src" / "benchmarks" / "micro"
    # dotnet build is not working for some reason, use dotnet run to restore and build the project
    run(f'dotnet run -c Release -f {CFG.bench_tfm} -- --list flat', cwd=micro_dir, check=False)

    micro_bin = (perf_dir / "artifacts" / "bin" / "MicroBenchmarks"
                 / "Release" / CFG.bench_tfm / "MicroBenchmarks")
    all_benchmarks = WORK_DIR / "all_benchmarks.txt"
    run([str(micro_bin)] + bench_args + ["--list", "flat"],
        cwd=micro_dir, stdout_file=all_benchmarks, shell=False, check=False)


def _build_custom_benchmarks(bench_args: List[str]):
    """Create a small BDN project from the gist snippet and build it."""
    csproj = DIR_BENCHAPP / "benchapp.csproj"
    if not csproj.exists():
        run(f"dotnet new console -f {CFG.bench_tfm}", cwd=DIR_BENCHAPP)
        shutil.copy2(CFG.bench_code_file, DIR_BENCHAPP / "Program.cs")
        if CFG.bench_csproj_file:
            shutil.copy2(CFG.bench_csproj_file, csproj)
        else:
            download("https://gist.github.com/EgorBo/c3378873ad204ebf522a07138f621128/raw", csproj)

        # Always target net10 and net11 so BDN can run either via --runtimes
        # (a lower-TFM benchapp can be referenced by any equal-or-newer runtime job).
        csproj_text = csproj.read_text(encoding="utf-8")
        csproj_text = re_mod.sub(
            r'<TargetFrameworks?>[^<]+</TargetFrameworks?>',
            '<TargetFrameworks>net10.0;net11.0</TargetFrameworks>',
            csproj_text,
        )
        csproj.write_text(csproj_text, encoding="utf-8")

        # Auto-detect whether the snippet already has an entrypoint
        snippet_text = (DIR_BENCHAPP / "Program.cs").read_text(encoding="utf-8")
        has_entrypoint = (
            re_mod.search(r'\bstatic\s+(?:async\s+)?(?:void|int|Task(?:<int>)?)\s+Main\s*\(', snippet_text)
            or re_mod.search(r'\b(?:BenchmarkSwitcher|BenchmarkRunner)\b', snippet_text)
        )
        if has_entrypoint:
            post_log("Detected entrypoint in benchmark snippet, skipping Entrypoint.cs generation")
        else:
            post_log("No entrypoint detected in benchmark snippet, adding Entrypoint.cs")
            (DIR_BENCHAPP / "Entrypoint.cs").write_text(
                'BenchmarkDotNet.Running.BenchmarkSwitcher.FromAssembly('
                'typeof(ThisAsmType).Assembly).Run(args); class ThisAsmType {}\n',
                encoding="utf-8",
            )

        run(f"dotnet build -c Release -f {CFG.bench_tfm} -o test", cwd=DIR_BENCHAPP)

    # Always (re)generate the benchmark list if it's missing
    all_benchmarks = WORK_DIR / "all_benchmarks.txt"
    if not all_benchmarks.exists() or all_benchmarks.stat().st_size == 0:
        bench_dll = os.path.join("test", "benchapp.dll")
        run(["dotnet", bench_dll] + bench_args + ["--list", "flat"],
            cwd=DIR_BENCHAPP, stdout_file=all_benchmarks, shell=False)


# =============================================================================
#  Stage 5 -- Build core_roots for all commits/PRs
# =============================================================================

def clone_runtime():
    runtime_dir = WORK_DIR / "runtime"
    if not runtime_dir.is_dir():
        post_log("Cloning dotnet/runtime...")
        # Enable long paths on Windows -- dotnet/runtime has files that exceed the 260-char limit
        run('git config --global core.longpaths true')
        run(f'git clone --no-tags --single-branch '
                    f'https://github.com/dotnet/runtime.git "{runtime_dir}"', check=False)
        run('git config --global user.email egorbot@egorbo.com', cwd=runtime_dir)
        run('git config --global user.name egorbot', cwd=runtime_dir)
    else:
        post_log("dotnet/runtime already cloned")

def _expand_commit_ranges(items: List[str], runtime_dir: Path) -> List[str]:
    """
    Expand SHA1..SHA2 range entries into individual commit hashes using git log.
    Non-range entries (plain commits, PRs, 'main') are passed through as-is.
    Each range is capped at 10 commits.
    """
    result = []
    for item in items:
        if ".." in item and not item.startswith("PR_"):
            post_log(f"Expanding commit range: {item}")
            # Normalize ... to .. for git log (both mean "commits between" for our purposes)
            range_expr = item.replace("...", "..")
            # Ensure full history is available for range resolution
            run("git fetch --unshallow origin || git fetch origin", cwd=runtime_dir, check=False)
            proc = subprocess.run(
                f"git log --format=%H --reverse {range_expr}",
                cwd=runtime_dir, shell=True,
                capture_output=True, text=True,
            )
            if proc.returncode != 0 or not proc.stdout.strip():
                post_log(f"Failed to expand range '{item}': {proc.stderr.strip()}")
                send_results(success=False, exit_code=1)
            commits = [c.strip() for c in proc.stdout.strip().splitlines() if c.strip()]
            if len(commits) > 10:
                post_log(f"Range '{item}' has {len(commits)} commits (max 10), truncating to last 10")
                commits = commits[-10:]
            post_log(f"  Expanded to {len(commits)} commits: {[c[:8] for c in commits]}")
            result.extend(commits)
        else:
            result.append(item)
    return result


def build_core_roots():
    runtime_dir = WORK_DIR / "runtime"
    clone_runtime()

    # Expand any SHA1..SHA2 ranges into individual commits
    CFG.gh_commits_and_prs = _expand_commit_ranges(CFG.gh_commits_and_prs, runtime_dir)

    for item in CFG.gh_commits_and_prs:
        post_log(f"Building core_root for '{item}'...")
        core_root_dest = CORE_ROOTS_DIR / item
        if core_root_dest.is_dir():
            print(f"Directory {core_root_dest} already exists, skipping")
            continue

        is_pr = item.startswith("PR_")
        pr_number = item[3:] if is_pr else ""
        commit = "" if is_pr else item

        run("git stash", cwd=runtime_dir)

        # Some ugly logic to switch to the commit/pr/main
        print(f"Fetching and merging {item}...")

        if is_pr:
            run(f"git fetch origin pull/{pr_number}/head:pr-{pr_number}", cwd=runtime_dir)
            run(f"git merge pr-{pr_number} --no-commit --no-ff", cwd=runtime_dir)
        elif commit:
            if commit == "main":
                run("git checkout main", cwd=runtime_dir)
                run("git pull origin main", cwd=runtime_dir)
            else:
                # Short commit hashes can't be fetched as refs -- fetch full
                # history (unshallow if needed) then checkout locally.
                run("git fetch --unshallow origin || git fetch origin", cwd=runtime_dir, check=False)
                run(f"git checkout {commit}", cwd=runtime_dir)

        # Install deps via runtime's own script (most deps come from here)
        if TARGET_OS != "windows":
            # The script calls apt-get internally; prefix with sudo if not root.
            # -n so a machine whose sudo asks for a password fails fast instead of
            # blocking on /dev/tty until the job times out.
            prefix = ""
            if TARGET_OS != "osx" and os.getuid() != 0 and shutil.which("sudo"):
                prefix = "sudo -n "
            run(f"{prefix}eng/common/native/./install-dependencies.sh", cwd=runtime_dir, check=False)

        # Make it more resilient to warnings in case if we build old commits
        dbp = runtime_dir / "Directory.Build.props"
        if dbp.exists():
            sed_replace(dbp,
                        "<NoWarn>$(NoWarn);CS8500;CS8969</NoWarn>",
                        "<NoWarn>$(NoWarn);CS8500;CS8969;NU1903</NoWarn>")
            sed_replace(dbp,
                        "<TreatWarningsAsErrors Condition=\"'$(TreatWarningsAsErrors)' == ''\">true</TreatWarningsAsErrors>",
                        "<TreatWarningsAsErrors>false</TreatWarningsAsErrors>")

        print("=" * 82)
        print(f"  Building runtime for {item}...")
        print("=" * 82)
        arch_flag = f" -a {TARGET_ARCH}" if TARGET_ARCH != "x64" else ""
        # src/tests/build.sh|cmd takes the arch name directly (no -a prefix)
        tests_arch_flag = f" {TARGET_ARCH}" if TARGET_ARCH != "x64" else ""
        run(f"{make_script('build')} clr+libs -c Release{arch_flag} {CFG.runtime_build_args}", cwd=runtime_dir)

        if TARGET_OS == "windows":
            run(f"src\\tests\\build.cmd{tests_arch_flag} Release generatelayoutonly /p:BuildNativeTests=false", cwd=runtime_dir)
        else:
            run(f"./src/tests/build.sh{tests_arch_flag} Release generatelayoutonly /p:BuildNativeTests=false", cwd=runtime_dir)

        print("Successfully built runtime")
        post_log(f"Core_root built for '{item}' ✓")

        # Copy Core_Root to our core_roots folder
        core_root_src = (runtime_dir / "artifacts" / "tests" / "coreclr"
                         / f"{TARGET_OS}.{TARGET_ARCH}.Release" / "Tests" / "Core_Root")
        shutil.copytree(str(core_root_src), str(core_root_dest))

        run(f"{make_script('dotnet')} build-server shutdown", cwd=runtime_dir)
        kill_process_by_name("dotnet")


# =============================================================================
#  Stage 6 -- Run benchmarks
# =============================================================================

def run_benchmarks(bench_args: List[str], attempt: int = 1, total_attempts: int = 1):
    """Run BDN benchmarks using all built core_roots (or without --corerun if none).
    When total_attempts > 1, result files are suffixed with the attempt number
    so that multiple runs don't overwrite each other."""
    # Gather all corerun paths (one per commit/PR)
    corerun_paths = sorted(globmod.glob(
        str(CORE_ROOTS_DIR / "*" / make_exe("corerun"))
    ))
    post_log(f"Running benchmarks with {len(corerun_paths)} corerun(s): {corerun_paths}")
    hide_columns = ["-h", "Job", "StdDev", "RatioSD", "Median", "Min", "Max"]

    # Build the --corerun portion only when we actually have core_roots
    corerun_args = ["--corerun"] + corerun_paths if corerun_paths else []

    if CFG.bench_use_dotnet_performance:
        # Run benchmarks from dotnet/performance repo
        micro_dir = WORK_DIR / "performance" / "src" / "benchmarks" / "micro"
        micro_bin = (WORK_DIR / "performance" / "artifacts" / "bin"
                     / "MicroBenchmarks" / "Release" / CFG.bench_tfm / "MicroBenchmarks")
        run([str(micro_bin)] + bench_args + corerun_args + hide_columns,
            cwd=micro_dir, shell=False)
        results_dir = (
            WORK_DIR / "performance" / "artifacts" / "bin" / "MicroBenchmarks"
            / "Release" / CFG.bench_tfm / "BenchmarkDotNet.Artifacts" / "results"
        )
    else:
        # Run custom benchmarks
        run(["dotnet", "run", "-c", "Release", "-f", CFG.bench_tfm, "--"] +
            corerun_args + bench_args + hide_columns,
            cwd=DIR_BENCHAPP, shell=False)
        results_dir = DIR_BENCHAPP / "BenchmarkDotNet.Artifacts" / "results"

    results_pattern = str(results_dir / "*.*")

    # BDN's EventPipeProfiler puts .speedscope.json files in the parent
    # (BenchmarkDotNet.Artifacts/) not in results/, so copy those too.
    bdn_artifacts_dir = results_dir.parent
    speedscope_pattern = str(bdn_artifacts_dir / "*.speedscope.json")

    if total_attempts > 1:
        # Rename result files with an attempt *prefix* before copying to ARTIFACTS_DIR,
        # so multiple attempts don't overwrite each other.
        # A suffix would break the server-side matching, which keys off the file
        # endings ("-report-github.md", ".speedscope.json").
        # e.g. "MyBench-report-github.md" → "attempt2-MyBench-report-github.md"
        for f in globmod.glob(results_pattern) + globmod.glob(speedscope_pattern):
            p = Path(f)
            dest = ARTIFACTS_DIR / f"attempt{attempt}-{p.name}"
            shutil.copy2(str(p), str(dest))
        # Also clean BDN results dir so next attempt starts fresh
        for f in globmod.glob(results_pattern) + globmod.glob(speedscope_pattern):
            os.remove(f)
    else:
        copy_glob(results_pattern, ARTIFACTS_DIR)
        copy_glob(speedscope_pattern, ARTIFACTS_DIR)


# =============================================================================
#  Stage 6 (alternative) -- OrchardCore CMS throughput benchmark
# =============================================================================

def run_orchard(cfg: Config):
    """Run the OrchardCore macro-benchmark instead of the BDN pipeline.

    Stages 1-3 (environment, dependencies, SDKs) are shared; the benchmark itself
    lives in ``orchard-benchmarking.py``.
    """
    if cfg.gh_commits_and_prs:
        post_log("[STAGE 4/5] Building core_roots for all commits/PRs...")
        build_core_roots()
        post_log("[STAGE 4/5] Core_roots built ✓")
    else:
        # Only reachable when the agent is driven manually: the service always
        # requires commits/PRs for this benchmark kind.
        post_log("[STAGE 4/5] No commits/PRs specified -- running on the installed SDK runtime")

    post_log("[STAGE 5/5] Running the OrchardCore benchmark...")
    orchard = load_sibling_module("orchard-benchmarking.py", "orchard_benchmarking")
    if orchard is None:
        raise RuntimeError("orchard-benchmarking.py is missing from the agent payload")
    orchard.run_orchard_benchmarks()
    post_log("[STAGE 5/5] OrchardCore benchmark completed ✓")

    post_log("Finalizing -- uploading results...")
    send_results(success=True)


# =============================================================================
#  Entry point
# =============================================================================

def main(cfg: Optional[Config] = None):
    """Run the full pipeline. Pass a Config directly, or leave as None
    to parse CLI args (which fall back to env vars, then defaults)."""
    if cfg is None:
        cfg = Config.parse_args()

    setup_environment(cfg)

    # Start background log sender if callback is configured
    start_callback_sender()

    cpu_count = os.cpu_count() or "?"
    post_log(f"[STAGE 1/6] Environment set up. OS={TARGET_OS}, Arch={TARGET_ARCH}, CPUs={cpu_count}, WorkDir={WORK_DIR}")
    post_log(f"  Kind: {cfg.benchmark_kind}")
    post_log(f"  Commits/PRs: {cfg.gh_commits_and_prs}")
    post_log(f"  BenchCodeFile: {cfg.bench_code_file or '(none)'}")
    post_log(f"  Callback: {cfg.callback_url or '(none)'}, JobId: {cfg.job_id or '(none)'}")

    bench_args: List[str] = []
    if cfg.is_orchard:
        # The OrchardCore benchmark is a fixed workload — no BDN, no arguments.
        post_log("  BDN args: (not used by the OrchardCore benchmark)")
    else:
        # Read BDN_ARGS.rsp (from user-provided path, or download a default)
        if cfg.bdn_args_file:
            rsp = Path(cfg.bdn_args_file).resolve()
        else:
            rsp = WORK_DIR / "BDN_ARGS.rsp"
            if not rsp.exists():
                download("https://gist.github.com/EgorBo/1f99f41c39ad790294c164306001fb66/raw", rsp)
        bench_args = read_lines(rsp)
        # RSP lines may contain shell-style quotes (e.g. --filter "Foo*").
        # When passed as list elements to subprocess with shell=False, literal
        # quotes are NOT stripped, so BDN receives "Foo*" (with quotes) as the
        # filter value and matches nothing.  Use shlex.split to parse properly.
        bench_args = shlex.split(" ".join(bench_args), posix=True)
        post_log(f"  BDN args: {bench_args}")

    if cfg.skip_deps:
        post_log("[STAGE 2/6] Skipping dependency installation (--skip_deps)")
        post_log("[STAGE 3/6] Skipping .NET SDK installation (--skip_deps)")
    else:
        post_log("[STAGE 2/6] Installing dependencies...")
        install_dependencies()
        post_log("[STAGE 2/6] Dependencies installed ✓")

        post_log("[STAGE 3/6] Installing .NET SDKs...")
        install_dotnet_sdks()
        post_log("[STAGE 3/6] .NET SDKs installed ✓")

    if cfg.is_orchard:
        run_orchard(cfg)
        return

    post_log("[STAGE 4/6] Building benchmarks...")
    build_benchmarks(bench_args)
    post_log("[STAGE 4/6] Benchmarks built ✓")

    if cfg.gh_commits_and_prs:
        post_log("[STAGE 5/6] Building core_roots for all commits/PRs...")
        build_core_roots()
        post_log("[STAGE 5/6] Core_roots built ✓")
    else:
        post_log("[STAGE 5/6] No commits/PRs specified -- skipping core_root build")

    post_log("[STAGE 6/6] Running benchmarks...")
    for attempt in range(1, cfg.attempts + 1):
        if cfg.attempts > 1:
            post_log(f"[STAGE 6/6] Attempt {attempt}/{cfg.attempts}")
        run_benchmarks(bench_args, attempt=attempt, total_attempts=cfg.attempts)
    post_log("[STAGE 6/6] Benchmarks completed ✓")

    # Run perf profiling if enabled (Linux only -- delegated to platform module)
    if cfg.perf_enabled:
        if _platform_mod and hasattr(_platform_mod, "run_perf_profiling"):
            post_log("[PERF] Starting perf profiling stage...")
            _platform_mod.run_perf_profiling()
        else:
            post_log(f"[PERF] Profiling was requested but is not supported on {TARGET_OS} "
                     f"-- no profiling artifacts will be produced.")

    # Finalize: package artifacts, upload results
    post_log("Finalizing -- uploading results...")
    send_results(success=True)


def _run_main_guarded():
    """Entry point wrapper: any unhandled failure must be reported back to the server.

    Without this the job simply stops posting and the user waits for the (multi-hour)
    server-side timeout with no error message.
    """
    try:
        main()
    except SystemExit:
        raise
    except BaseException as ex:  # noqa: BLE001 - last-resort reporting
        details = traceback.format_exc()
        try:
            post_log(f"FATAL: agent failed with an unhandled error:\n{details}")
        except Exception:
            print(details)
        try:
            send_results(success=False, error=f"{type(ex).__name__}: {ex}")
        except Exception:
            print("Failed to report the failure back to EgorBot", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    _run_main_guarded()
