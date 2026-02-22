#!/usr/bin/env python3
"""
Windows-specific helpers for the EgorBot agent.

Exports:
    setup_platform()          — resolve PowerShell, set _POWERSHELL
    install_platform_deps()   — install git, cmake, ninja, VS Build Tools
"""

import os
import shutil
import subprocess
from pathlib import Path

# ── Injected by common module's load_platform_module() ──────────────────────
# ``common`` is set to the bdn-benchmarking-common module before this file executes.
common = None  # type: ignore


# ═══════════════════════════════════════════════════════════════════════════════
#  PowerShell resolution
# ═══════════════════════════════════════════════════════════════════════════════

POWERSHELL: str = ""  # resolved lazily in setup_platform()


def _find_powershell() -> str:
    """Find a working PowerShell executable: pwsh (PS 7) → powershell (PS 5.1) → full paths."""
    for candidate in [
        "pwsh",                                                             # PS 7, usually on PATH
        "powershell",                                                       # PS 5.1, usually on PATH
        r"C:\Program Files\PowerShell\7\pwsh.exe",                          # PS 7 default install
        r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",       # PS 5.1 full path
    ]:
        if shutil.which(candidate) or os.path.isfile(candidate):
            return candidate
    return "powershell"  # last-resort fallback


def setup_platform():
    """Resolve PowerShell path early so dotnet_install_cmd() can use it."""
    global POWERSHELL
    POWERSHELL = _find_powershell()
    common.post_log(f"Using PowerShell: {POWERSHELL}")


# ═══════════════════════════════════════════════════════════════════════════════
#  VS Build Tools
# ═══════════════════════════════════════════════════════════════════════════════

def _ensure_vs_build_tools():
    """
    Ensure Visual Studio Build Tools with C++ workload is available.
    Downloads vswhere if missing; installs VS Build Tools if needed.
    """
    pf86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
    installer_dir = os.path.join(pf86, "Microsoft Visual Studio", "Installer")
    vswhere_exe = os.path.join(installer_dir, "vswhere.exe")

    # ── Step 1: ensure vswhere.exe exists ─────────────────────────────────
    if not os.path.isfile(vswhere_exe):
        common.post_log("vswhere.exe not found, downloading...")
        common.ensure_dirs(Path(installer_dir))
        vswhere_url = "https://netcorenativeassets.blob.core.windows.net/resource-packages/external/windows/vswhere/3.1.7/vswhere.exe"
        try:
            common.download(vswhere_url, Path(vswhere_exe))
        except Exception as e:
            common.post_log(f"WARNING: Failed to download vswhere.exe: {e}")
            return

    # ── Step 2: check if VS with C++ tools is already installed ───────────
    result = subprocess.run(
        [vswhere_exe, "-latest", "-prerelease", "-products", "*",
         "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
         "-property", "installationPath"],
        capture_output=True, text=True
    )
    vs_path = result.stdout.strip()
    if vs_path and os.path.isdir(vs_path):
        common.post_log(f"VS Build Tools found at {vs_path}")
        _activate_vs_environment(vs_path)
        return

    # ── Step 3: install VS Build Tools with C++ workload ──────────────────
    common.post_log("VS Build Tools with C++ not found — installing (this may take 10-20 min)...")
    vs_installer_url = "https://aka.ms/vs/17/release/vs_BuildTools.exe"
    vs_installer = common.WORK_DIR / "vs_BuildTools.exe"
    try:
        common.download(vs_installer_url, vs_installer)
    except Exception as e:
        common.post_log(f"WARNING: Failed to download VS Build Tools installer: {e}")
        return

    common.run(f'"{vs_installer}" --quiet --wait --norestart '
        '--add Microsoft.VisualStudio.Workload.VCTools '
        '--add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 '
        '--add Microsoft.VisualStudio.Component.VC.Tools.ARM64 '
        '--add Microsoft.VisualStudio.Component.Windows11SDK.26100 '
        '--includeRecommended',
        check=False)

    # Re-run vswhere to find the newly installed path
    result = subprocess.run(
        [vswhere_exe, "-latest", "-prerelease", "-products", "*",
         "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
         "-property", "installationPath"],
        capture_output=True, text=True
    )
    vs_path = result.stdout.strip()
    if vs_path and os.path.isdir(vs_path):
        common.post_log(f"VS Build Tools installed at {vs_path}")
        _activate_vs_environment(vs_path)
    else:
        common.post_log("WARNING: VS Build Tools installation may have failed — build.cmd will likely fail")


