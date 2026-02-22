#!/usr/bin/env python3
"""
Cross-platform rewrite of run.sh — builds dotnet/runtime core_roots for
specified commits/PRs and runs BDN microbenchmarks against them.
Requires only the Python 3 standard library (no pip install needed).

This is the common module shared by all platforms.  Platform-specific
helpers live in egorbot-agent-{windows,linux,macos}.py and are loaded
at runtime via ``load_platform_module``.
"""

import argparse
import glob as globmod
import importlib.util
import io
import json
import os
import platform
import re as re_mod
import shutil
import subprocess
import sys
import threading
import time
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import List, NoReturn, Optional


# ═══════════════════════════════════════════════════════════════════════════════
# Example usage:
#   (1) With custom benchmark snippet:
#       python egorbot-agent-common.py --job_tag my_test1 --gh_commits_and_prs "PR_12345;main" --bench_code_file ./MyBenchmark.cs
#
#   (2) With dotnet/performance benchmarks for a27de4a and its previous commits:
#       python egorbot-agent-common.py --job_tag my_test2 --gh_commits_and_prs "a27de4a;a27de4a~1;a27de4a~2"
#
# BDN arguments are read from BDN_ARGS.rsp file from current dir.
# ═══════════════════════════════════════════════════════════════════════════════

@dataclass
class Config:
    work_dir: str
    job_tag: str
    gh_commits_and_prs: List[str]
    bench_code_file: str
    bench_csproj_file: str
    bench_tfm: str
    runtime_build_args: str
    bdn_args_file: str
    perf_enabled: bool
    perf_record_args: str
    perf_record_freq: str
    callback_url: str
    job_id: str
    skip_deps: bool

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
        p.add_argument("--bench_tfm", default="net10.0",
                        help="Target framework moniker (default: net10.0)")
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
        p.add_argument("--perf_record_freq", default="999",
                        help="perf record -F frequency (default: 999)")
        p.add_argument("--callback_url", default="",
                        help="Base URL of EgorBot service for posting logs/results (e.g. http://host:5000/api/internal)")
        p.add_argument("--job_id", default="",
                        help="Job ID assigned by the EgorBot service")
        p.add_argument("--skip_deps", type=int, choices=[0, 1],
                        default=0,
                        help="1 = skip dependency and .NET SDK installation (default: 0)")

        args = p.parse_args(argv)

        return Config(
            work_dir=args.work_dir,
            job_tag=args.job_tag,
            gh_commits_and_prs=[s.strip() for s in args.gh_commits_and_prs.split(";") if s.strip()],
            bench_code_file=args.bench_code_file,
            bench_csproj_file=args.bench_csproj_file,
            bench_tfm=args.bench_tfm,
            runtime_build_args=args.runtime_build_args,
            bdn_args_file=args.bdn_args_file,
            perf_enabled=bool(args.perf_enabled),
            perf_record_args=args.perf_record_args,
            perf_record_freq=args.perf_record_freq,
            callback_url=args.callback_url,
            job_id=args.job_id,
            skip_deps=bool(args.skip_deps),
        )


# ── Derived paths & platform info (filled in setup_environment()) ───────────
WORK_DIR: Path
ARTIFACTS_DIR: Path
DIR_BENCHAPP: Path
CORE_ROOTS_DIR: Path
TARGET_OS: str
TARGET_ARCH: str
CFG: Config

# ── Platform module (loaded in setup_environment()) ─────────────────────────
_platform_mod = None  # type: ignore


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
        # Stream subprocess output line-by-line through Python's sys.stdout
        # so TeeWriter captures it for the callback log sender.
        proc = subprocess.Popen(
            cmd, cwd=cwd, env=merged_env, shell=shell,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            bufsize=1, text=True, errors="replace",
        )
        for line in proc.stdout:
            sys.stdout.write(line)
            sys.stdout.flush()
        proc.wait()
        result = subprocess.CompletedProcess(cmd, proc.returncode)

    if check and result.returncode != 0:
        print(f"\n❌ Command failed (exit {result.returncode}): {label}")
        send_results(success=False, exit_code=result.returncode)

    return result


