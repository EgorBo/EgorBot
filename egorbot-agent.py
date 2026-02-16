#!/usr/bin/env python3
"""
Cross-platform rewrite of run.sh — builds dotnet/runtime core_roots for
specified commits/PRs and runs BDN microbenchmarks against them.
Requires only the Python 3 standard library (no pip install needed).
"""

import argparse
import glob as globmod
import os
import platform
import shutil
import subprocess
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import List, NoReturn, Optional


# ═══════════════════════════════════════════════════════════════════════════════
# Example usage:
#   (1) With custom benchmark snippet:
#       python egorbot-agent.py --job_tag my_test1 --gh_commits_and_prs "PR_12345;main" --bench_code_link "https://gist.github.com/your_gist_link/raw" --bench_add_entrypoint 1
#
#   (2) With dotnet/performance benchmarks for a27de4a and its previous commits:
#       python egorbot-agent.py --job_tag my_test2 --gh_commits_and_prs "a27de4a;a27de4a~1;a27de4a~2"
#
# BDN arguments are read from BDN_ARGS.rsp file from current dir.
# ═══════════════════════════════════════════════════════════════════════════════

@dataclass
class Config:
    work_dir: str
    job_tag: str
    gh_commits_and_prs: List[str]
    bench_code_link: str
    bench_csproj_link: str
    bench_add_entrypoint: bool
    bench_tfm: str
    runtime_build_args: str
    perf_enabled: bool
    perf_record_args: str
    perf_record_freq: str

    @property
    def bench_use_dotnet_performance(self) -> bool:
        """Use dotnet/performance benchmark suite when bench_code_link is empty.
        We'll rely on --filter arg from BDN_ARGS.rsp."""
        return self.bench_code_link == ""

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
        p.add_argument("--gh_commits_and_prs",
                        default="PR_124445;main",
                        help='Semicolon-separated commits/PRs to compare. PRs prefixed with "PR_", '
                             'e.g. PR_12345;main  (default: PR_124445;main)')
        p.add_argument("--bench_code_link",
                        default="",
                        help="Link to benchmark snippet (empty = use dotnet/performance)")
        p.add_argument("--bench_csproj_link",
                        default="https://gist.github.com/EgorBo/c3378873ad204ebf522a07138f621128/raw",
                        help="csproj template link for custom benchmarks")
        p.add_argument("--bench_add_entrypoint", type=int, choices=[0, 1],
                        default=1,
                        help="1 = add Program.cs with BenchmarkSwitcher, 0 = don't (default: 1)")
        p.add_argument("--bench_tfm", default="net10.0",
                        help="Target framework moniker (default: net10.0)")
        p.add_argument("--runtime_build_args", default="/p:NoPgoOptimize=true",
                        help='Extra args for build.sh/build.cmd (default: /p:NoPgoOptimize=true)')
        p.add_argument("--perf_enabled", type=int, choices=[0, 1],
                        default=0,
                        help="1 = enable perf recording (default: 0)")
        p.add_argument("--perf_record_args", default="",
                        help="Extra args for perf record")
        p.add_argument("--perf_record_freq", default="999",
                        help="perf record -F frequency (default: 999)")

        args = p.parse_args(argv)

        return Config(
            work_dir=args.work_dir,
            job_tag=args.job_tag,
            gh_commits_and_prs=[s.strip() for s in args.gh_commits_and_prs.split(";") if s.strip()],
            bench_code_link=args.bench_code_link,
            bench_csproj_link=args.bench_csproj_link,
            bench_add_entrypoint=bool(args.bench_add_entrypoint),
            bench_tfm=args.bench_tfm,
            runtime_build_args=args.runtime_build_args,
            perf_enabled=bool(args.perf_enabled),
            perf_record_args=args.perf_record_args,
            perf_record_freq=args.perf_record_freq,
        )


# ── Derived paths & platform info (filled in setup_environment()) ───────────
WORK_DIR: Path
ARTIFACTS_DIR: Path
DIR_BENCHAPP: Path
CORE_ROOTS_DIR: Path
TARGET_OS: str
TARGET_ARCH: str
CFG: Config


# ═══════════════════════════════════════════════════════════════════════════════
#  Helpers
# ═══════════════════════════════════════════════════════════════════════════════