def _activate_vs_environment(vs_path: str):
    """
    Run VsDevCmd.bat and capture the resulting environment variables so that
    init-vs-env.cmd in dotnet/runtime sees VisualStudioVersion already set.
    """
    vsdevcmd = os.path.join(vs_path, "Common7", "Tools", "VsDevCmd.bat")
    if not os.path.isfile(vsdevcmd):
        common.post_log(f"WARNING: VsDevCmd.bat not found at {vsdevcmd}")
        return

    result = subprocess.run(
        f'cmd /c ""{vsdevcmd}" -no_logo && set"',
        capture_output=True, text=True, shell=True
    )
    if result.returncode != 0:
        common.post_log("WARNING: VsDevCmd.bat failed")
        return

    for line in result.stdout.splitlines():
        if '=' not in line:
            continue
        key, _, value = line.partition('=')
        if key.upper() in ("PATH", "INCLUDE", "LIB", "LIBPATH") or \
           key.upper().startswith(("VS", "VC", "VSCMD", "VISUAL")):
            os.environ[key] = value

    common.post_log(f"VS environment activated (VisualStudioVersion={os.environ.get('VisualStudioVersion', '?')})")


# ═══════════════════════════════════════════════════════════════════════════════
#  winget
# ═══════════════════════════════════════════════════════════════════════════════

def _ensure_winget() -> bool:
    """
    Check if winget is usable.  On Windows Server, cloud-init runs as SYSTEM
    which cannot execute MSIX apps — so this usually returns False.
    """
    if shutil.which("winget"):
        try:
            r = subprocess.run(
                ["winget", "--version"],
                capture_output=True, text=True, timeout=15,
            )
            if r.returncode == 0:
                common.post_log(f"winget available: {r.stdout.strip()}")
                return True
        except Exception:
            pass
    common.post_log("winget not usable (SYSTEM account cannot run MSIX apps)")
    return False


# ═══════════════════════════════════════════════════════════════════════════════
#  Standalone tool installers (fallback when winget is unavailable)
# ═══════════════════════════════════════════════════════════════════════════════

def _install_git_standalone():
    """Download and silently install Git for Windows (portable) if not already available."""
    if shutil.which("git"):
        common.post_log(f"Git already available: {shutil.which('git')}")
        return
    common.post_log("Installing Git for Windows (portable)...")
    git_ver = "2.47.1"
    git_url = f"https://github.com/git-for-windows/git/releases/download/v{git_ver}.windows.1/PortableGit-{git_ver}-64-bit.7z.exe"
    git_dir = common.WORK_DIR / "PortableGit"
    git_archive = common.WORK_DIR / "PortableGit.exe"
    try:
        common.download(git_url, git_archive)
        common.run(f'"{git_archive}" -y -o"{git_dir}"', check=False)
        git_bin = git_dir / "cmd"
        if git_bin.is_dir():
            os.environ["PATH"] = str(git_bin) + os.pathsep + os.environ["PATH"]
            common.post_log(f"Git installed at {git_bin}")
        else:
            common.post_log("WARNING: Git extraction may have failed")
    except Exception as e:
        common.post_log(f"WARNING: Failed to install Git: {e}")


def _install_cmake_standalone():
    """Download and install CMake if not already available."""
    if shutil.which("cmake"):
        common.post_log(f"CMake already available: {shutil.which('cmake')}")
        return
    common.post_log("Installing CMake...")
    cmake_ver = "3.31.4"
    cmake_url = f"https://github.com/Kitware/CMake/releases/download/v{cmake_ver}/cmake-{cmake_ver}-windows-x86_64.zip"
    cmake_zip = common.WORK_DIR / "cmake.zip"
    cmake_dir = common.WORK_DIR / "cmake"
    try:
        common.download(cmake_url, cmake_zip)
        import zipfile
        with zipfile.ZipFile(cmake_zip, 'r') as zf:
            zf.extractall(cmake_dir)
        for d in cmake_dir.rglob("cmake.exe"):
            bin_dir = str(d.parent)
            os.environ["PATH"] = bin_dir + os.pathsep + os.environ["PATH"]
            common.post_log(f"CMake installed at {bin_dir}")
            break
    except Exception as e:
        common.post_log(f"WARNING: Failed to install CMake: {e}")


def _install_ninja_standalone():
    """Download and install Ninja if not already available."""
    if shutil.which("ninja"):
        common.post_log(f"Ninja already available: {shutil.which('ninja')}")
        return
    common.post_log("Installing Ninja...")
    ninja_url = "https://github.com/ninja-build/ninja/releases/download/v1.12.1/ninja-win.zip"
    ninja_zip = common.WORK_DIR / "ninja.zip"
    ninja_dir = common.WORK_DIR / "ninja"
    try:
        common.download(ninja_url, ninja_zip)
        import zipfile
        with zipfile.ZipFile(ninja_zip, 'r') as zf:
            zf.extractall(ninja_dir)
        os.environ["PATH"] = str(ninja_dir) + os.pathsep + os.environ["PATH"]
        common.post_log(f"Ninja installed at {ninja_dir}")
    except Exception as e:
        common.post_log(f"WARNING: Failed to install Ninja: {e}")


# ═══════════════════════════════════════════════════════════════════════════════
#  Ensure Python is on PATH (for dotnet/runtime native build)
# ═══════════════════════════════════════════════════════════════════════════════