def download(url: str, dest: Path):
    """Download *url* to *dest* using urllib (no third-party deps)."""
    import urllib.request
    import ssl
    print(f"  ⬇  {url} → {dest}", flush=True)
    try:
        urllib.request.urlretrieve(url, str(dest))
    except urllib.error.URLError as e:
        if "CERTIFICATE_VERIFY_FAILED" in str(e):
            print("  ⚠  SSL verification failed, retrying with system cert store...", flush=True)
            # Some Helix Windows machines lack a proper certifi bundle.
            # Fall back to an unverified context for well-known hosts.
            ctx = ssl.create_default_context()
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
            opener = urllib.request.build_opener(
                urllib.request.HTTPSHandler(context=ctx)
            )
            with opener.open(url) as resp:
                dest.write_bytes(resp.read())
        else:
            raise


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


def dotnet_install_cmd(script: Path, *extra_args: str) -> str:
    """Build the command to invoke dotnet-install.{ps1,sh}."""
    if TARGET_OS == "windows":
        ps_args = " ".join(_to_ps_arg(a) for a in extra_args)
        ps = _platform_mod.POWERSHELL if _platform_mod else "powershell"
        return (f'"{ps}" -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = '
                f"[Net.SecurityProtocolType]::Tls12; & '{script}' {ps_args}\"")
    args = " ".join(extra_args)
    return f'bash "{script}" {args}'


def _to_ps_arg(arg: str) -> str:
    """Convert a bash-style ``--kebab-arg`` to PowerShell ``-PascalArg``."""
    if arg.startswith("--"):
        return "-" + "".join(part.capitalize() for part in arg[2:].split("-"))
    return arg


