#!/usr/bin/env python3
"""
Utility functions and infrastructure shared by all EgorBot agent modules.

Loaded by ``bdn-benchmarking-common.py`` at startup.  Provides:

  * Process execution helpers  (run, kill_process_by_name)
  * File / IO utilities        (download, read_lines, zip_directory, …)
  * Platform detection          (detect_platform, is_unix, …)
  * .NET tooling helpers        (dotnet_install_cmd)
  * Dynamic module loading      (load_platform_module)
  * Callback / logging          (TeeWriter, post_log, start/stop_callback_sender)
  * Result packaging            (send_results)

All functions that need shared state access module-level globals which are
set during initialisation by ``common.setup_environment()``.
"""

import glob as globmod
import importlib.util
import io
import json
import os
import platform
import shutil
import subprocess
import sys
import threading
import time
import zipfile
from pathlib import Path
from typing import List, NoReturn, Optional


# ═══════════════════════════════════════════════════════════════════════════════
#  Module-level state — set by the common module's setup_environment()
# ═══════════════════════════════════════════════════════════════════════════════

WORK_DIR: Optional[Path] = None
ARTIFACTS_DIR: Optional[Path] = None
DIR_BENCHAPP: Optional[Path] = None
CORE_ROOTS_DIR: Optional[Path] = None
TARGET_OS: str = ""
TARGET_ARCH: str = ""
CFG = None                # Config instance (set by common)
_platform_mod = None      # Platform-specific module

# Back-reference to the common module so load_platform_module() can inject it
# into platform modules as ``mod.common``.
_common_ref = None

# Callback state
_tee_stdout: Optional["TeeWriter"] = None
_log_sender_stop = threading.Event()


def set_common_ref(common_module):
    """Store a reference to ``bdn-benchmarking-common`` for platform injection."""
    global _common_ref
    _common_ref = common_module


# ═══════════════════════════════════════════════════════════════════════════════
#  Process execution
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


