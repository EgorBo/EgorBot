#!/usr/bin/env python3
"""
macOS-specific helpers for the EgorBot agent.

Exports:
    setup_platform()          — ensure Homebrew is on PATH
    install_platform_deps()   — brew install ninja
"""

import os

# ── Injected by common module's load_platform_module() ──────────────────────
common = None  # type: ignore


def setup_platform():
    """Ensure Homebrew is on PATH for macOS (Helix machines may not have it)."""
    for brew_dir in ("/opt/homebrew/bin", "/usr/local/bin"):
        if os.path.isfile(os.path.join(brew_dir, "brew")) and \
           brew_dir not in os.environ.get("PATH", ""):
            os.environ["PATH"] = brew_dir + os.pathsep + os.environ.get("PATH", "")


def install_platform_deps():
    """Install build dependencies via Homebrew."""
    common.run("brew install ninja", check=False)