def run(
    cmd: str | List[str],
    *,
    cwd: Optional[Path] = None,
    check: bool = True,
    env: Optional[dict] = None,
    shell: bool = True,
    stdout_file: Optional[Path] = None,
) -> subprocess.CompletedProcess:
    """
    Run *cmd* with live stdout/stderr streaming to the terminal.
    If *stdout_file* is set, stdout is written to that file instead of the
    terminal (cross-platform replacement for ``> file`` shell redirect).
    If *check* is True (default) and the command exits non-zero,
    ``send_results`` is called with the error code and the script exits.
    """
    merged_env = {**os.environ, **(env or {})}
    label = cmd if isinstance(cmd, str) else " ".join(cmd)
    if stdout_file:
        print(f"\n▶ {label}  (→ {stdout_file})", flush=True)
    else:
        print(f"\n▶ {label}", flush=True)

    if stdout_file:
        with open(stdout_file, "w", encoding="utf-8") as fout:
            result = subprocess.run(
                cmd, cwd=cwd, env=merged_env, shell=shell,
                stdout=fout,
            )
    else:
        result = subprocess.run(
            cmd, cwd=cwd, env=merged_env, shell=shell,
            # No capture — stdout/stderr flow directly to the terminal in real time.
        )

    if check and result.returncode != 0:
        print(f"\n❌ Command failed (exit {result.returncode}): {label}")
        send_results(success=False, exit_code=result.returncode)

    return result


def download(url: str, dest: Path):
    """Download *url* to *dest* using urllib (no third-party deps)."""
    import urllib.request
    print(f"  ⬇  {url} → {dest}", flush=True)
    urllib.request.urlretrieve(url, str(dest))


def read_lines(path: Path) -> List[str]:
    """Read non-empty, non-comment lines from a file."""
    lines = path.read_text(encoding="utf-8").splitlines()
    return [l.strip() for l in lines if l.strip() and not l.strip().startswith("#")]


def zip_directory(src_dir: Path, zip_path: Path):
    """Recursively zip *src_dir* into *zip_path*."""
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, _dirs, files in os.walk(src_dir):
            for f in files:
                full = Path(root) / f
                zf.write(full, full.relative_to(src_dir))
    print(f"  📦 Created {zip_path}")