def kill_process_by_name(name: str):
    """Best-effort kill of processes by name (cross-platform).

    Refuses to kill 'dotnet' when the agent is talking to a callback on localhost:
    that is a local development run, where the EgorBot server itself is a dotnet
    process and killing it would take the whole bot down.
    """
    if name == "dotnet" and CFG.callback_url and "localhost" in CFG.callback_url:
        print("  ⚠  Skipping 'kill dotnet' — local run, this would kill the EgorBot server.")
        return
    try:
        if TARGET_OS == "windows":
            subprocess.run(f"taskkill /F /IM {name}.exe", shell=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else:
            subprocess.run(f"pkill {name}", shell=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception:
        pass


# ═══════════════════════════════════════════════════════════════════════════════
#  File / IO utilities
# ═══════════════════════════════════════════════════════════════════════════════

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


def sed_replace(filepath: Path, old: str, new: str):
    """In-place text replacement in *filepath* (cross-platform sed)."""
    text = filepath.read_text(encoding="utf-8")
    text = text.replace(old, new)
    filepath.write_text(text, encoding="utf-8")


def ensure_dirs(*dirs: Path):
    """Create directories (with parents) if they don't already exist."""
    for d in dirs:
        d.mkdir(parents=True, exist_ok=True)


def copy_glob(pattern: str, dest_dir: Path):
    """Copy every file matching *pattern* into *dest_dir*."""
    for src in globmod.glob(pattern):
        shutil.copy2(src, dest_dir)


# ═══════════════════════════════════════════════════════════════════════════════
#  Platform detection
# ═══════════════════════════════════════════════════════════════════════════════

def detect_platform() -> tuple[str, str]:
    """Return ``(target_os, target_arch)``."""
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


def is_unix() -> bool:
    """``True`` when running on Linux or macOS."""
    return TARGET_OS in ("linux", "osx")


def make_exe(name: str) -> str:
    """Append ``.exe`` on Windows, nothing otherwise."""
    return f"{name}.exe" if TARGET_OS == "windows" else name


def make_script(name: str) -> str:
    """Return ``name.cmd`` on Windows, ``./name.sh`` otherwise."""
    return f"{name}.cmd" if TARGET_OS == "windows" else f"./{name}.sh"


# ═══════════════════════════════════════════════════════════════════════════════
#  .NET tooling helpers
# ═══════════════════════════════════════════════════════════════════════════════

def dotnet_install_cmd(script: Path, *extra_args: str) -> str:
    """Build the shell command to invoke ``dotnet-install.{ps1,sh}``."""
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


# ═══════════════════════════════════════════════════════════════════════════════
#  Dynamic platform module loading
# ═══════════════════════════════════════════════════════════════════════════════

def load_sibling_module(filename: str, module_name: str):
    """Load a module that ships next to this script and inject the *common*
    module into it as ``mod.common``."""
    mod_path = Path(__file__).parent / filename
    if not mod_path.exists():
        post_log(f"WARNING: Module not found: {mod_path}")
        return None

    spec = importlib.util.spec_from_file_location(module_name, mod_path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    mod.common = _common_ref
    return mod


def load_platform_module(target_os: str):
    """
    Dynamically load the platform-specific module from the same directory.

    Maps:
        ``"windows"`` → ``bdn-benchmarking-windows.py``
        ``"linux"``   → ``bdn-benchmarking-linux.py``
        ``"osx"``     → ``bdn-benchmarking-macos.py``

    The loaded module receives a reference to the *common* module (not this
    helpers module) so that ``mod.common.run()`` etc. work correctly.
    """
    os_to_file = {
        "windows": "bdn-benchmarking-windows.py",
        "linux":   "bdn-benchmarking-linux.py",
        "osx":     "bdn-benchmarking-macos.py",
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
    # Inject the common module (not this helpers module) so platform code
    # can call common.run(), common.post_log(), common.WORK_DIR, etc.
    mod.common = _common_ref
    return mod


# ═══════════════════════════════════════════════════════════════════════════════
#  Logging & callback infrastructure
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


def post_log(message: str):
    """Immediately post a single log line to the callback endpoint (and print it)."""
    try:
        print(f">> {message}", flush=True)
    except UnicodeEncodeError:
        print(f">> {message.encode('ascii', 'replace').decode()}", flush=True)
    if CFG and CFG.callback_url and CFG.job_id:
        _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/logs", [message])


def _post_json(url: str, data) -> bool:
    """POST JSON to *url*.  Returns ``True`` on success."""
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
    """POST multipart/form-data.

    *fields*: ``{name: value}``
    *files*:  ``{name: (filename, bytes)}``
    """
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
    """Background thread that sends buffered log lines and heartbeats every 5 s."""
    global _tee_stdout
    while not _log_sender_stop.is_set():
        _log_sender_stop.wait(5)
        if _tee_stdout is None or not CFG or not CFG.callback_url or not CFG.job_id:
            continue
        lines = _tee_stdout.drain()
        if lines:
            _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/logs", lines)
        # Heartbeat
        _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/heartbeat", {})


def start_callback_sender():
    """Install TeeWriter on stdout/stderr and start the background log sender."""
    global _tee_stdout
    if not CFG or not CFG.callback_url or not CFG.job_id:
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
    if _tee_stdout is not None and CFG and CFG.callback_url and CFG.job_id:
        # Flush remaining lines
        lines = _tee_stdout.drain()
        if lines:
            _post_json(f"{CFG.callback_url}/jobs/{CFG.job_id}/logs", lines)


# ═══════════════════════════════════════════════════════════════════════════════
#  Result packaging — single exit point on success *or* failure
# ═══════════════════════════════════════════════════════════════════════════════

def send_results(*, success: bool, exit_code: int = 0, error: str = "") -> NoReturn:
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
            # Surface the real reason — "exit code 1" tells the user nothing.
            fields["error"] = error or f"Agent failed with exit code {exit_code}"
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