def load_platform_module(target_os: str):
    """
    Dynamically load the platform-specific module from the same directory.
    Maps: "windows" → egorbot-agent-windows.py
          "linux"   → egorbot-agent-linux.py
          "osx"     → egorbot-agent-macos.py
    """
    os_to_file = {
        "windows": "egorbot-agent-windows.py",
        "linux":   "egorbot-agent-linux.py",
        "osx":     "egorbot-agent-macos.py",
    }
    filename = os_to_file.get(target_os)
    if not filename:
        post_log(f"WARNING: No platform module for OS '{target_os}'")
        return None

    # Look next to this script
    script_dir = Path(__file__).parent
    mod_path = script_dir / filename
    if not mod_path.exists():
        post_log(f"WARNING: Platform module not found: {mod_path}")
        return None

    spec = importlib.util.spec_from_file_location(f"platform_{target_os}", mod_path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    # Inject a reference to the common module AFTER exec so the module-level
    # ``common = None`` placeholder doesn't overwrite our reference.
    mod.common = sys.modules[__name__]
    return mod


# ═══════════════════════════════════════════════════════════════════════════════
#  Callback support: TeeWriter, background log sender, result upload
# ═══════════════════════════════════════════════════════════════════════════════

class TeeWriter:
    """Wraps stdout/stderr to also accumulate lines for the background sender."""

    def __init__(self, original_stream):
        self._original = original_stream
        self._lock = threading.Lock()
        self._buffer: List[str] = []

    def write(self, text: str):
        try:
            self._original.write(text)
        except UnicodeEncodeError:
            self._original.write(text.encode('ascii', 'replace').decode())
        if text.strip():
            with self._lock:
                self._buffer.append(text.rstrip())

    def flush(self):
        self._original.flush()

    def drain(self) -> List[str]:
        """Return and clear buffered lines."""
        with self._lock:
            lines = self._buffer[:]
            self._buffer.clear()
        return lines

    # Delegate everything else to the original stream
    def __getattr__(self, name):
        return getattr(self._original, name)


_tee_stdout: Optional[TeeWriter] = None
_log_sender_stop = threading.Event()


def post_log(message: str):
    """Immediately post a single log line to the callback endpoint (and print it)."""
    try:
        print(f">> {message}", flush=True)
    except UnicodeEncodeError:
        print(f">> {message.encode('ascii', 'replace').decode()}", flush=True)
    if CFG.callback_url and CFG.job_id:
        _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/logs", [message])


def _post_json(url: str, data) -> bool:
    """POST JSON to url. Returns True on success."""
    import urllib.request
    try:
        body = json.dumps(data).encode("utf-8")
        req = urllib.request.Request(url, data=body,
                                     headers={"Content-Type": "application/json"})
        urllib.request.urlopen(req, timeout=10)
        return True
    except Exception:
        return False


def _post_multipart(url: str, fields: dict, files: dict) -> bool:
    """POST multipart/form-data. fields: {name: value}, files: {name: (filename, bytes)}."""
    import urllib.request
    try:
        boundary = "----EgorBotBoundary" + str(int(time.time()))
        body = io.BytesIO()

        for key, val in fields.items():
            body.write(f"--{boundary}\r\n".encode())
            body.write(f'Content-Disposition: form-data; name="{key}"\r\n\r\n'.encode())
            body.write(f"{val}\r\n".encode())

        for key, (filename, filedata) in files.items():
            body.write(f"--{boundary}\r\n".encode())
            body.write(f'Content-Disposition: form-data; name="{key}"; filename="{filename}"\r\n'.encode())
            body.write(b"Content-Type: application/octet-stream\r\n\r\n")
            body.write(filedata)
            body.write(b"\r\n")

        body.write(f"--{boundary}--\r\n".encode())

        req = urllib.request.Request(
            url, data=body.getvalue(),
            headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        urllib.request.urlopen(req, timeout=120)
        return True
    except Exception as e:
        print(f"  ⚠  Multipart upload failed: {e}")
        return False


def _log_sender_thread():
    """Background thread that sends buffered log lines and heartbeats every 5 seconds."""
    global _tee_stdout
    while not _log_sender_stop.is_set():
        _log_sender_stop.wait(5)
        if _tee_stdout is None or not CFG.callback_url or not CFG.job_id:
            continue
        lines = _tee_stdout.drain()
        if lines:
            _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/logs", lines)
        # Heartbeat
        _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/heartbeat", {})


def start_callback_sender():
    """Install TeeWriter on stdout/stderr and start the background log sender."""
    global _tee_stdout
    if not CFG.callback_url or not CFG.job_id:
        return
    _tee_stdout = TeeWriter(sys.stdout)
    sys.stdout = _tee_stdout  # type: ignore
    sys.stderr = TeeWriter(sys.stderr)  # type: ignore
    t = threading.Thread(target=_log_sender_thread, daemon=True)
    t.start()


def stop_callback_sender():
    """Flush remaining logs and stop the background sender."""
    global _tee_stdout
    _log_sender_stop.set()
    if _tee_stdout is not None and CFG.callback_url and CFG.job_id:
        # Flush remaining lines
        lines = _tee_stdout.drain()
        if lines:
            _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/logs", lines)


# ═══════════════════════════════════════════════════════════════════════════════
#  SendResults — the single exit point on success *or* failure
# ═══════════════════════════════════════════════════════════════════════════════

def send_results(*, success: bool, exit_code: int = 0) -> NoReturn:
    """
    Package artefacts into a zip and report the outcome.
    Always terminates the process.
    """
    post_log(f"send_results called: success={success}, exit_code={exit_code}")
    zip_path = WORK_DIR / f"artifacts_{CFG.job_tag}.zip"

    # Try to copy the agent log (if present) before zipping
    agent_log = WORK_DIR / "agent.log"
    if agent_log.exists():
        shutil.copy2(agent_log, ARTIFACTS_DIR)

    if ARTIFACTS_DIR.exists() and any(ARTIFACTS_DIR.iterdir()):
        zip_directory(ARTIFACTS_DIR, zip_path)
    else:
        print("  ⚠  No artefacts to zip.")

    # Upload results to the EgorBot service if callback_url is configured
    if CFG.callback_url and CFG.job_id:
        stop_callback_sender()
        complete_url = f"{CFG.callback_url}/jobs/{CFG.job_id}/complete"
        fields = {"success": "true" if success else "false"}
        if not success:
            fields["error"] = f"Agent failed with exit code {exit_code}"
        files = {}
        if zip_path.exists():
            files["artifacts"] = (zip_path.name, zip_path.read_bytes())
        post_log(f"Uploading results to {complete_url} ({zip_path.stat().st_size if zip_path.exists() else 0} bytes)...")
        if _post_multipart(complete_url, fields, files):
            post_log("Upload successful.")
        else:
            post_log("WARNING: Upload to /complete failed — results are still available locally.")

    if success:
        print(f"\n✅ Finished successfully.  Artefacts: {zip_path}")
    else:
        print(f"\n❌ Failed (exit code {exit_code}).  Artefacts: {zip_path}")

    sys.exit(0 if success else exit_code)


# ═══════════════════════════════════════════════════════════════════════════════
#  Main stages
# ═══════════════════════════════════════════════════════════════════════════════

def setup_environment(cfg: Config):
    """Detect platform, create working directories, set global .NET env vars."""
    global WORK_DIR, ARTIFACTS_DIR, DIR_BENCHAPP, CORE_ROOTS_DIR
    global TARGET_OS, TARGET_ARCH, CFG, _platform_mod

    CFG = cfg
    WORK_DIR = Path(cfg.work_dir).resolve()
    os.chdir(WORK_DIR)

    TARGET_OS, TARGET_ARCH = detect_platform()

    # Load platform-specific module
    _platform_mod = load_platform_module(TARGET_OS)
    if _platform_mod and hasattr(_platform_mod, "setup_platform"):
        _platform_mod.setup_platform()

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
##
########################################################################################

def install_dependencies():
    # On local runs (callback to localhost), don't kill dotnet — it would kill the web server!
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


########################################################################################
##
## Install .NET SDKs
##
########################################################################################

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


########################################################################################
##
## Build & prepare benchmarks
##
########################################################################################

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

        # Fix multi-targeting: only build for the requested TFM
        csproj_text = csproj.read_text(encoding="utf-8")
        csproj_text = re_mod.sub(
            r'<TargetFrameworks>[^<]+</TargetFrameworks>',
            f'<TargetFramework>{CFG.bench_tfm}</TargetFramework>',
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


########################################################################################
##
## Build core-roots for all commits and PRs specified in GH_COMMITS_AND_PRS
##
########################################################################################

def clone_runtime():
    runtime_dir = WORK_DIR / "runtime"
    if not runtime_dir.is_dir():
        post_log("Cloning dotnet/runtime...")
        # Enable long paths on Windows — dotnet/runtime has files that exceed the 260-char limit
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
                # Short commit hashes can't be fetched as refs — fetch full
                # history (unshallow if needed) then checkout locally.
                run("git fetch --unshallow origin || git fetch origin", cwd=runtime_dir, check=False)
                run(f"git checkout {commit}", cwd=runtime_dir)

        # Install deps via runtime's own script (most deps come from here)
        if TARGET_OS == "osx":
            run("eng/common/native/./install-dependencies.sh", cwd=runtime_dir, check=False)
        elif TARGET_OS != "windows":
            run("sudo eng/common/native/./install-dependencies.sh", cwd=runtime_dir, check=False)

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
        run(f"{make_script('build')} clr+libs -c Release{arch_flag} {CFG.runtime_build_args}", cwd=runtime_dir)

        if TARGET_OS == "windows":
            run(f"src\\tests\\build.cmd{arch_flag} Release generatelayoutonly", cwd=runtime_dir)
        else:
            run(f"./src/tests/build.sh{arch_flag} Release generatelayoutonly", cwd=runtime_dir)

        print("Successfully built runtime")
        post_log(f"Core_root built for '{item}' ✓")

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
    """Run BDN benchmarks using all built core_roots (or without --corerun if none)."""
    # Gather all corerun paths (one per commit/PR)
    corerun_paths = sorted(globmod.glob(
        str(CORE_ROOTS_DIR / "*" / make_exe("corerun"))
    ))
    post_log(f"Running benchmarks with {len(corerun_paths)} corerun(s): {corerun_paths}")
    hide_columns = ["-h", "Job", "StdDev", "RatioSD", "Median", "Min", "Max"]

    # Build the --corerun portion only when we actually have core_roots
    corerun_args = ["--corerun"] + corerun_paths if corerun_paths else []

    # Use sudo on Linux/macOS for more stable benchmark results
    # (reduces noise from CPU frequency scaling, perf counters, etc.)
    sudo_prefix = ["sudo"] if TARGET_OS in ("linux", "osx") else []

    if CFG.bench_use_dotnet_performance:
        # Run benchmarks from dotnet/performance repo
        micro_dir = WORK_DIR / "performance" / "src" / "benchmarks" / "micro"
        micro_bin = (WORK_DIR / "performance" / "artifacts" / "bin"
                     / "MicroBenchmarks" / "Release" / CFG.bench_tfm / "MicroBenchmarks")
        run(sudo_prefix + [str(micro_bin)] + bench_args + corerun_args + hide_columns,
            cwd=micro_dir, shell=False)
        # Copy performance/artifacts/.../BenchmarkDotNet.Artifacts/results to artifacts dir
        results_pattern = str(
            WORK_DIR / "performance" / "artifacts" / "bin" / "MicroBenchmarks"
            / "Release" / CFG.bench_tfm / "BenchmarkDotNet.Artifacts" / "results" / "*.*"
        )
    else:
        # Run custom benchmarks
        run(sudo_prefix + ["dotnet", "run", "-c", "Release", "-f", CFG.bench_tfm, "--"] +
            corerun_args + bench_args + hide_columns,
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

    # Start background log sender if callback is configured
    start_callback_sender()

    post_log(f"[STAGE 1/6] Environment set up. OS={TARGET_OS}, Arch={TARGET_ARCH}, WorkDir={WORK_DIR}")
    post_log(f"  Commits/PRs: {cfg.gh_commits_and_prs}")
    post_log(f"  BenchCodeFile: {cfg.bench_code_file or '(none)'}")
    post_log(f"  Callback: {cfg.callback_url or '(none)'}, JobId: {cfg.job_id or '(none)'}")

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
    import shlex
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

    post_log("[STAGE 4/6] Building benchmarks...")
    build_benchmarks(bench_args)
    post_log("[STAGE 4/6] Benchmarks built ✓")

    if cfg.gh_commits_and_prs:
        post_log("[STAGE 5/6] Building core_roots for all commits/PRs...")
        build_core_roots()
        post_log("[STAGE 5/6] Core_roots built ✓")
    else:
        post_log("[STAGE 5/6] No commits/PRs specified — skipping core_root build")

    post_log("[STAGE 6/6] Running benchmarks...")
    run_benchmarks(bench_args)
    post_log("[STAGE 6/6] Benchmarks completed ✓")

    # Run perf profiling if enabled (Linux only — delegated to platform module)
    if cfg.perf_enabled and _platform_mod and hasattr(_platform_mod, "run_perf_profiling"):
        post_log("[PERF] Starting perf profiling stage...")
        _platform_mod.run_perf_profiling()

    # Finalize: copy logs, zip artifacts, report success
    post_log("Finalizing — packaging artifacts and uploading results...")
    agent_log = WORK_DIR / "agent.log"
    if agent_log.exists():
        shutil.copy2(agent_log, ARTIFACTS_DIR)
    zip_path = WORK_DIR / f"artifacts_{CFG.job_tag}.zip"
    zip_directory(ARTIFACTS_DIR, zip_path)
    send_results(success=True)


if __name__ == "__main__":
    main()