def _ensure_python_on_path():
    """
    Make sure 'python' or 'python3' is discoverable on PATH and that the
    embeddable distribution's restricted sys.path is disabled.

    The embeddable Python ships a ``python3XX._pth`` file that locks down
    sys.path to only the entries listed in it, preventing Python from adding
    the script's directory on startup.  Scripts in dotnet/runtime (e.g.
    genEventPipe.py) rely on sibling imports (``from genEventing import *``)
    which fail under that restriction.  Renaming the ._pth file restores
    normal path behaviour.
    """
    import sys as _sys
    import glob as _glob

    # ── 1. Fix the ._pth lockdown in embeddable Python ──────────────────
    py_dir = os.path.dirname(_sys.executable)
    if py_dir and os.path.isdir(py_dir):
        for pth in _glob.glob(os.path.join(py_dir, "python*._pth")):
            renamed = pth + ".bak"
            try:
                os.rename(pth, renamed)
                common.post_log(f"Renamed embeddable ._pth file: {pth} → {renamed}")
            except OSError as ex:
                common.post_log(f"WARNING: Could not rename {pth}: {ex}")

    # ── 2. Add Python to PATH if needed ─────────────────────────────────
    if shutil.which("python") or shutil.which("python3"):
        common.post_log(f"Python on PATH: {shutil.which('python') or shutil.which('python3')}")
        return
    if py_dir and os.path.isdir(py_dir):
        os.environ["PATH"] = py_dir + os.pathsep + os.environ.get("PATH", "")
        common.post_log(f"Added running Python to PATH: {py_dir} ({_sys.executable})")
    else:
        common.post_log(f"WARNING: Could not determine Python directory from {_sys.executable}")


# ═══════════════════════════════════════════════════════════════════════════════
#  PATH refresh
# ═══════════════════════════════════════════════════════════════════════════════

def _refresh_windows_path():
    """
    Merge Machine and User PATH from the registry into the current process
    PATH so that tools installed by winget become visible, without losing
    inherent system dirs like C:\\Windows\\System32.
    """
    import winreg
    registry_dirs: list[str] = []
    for hive, subkey in [
        (winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        (winreg.HKEY_CURRENT_USER, r"Environment"),
    ]:
        try:
            with winreg.OpenKey(hive, subkey) as key:
                val, _ = winreg.QueryValueEx(key, "Path")
                for d in val.split(os.pathsep):
                    d = d.strip()
                    if d:
                        registry_dirs.append(d)
        except OSError:
            pass
    if not registry_dirs:
        return
    current = set(p.lower() for p in os.environ.get("PATH", "").split(os.pathsep))
    new_entries = [d for d in registry_dirs if d.lower() not in current]
    if new_entries:
        os.environ["PATH"] = os.environ.get("PATH", "") + os.pathsep + os.pathsep.join(new_entries)
    common.post_log(f"Refreshed PATH from registry (+{len(new_entries)} new entries)")


# ═══════════════════════════════════════════════════════════════════════════════
#  Main entry point: install_platform_deps
# ═══════════════════════════════════════════════════════════════════════════════

def install_platform_deps():
    """
    Install all Windows build dependencies, then activate VS environment.
    If winget is available, use it; otherwise fall back to direct downloads.
    """
    use_winget = _ensure_winget()

    if use_winget:
        common.post_log("Installing Windows build dependencies via winget...")
        for pkg in ["Git.Git", "Kitware.CMake", "Ninja-build.Ninja", "Python.Python.3.11"]:
            common.run(f'winget install -e --id {pkg} --accept-source-agreements --accept-package-agreements',
                check=False)
        _refresh_windows_path()

        common.post_log("Installing Visual Studio 2022 Community with C++ workload (this may take 10-20 min)...")
        common.run(
            'winget install -e --id Microsoft.VisualStudio.2022.Community '
            '--accept-source-agreements --accept-package-agreements '
            '--override "'
            '--quiet --wait --norestart '
            '--add Microsoft.VisualStudio.Workload.NativeDesktop '
            '--includeRecommended'
            '"',
            check=False,
        )
    else:
        common.post_log("winget not available — installing tools via direct download...")
        _install_git_standalone()
        _install_cmake_standalone()
        _install_ninja_standalone()
        _ensure_vs_build_tools()

    # Ensure the Python that's running the agent is discoverable by subprocesses
    # (dotnet/runtime's native build requires Python for code generation).
    _ensure_python_on_path()

    # Locate and activate VS environment
    pf86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
    vswhere_exe = os.path.join(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe")

    if not os.path.isfile(vswhere_exe) and shutil.which("vswhere"):
        vswhere_exe = shutil.which("vswhere")

    if os.path.isfile(vswhere_exe):
        result = subprocess.run(
            [vswhere_exe, "-latest", "-prerelease", "-products", "*",
             "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
             "-property", "installationPath"],
            capture_output=True, text=True
        )
        vs_path = result.stdout.strip()
        if vs_path and os.path.isdir(vs_path):
            common.post_log(f"VS 2022 found at {vs_path}")
            _activate_vs_environment(vs_path)
        else:
            common.post_log("WARNING: VS installed but vswhere can't find VC tools — build.cmd may fail")
    else:
        common.post_log("WARNING: vswhere not found after VS install — build.cmd may fail")