def kill_process_by_name(name: str):
    """Best-effort kill of processes by name (cross-platform)."""
    try:
        if TARGET_OS == "windows":
            subprocess.run(f"taskkill /F /IM {name}.exe", shell=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else:
            subprocess.run(f"pkill {name}", shell=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception:
        pass


def sed_replace(filepath: Path, old: str, new: str):
    """In-place text replacement in *filepath* (cross-platform sed)."""
    text = filepath.read_text(encoding="utf-8")
    text = text.replace(old, new)
    filepath.write_text(text, encoding="utf-8")


def detect_platform() -> tuple[str, str]:
    """Return (target_os, target_arch)."""
    system = platform.system().lower()
    machine = platform.machine().lower()
    if system == "linux":
        target_os = "linux"
    elif system == "darwin":
        target_os = "osx"
    elif system == "windows":
        target_os = "windows"
    else:
        target_os = system
    if machine in ("aarch64", "arm64"):
        target_arch = "arm64"
    else:
        target_arch = "x64"
    return target_os, target_arch


def make_exe(name: str) -> str:
    """Append .exe on Windows, nothing otherwise."""
    return f"{name}.exe" if TARGET_OS == "windows" else name


def make_script(name: str) -> str:
    """Return name.cmd on Windows, ./name.sh otherwise (for direct execution)."""
    return f"{name}.cmd" if TARGET_OS == "windows" else f"./{name}.sh"


def _to_ps_arg(arg: str) -> str:
    """Convert a bash-style ``--kebab-arg`` to PowerShell ``-PascalArg``."""
    if arg.startswith("--"):
        return "-" + "".join(part.capitalize() for part in arg[2:].split("-"))
    return arg


def dotnet_install_cmd(script: Path, *extra_args: str) -> str:
    if TARGET_OS == "windows":
        ps_args = " ".join(_to_ps_arg(a) for a in extra_args)
        return (f"powershell -ExecutionPolicy Bypass -Command \"[Net.ServicePointManager]::SecurityProtocol = "
                f"[Net.SecurityProtocolType]::Tls12; & '{script}' {ps_args}\"")
    args = " ".join(extra_args)
    return f'bash "{script}" {args}'


# ═══════════════════════════════════════════════════════════════════════════════
#  SendResults — the single exit point on success *or* failure
# ═══════════════════════════════════════════════════════════════════════════════

def send_results(*, success: bool, exit_code: int = 0) -> NoReturn:
    """
    Package artefacts into a zip and report the outcome.
    Always terminates the process.

    TODO: plug in your own upload / notification logic here.
    """
    zip_path = WORK_DIR / f"artifacts_{CFG.job_tag}.zip"

    # Try to copy the agent log (if present) before zipping
    agent_log = WORK_DIR / "agent.log"
    if agent_log.exists():
        shutil.copy2(agent_log, ARTIFACTS_DIR)

    if ARTIFACTS_DIR.exists() and any(ARTIFACTS_DIR.iterdir()):
        zip_directory(ARTIFACTS_DIR, zip_path)
    else:
        print("  ⚠  No artefacts to zip.")

    if success:
        print(f"\n✅ Finished successfully.  Artefacts: {zip_path}")
        # TODO: upload zip_path / send success notification
    else:
        print(f"\n❌ Failed (exit code {exit_code}).  Artefacts: {zip_path}")
        # TODO: upload zip_path / send failure notification

    sys.exit(0 if success else exit_code)


# ═══════════════════════════════════════════════════════════════════════════════
#  Main stages
# ═══════════════════════════════════════════════════════════════════════════════

def setup_environment(cfg: Config):
    """Detect platform, create working directories, set global .NET env vars."""
    global WORK_DIR, ARTIFACTS_DIR, DIR_BENCHAPP, CORE_ROOTS_DIR
    global TARGET_OS, TARGET_ARCH, CFG

    CFG = cfg
    WORK_DIR = Path(cfg.work_dir).resolve()
    os.chdir(WORK_DIR)

    TARGET_OS, TARGET_ARCH = detect_platform()

    ARTIFACTS_DIR  = WORK_DIR / "artifacts"
    DIR_BENCHAPP   = WORK_DIR / "benchapp"
    CORE_ROOTS_DIR = WORK_DIR / "core_roots"
    for d in (ARTIFACTS_DIR, DIR_BENCHAPP, CORE_ROOTS_DIR):
        d.mkdir(parents=True, exist_ok=True)

    # Some global env vars for .NET
    os.environ["DOTNET_JitEnableOptionalRelocs"] = "0"  # Improve consistency of measurements
    os.environ["DOTNET_EnableWriteXorExecute"]   = "0"  # Not sure this affects consistency, improves perf record
    os.environ["PERFLAB_TARGET_FRAMEWORKS"]      = cfg.bench_tfm


########################################################################################
##
## Install dependencies
## NOTE: most deps are installed by 'eng/common/native/install-dependencies.sh'
##
########################################################################################

def install_dependencies():
    kill_process_by_name("dotnet")

    marker = WORK_DIR / ".deps_installed"
    if marker.exists():
        print("Dependencies already installed, skipping installation")
        return

    if TARGET_OS == "linux":
        if shutil.which("apt"):
            run("apt update")
            # ninja-build is not installed by install-dependencies.sh yet :'(
            run("apt install -y git zip ninja-build parallel")

            # Install perf if it's not available and PERF_ENABLED is 1
            if CFG.perf_enabled and not shutil.which("perf"):
                print("perf not found, installing linux-tools-generic and linux-cloud-tools-generic")
                run("apt install -y linux-tools-generic linux-cloud-tools-generic", check=False)
                run(
                    "bash -c 'ln -s /usr/lib/linux-tools/$(ls /usr/lib/linux-tools/ "
                    "| grep -v common | head -n 1) /usr/lib/linux-tools/$(uname -r) || true'",
                    check=False,
                )
        elif shutil.which("dnf"):
            run("dnf install -y git zip ninja-build parallel")
            run("dnf install -y perl-open.noarch")  # for FlameGraph
        marker.touch()

    elif TARGET_OS == "osx":
        # TODO: insert macOS dependency installation here
        # marker.touch()

    elif TARGET_OS == "windows":
        # TODO: insert Windows dependency installation here
        # marker.touch()

    else:
        print(f"❌ Unsupported TARGET_OS: {TARGET_OS}")
        send_results(success=False, exit_code=1)


########################################################################################
##
## Install .NET SDKs
##
########################################################################################

def install_dotnet_sdks():
    script_name = "dotnet-install.ps1" if TARGET_OS == "windows" else "dotnet-install.sh"
    script_path = WORK_DIR / script_name

    if not script_path.exists():
        download(f"https://dot.net/v1/{script_name}", script_path)
        if TARGET_OS != "windows":
            script_path.chmod(0o755)

        install_dir = str(WORK_DIR / ".dotnet")
        run(dotnet_install_cmd(script_path, "--channel", "11.0", "--quality", "daily",
                               "--install-dir", install_dir))
        run(dotnet_install_cmd(script_path, "--channel", "10.0",
                               "--install-dir", install_dir))

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


########################################################################################
##
## Build & prepare benchmarks
##
########################################################################################

def build_benchmarks(bench_args: List[str]):
    if CFG.bench_use_dotnet_performance:
        _build_dotnet_performance_benchmarks(bench_args)
    else:
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
        download(CFG.bench_code_link, DIR_BENCHAPP / "Program.cs")
        download(CFG.bench_csproj_link, csproj)

        if CFG.bench_add_entrypoint:
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


########################################################################################
##
## Build core-roots for all commits and PRs specified in GH_COMMITS_AND_PRS
##
########################################################################################

def clone_runtime():
    runtime_dir = WORK_DIR / "runtime"
    if not runtime_dir.is_dir():
        run(f'git clone --no-tags --single-branch '
                    f'https://github.com/dotnet/runtime.git "{runtime_dir}"', check=False)
        run('git config --global user.email egorbot@egorbo.com', cwd=runtime_dir)
        run('git config --global user.name egorbot', cwd=runtime_dir)

def build_core_roots():
    runtime_dir = WORK_DIR / "runtime"
    clone_runtime()

    for item in CFG.gh_commits_and_prs:
        print(f"\nBuilding for {item}")
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
                run(f"git fetch origin {commit}", cwd=runtime_dir)
                run(f"git checkout {commit}", cwd=runtime_dir)

        # Install deps via runtime's own script (most deps come from here)
        if TARGET_OS != "windows":
            run("eng/common/native/./install-dependencies.sh", cwd=runtime_dir)

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
        run(f"{make_script('build')} clr+libs -c Release {CFG.runtime_build_args}", cwd=runtime_dir)

        if TARGET_OS == "windows":
            run("src\\tests\\build.cmd Release generatelayoutonly", cwd=runtime_dir)
        else:
            run("./src/tests/build.sh Release generatelayoutonly", cwd=runtime_dir)

        print("Successfully built runtime")

        # Copy Core_Root to our core_roots folder
        core_root_src = (runtime_dir / "artifacts" / "tests" / "coreclr"
                         / f"{TARGET_OS}.{TARGET_ARCH}.Release" / "Tests" / "Core_Root")
        shutil.copytree(str(core_root_src), str(core_root_dest))

        run(f"{make_script('dotnet')} build-server shutdown", cwd=runtime_dir)
        kill_process_by_name("dotnet")


########################################################################################
##
## Run benchmarks
##
########################################################################################

def run_benchmarks(bench_args: List[str]):
    """Run BDN benchmarks using all built core_roots."""
    # Gather all corerun paths (one per commit/PR)
    corerun_paths = sorted(globmod.glob(
        str(CORE_ROOTS_DIR / "*" / make_exe("corerun"))
    ))
    hide_columns = ["-h", "Job", "StdDev", "RatioSD", "Median", "Min", "Max"]

    if CFG.bench_use_dotnet_performance:
        # Run benchmarks from dotnet/performance repo
        micro_dir = WORK_DIR / "performance" / "src" / "benchmarks" / "micro"
        micro_bin = (WORK_DIR / "performance" / "artifacts" / "bin"
                     / "MicroBenchmarks" / "Release" / CFG.bench_tfm / "MicroBenchmarks")
        run([str(micro_bin)] + bench_args + ["--corerun"] + corerun_paths + hide_columns,
            cwd=micro_dir, shell=False)
        # Copy performance/artifacts/.../BenchmarkDotNet.Artifacts/results to artifacts dir
        results_pattern = str(
            WORK_DIR / "performance" / "artifacts" / "bin" / "MicroBenchmarks"
            / "Release" / CFG.bench_tfm / "BenchmarkDotNet.Artifacts" / "results" / "*.*"
        )
    else:
        # Run custom benchmarks
        run(["dotnet", "run", "-c", "Release", "-f", CFG.bench_tfm, "--",
             "--corerun"] + corerun_paths + bench_args + hide_columns,
            cwd=DIR_BENCHAPP, shell=False)
        # Copy benchapp/BenchmarkDotNet.Artifacts/results/*.* to artifacts dir
        results_pattern = str(
            DIR_BENCHAPP / "BenchmarkDotNet.Artifacts" / "results" / "*.*"
        )

    for src in globmod.glob(results_pattern):
        shutil.copy2(src, ARTIFACTS_DIR)


# ═══════════════════════════════════════════════════════════════════════════════
#  Entry point
# ═══════════════════════════════════════════════════════════════════════════════

def main(cfg: Optional[Config] = None):
    """Run the full pipeline. Pass a Config directly, or leave as None
    to parse CLI args (which fall back to env vars, then defaults)."""
    if cfg is None:
        cfg = Config.parse_args()

    setup_environment(cfg)

    # Download / read BDN_ARGS.rsp
    rsp = WORK_DIR / "BDN_ARGS.rsp"
    if not rsp.exists():
        download("https://gist.github.com/EgorBo/1f99f41c39ad790294c164306001fb66/raw", rsp)
    bench_args = read_lines(rsp)

    install_dependencies()
    install_dotnet_sdks()
    build_benchmarks(bench_args)
    build_core_roots()
    run_benchmarks(bench_args)

    # Finalize: copy logs, zip artifacts, report success
    agent_log = WORK_DIR / "agent.log"
    if agent_log.exists():
        shutil.copy2(agent_log, ARTIFACTS_DIR)
    zip_path = WORK_DIR / f"artifacts_{CFG.job_tag}.zip"
    zip_directory(ARTIFACTS_DIR, zip_path)
    send_results(success=True)


if __name__ == "__main__":
    main()