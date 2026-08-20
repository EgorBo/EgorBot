#!/usr/bin/env python3
"""
OrchardCore CMS throughput benchmark for the EgorBot agent (Linux x64/arm64 only).

Ported from https://gist.github.com/EgorBo/7add052cc65b786bfc66dafd4c676d8c

Unlike the BDN pipeline this is a *macro* benchmark: a real ASP.NET Core app
(OrchardCore CMS, Blog recipe, SQLite) is published self-contained, its runtime
bits are then replaced with the ones from each commit's Core_Root, and the app is
hammered with bombardier while pinned to a fixed set of cores.

Exports:
    run_orchard_benchmarks()   — full pipeline, writes *-report-github.md into artifacts
"""

import json
import os
import re
import shutil
import socket
import statistics
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

# ── Injected by the common module's load_sibling_module() ───────────────────
common = None  # type: ignore

# ═══════════════════════════════════════════════════════════════════════════════
#  Constants
# ═══════════════════════════════════════════════════════════════════════════════

ORCHARD_REPO = "https://github.com/OrchardCMS/OrchardCore.git"

# Pinned so the benchmark stays comparable over time (same commit the reference
# script uses for .NET 10/11).
ORCHARD_COMMIT = "a71838a02d4cba6c2bca9a584cb2cd09dcc636fa"

BOMBARDIER_VERSION = "v1.2.6"
BOMBARDIER_URL = ("https://github.com/codesenberg/bombardier/releases/download/"
                  "{ver}/bombardier-{os}-{arch}")

SERVER_HOST = "127.0.0.1"
SERVER_PORT = 5014
BENCH_URL_PATH = "/about"

ACCEPT_HEADER = ("Accept: text/plain,text/html;q=0.9,application/xhtml+xml;q=0.9,"
                 "application/xml;q=0.8,*/*;q=0.7")

# perf sampling (profiling run only). The whole app is sampled across every core it
# uses, so a modest frequency already yields plenty of samples; the low-frequency
# pass keeps the speedscope profile small enough for the browser.
# perf sampling (profiling run only). The whole app is sampled across every core it
# uses, so the frequency is derived from the core count: a fixed one would produce a
# few MB on an 8-core VM but hundreds of MB on a 96-core one, which makes
# `perf report`/`perf script` take longer than the benchmark itself.
PERF_SAMPLE_BUDGET = 60000
# The speedscope pass gets its own (much smaller) budget: every sample becomes a
# full collapsed stack line, and ASP.NET stacks are deep, so the file grows into
# tens of MB long before the sample count looks large.
PERF_SPEEDSCOPE_BUDGET = 3000
PERF_FREQ_MIN = 99
PERF_FREQ_MAX = 999
PERF_LOW_FREQ_MIN = 19
PERF_RECORD_SECS = 10
PERF_LOW_SECS = 5
PERF_STAT_SECS = 6
MAX_PROFILED_RUNTIMES = 4

# Environment for the benchmarked app. AutoSetup provisions the tenant on the
# first request so the run needs no manual setup step.
ORCHARD_ENV = {
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__ShellName": "Default",
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__SiteName": "Benchmark",
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__SiteTimeZone": "Europe/Amsterdam",
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__AdminUsername": "admin",
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__AdminEmail": "info@orchardproject.net",
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__AdminPassword": "Password1!",
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__DatabaseProvider": "Sqlite",
    "OrchardCore__OrchardCore_AutoSetup__Tenants__0__RecipeName": "Blog",
    # DATAS costs ~15% on this workload and adds noise, HillClimbing delays the
    # steady state, and thread-pool spin-waiting hides regressions in the runtime.
    "DOTNET_GCDynamicAdaptationMode": "0",
    "DOTNET_HillClimbing_Disable": "1",
    "DOTNET_ThreadPool_UnfairSemaphoreSpinLimit": "0",
    # The app must never pick up an ASPNETCORE_URLS from the agent environment.
    "ASPNETCORE_URLS": "",
    "DOTNET_gcServer": "1",
}

NUGET_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="dotnet-public" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json" />
    <add key="dotnet-libraries" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-libraries/nuget/v3/index.json" />
    <add key="dotnet10" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/index.json" />
    <add key="dotnet10-transport" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10-transport/nuget/v3/index.json" />
    <add key="dotnet11" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet11/nuget/v3/index.json" />
    <add key="dotnet11-transport" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet11-transport/nuget/v3/index.json" />
  </packageSources>
</configuration>
"""


# ═══════════════════════════════════════════════════════════════════════════════
#  CPU affinity
# ═══════════════════════════════════════════════════════════════════════════════

def _available_cpus() -> list:
    """CPUs this agent is actually allowed to run on.

    The VM size is configurable (and containers/cgroups can hand out an arbitrary,
    non-contiguous CPU mask), so never assume 0..N-1: ask the scheduler.
    """
    try:
        cpus = sorted(os.sched_getaffinity(0))
        if cpus:
            return cpus
    except (AttributeError, OSError):
        pass
    return list(range(os.cpu_count() or 1))


def _split_cpus(cpus: list) -> tuple:
    """Split the available CPUs into (app_cpus, load_cpus).

    The load generator gets the last core only — one core saturates far more app
    cores than we will ever have. With very few cores everything shares core 0,
    which is noisy but still runs.
    """
    if len(cpus) >= 3:
        return cpus[:-1], cpus[-1:]
    if len(cpus) == 2:
        return cpus[:1], cpus[1:]
    return cpus, cpus


def _cpu_list(cpus: list) -> str:
    """Format an explicit CPU list for `taskset -c` (never a range: the mask may
    be sparse)."""
    return ",".join(str(c) for c in cpus)


def _clamp(value: int, low: int, high: int) -> int:
    return max(low, min(high, value))


def _taskset_prefix(cpus: list) -> list:
    if not shutil.which("taskset"):
        common.post_log("[ORCHARD] WARNING: taskset not found — running without CPU affinity!")
        return []
    return ["taskset", "-c", _cpu_list(cpus)]


# ═══════════════════════════════════════════════════════════════════════════════
#  Setup: bombardier, OrchardCore checkout, publish
# ═══════════════════════════════════════════════════════════════════════════════

def _bombardier_path() -> Path:
    """Download the bombardier load generator (single static binary)."""
    dest = common.WORK_DIR / "bombardier"
    if dest.exists():
        return dest

    arch = "arm64" if common.TARGET_ARCH == "arm64" else "amd64"
    url = BOMBARDIER_URL.format(ver=BOMBARDIER_VERSION, os=common.TARGET_OS, arch=arch)
    common.post_log(f"[ORCHARD] Downloading bombardier ({arch})...")
    common.download(url, dest)
    dest.chmod(0o755)
    return dest


def _prepare_repo() -> Path:
    """Clone OrchardCore at the pinned commit and point it at the dotnet feeds."""
    repo = common.WORK_DIR / "orchardcore"
    if not repo.is_dir():
        common.post_log(f"[ORCHARD] Cloning OrchardCore ({ORCHARD_COMMIT[:8]})...")
        common.run(f'git clone --no-tags --single-branch "{ORCHARD_REPO}" "{repo}"')
        common.run(f"git reset --hard {ORCHARD_COMMIT}", cwd=repo)
    else:
        common.post_log("[ORCHARD] OrchardCore already cloned")

    # The repo's own NuGet.config <clear/>s the parent feeds, so the daily
    # .NET 11 runtime/ref packs would not be found. Replace it wholesale.
    (repo / "NuGet.config").write_text(NUGET_CONFIG, encoding="utf-8")

    # global.json pins an SDK band that the agent does not install (it installs
    # 10.0 + 11.0 daily). Drop the pin instead of guessing a version.
    gj = repo / "global.json"
    if gj.exists():
        try:
            data = json.loads(gj.read_text(encoding="utf-8"))
            if data.pop("sdk", None) is not None:
                gj.write_text(json.dumps(data, indent=2), encoding="utf-8")
                common.post_log("[ORCHARD] Removed the SDK pin from global.json")
        except Exception as ex:
            common.post_log(f"[ORCHARD] WARNING: could not patch global.json: {ex}")

    return repo


def _publish(repo: Path) -> Path:
    """Publish OrchardCore.Cms.Web self-contained for the current RID."""
    tfm = common.CFG.bench_tfm                      # e.g. net11.0
    ver = tfm.replace("net", "")                    # e.g. 11.0
    rid = f"{common.TARGET_OS}-{common.TARGET_ARCH}"
    csproj = repo / "src" / "OrchardCore.Cms.Web" / "OrchardCore.Cms.Web.csproj"

    publish_dir = repo / "src" / "OrchardCore.Cms.Web" / "bin" / "Release" / tfm / rid / "publish"
    app_dll = publish_dir / "OrchardCore.Cms.Web.dll"
    if app_dll.exists():
        common.post_log(f"[ORCHARD] Already published: {publish_dir}")
        return publish_dir

    common.post_log(f"[ORCHARD] Publishing OrchardCore.Cms.Web for {tfm}/{rid} (self-contained)...")
    common.run(
        f'dotnet publish -c Release --sc -r {rid} '
        f'-p:AspNetCoreTargetFrameworks=net{ver} -p:CommonTargetFrameworks=net{ver} '
        f'-f {tfm} "{csproj}"',
        cwd=repo,
    )

    if not app_dll.exists():
        # Older/newer SDKs occasionally place the publish folder elsewhere.
        found = sorted((repo / "src" / "OrchardCore.Cms.Web" / "bin" / "Release")
                       .glob(f"**/publish/OrchardCore.Cms.Web.dll"))
        if not found:
            raise RuntimeError("OrchardCore publish succeeded but no publish folder was produced")
        publish_dir = found[0].parent

    common.post_log(f"[ORCHARD] Published to {publish_dir}")
    return publish_dir


# ═══════════════════════════════════════════════════════════════════════════════
#  Per-runtime run directory
# ═══════════════════════════════════════════════════════════════════════════════

def _safe(label: str) -> str:
    """Label usable as a file/directory name (commit refs contain '~', PRs '_')."""
    return re.sub(r"[^A-Za-z0-9_.~-]", "_", label)


def _link_tree(src: Path, dst: Path):
    """Copy a directory, hard-linking files where possible (publish is ~200 MB and
    is duplicated once per compared runtime)."""
    try:
        shutil.copytree(src, dst, copy_function=os.link)
    except Exception:
        shutil.rmtree(dst, ignore_errors=True)
        shutil.copytree(src, dst)


def _make_run_dir(label: str, publish_dir: Path, core_root) -> Path:
    """Create a private copy of the published app whose runtime bits come from
    *core_root* (the runtime built for one commit/PR).

    Only files that already exist in the publish folder are replaced, so the app's
    own dependencies (ASP.NET Core, YesSql, ...) are kept while every runtime
    component (libcoreclr, libclrjit, System.Private.CoreLib, System.*.dll, ...)
    comes from the commit under test. Core_Root test infrastructure is never
    injected into the app.
    """
    runs_dir = common.WORK_DIR / "orchard_runs"
    common.ensure_dirs(runs_dir)
    run_dir = runs_dir / _safe(label)
    if run_dir.exists():
        shutil.rmtree(run_dir, ignore_errors=True)

    _link_tree(publish_dir, run_dir)

    if core_root is None:
        common.post_log(f"[ORCHARD] [{label}] Using the published (SDK) runtime")
        return run_dir

    replaced = 0
    symbols = 0
    for src in sorted(Path(core_root).iterdir()):
        if not src.is_file():
            continue
        dst = run_dir / src.name
        if not dst.exists():
            # Separate debug info (libcoreclr.so.dbg, ...) has no counterpart in the
            # publish folder, but perf resolves native runtime frames through it via
            # .gnu_debuglink — without it a profile is a wall of raw addresses.
            if src.name.endswith(".dbg"):
                shutil.copy2(src, dst)
                symbols += 1
            continue
        # The tree is hard-linked: write through would corrupt the pristine publish.
        dst.unlink()
        shutil.copy2(src, dst)
        replaced += 1

    common.post_log(f"[ORCHARD] [{label}] Replaced {replaced} runtime file(s) from Core_Root"
                    f"{f' (+{symbols} debug symbol file(s))' if symbols else ''}")
    if replaced == 0:
        raise RuntimeError(f"No runtime files were replaced from {core_root} — "
                           f"the app would silently run on the SDK runtime")
    return run_dir


# ═══════════════════════════════════════════════════════════════════════════════
#  Server lifecycle
# ═══════════════════════════════════════════════════════════════════════════════

def _free_port(port: int):
    """Kill whatever still listens on the benchmark port (leftovers from a
    previous run would silently serve the load)."""
    if shutil.which("fuser"):
        subprocess.run(f"fuser -n tcp -k {port}", shell=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    common.kill_process_by_name("OrchardCore")


def _start_server(run_dir: Path, app_cpus: list, log_path: Path, extra_env: dict = None):
    """Start the OrchardCore host pinned to *app_cpus*."""
    exe = run_dir / "OrchardCore.Cms.Web"
    if exe.exists():
        exe.chmod(0o755)
        cmd = [str(exe)]
    else:
        cmd = ["dotnet", str(run_dir / "OrchardCore.Cms.Web.dll")]

    cmd += ["--urls", f"http://{SERVER_HOST}:{SERVER_PORT}"]
    cmd = _taskset_prefix(app_cpus) + cmd

    env = {**os.environ, **ORCHARD_ENV, **(extra_env or {})}
    # A stale SQLite/App_Data from a previous runtime must not leak into this run.
    shutil.rmtree(run_dir / "App_Data", ignore_errors=True)

    common.post_log(f"[ORCHARD] Starting: {' '.join(cmd)}")
    log = open(log_path, "w", encoding="utf-8", errors="replace")
    proc = subprocess.Popen(cmd, cwd=str(run_dir), env=env,
                            stdout=log, stderr=subprocess.STDOUT)
    proc._egorbot_log = log  # keep the handle alive until _stop_server
    return proc


def _stop_server(proc, log_path: Path):
    if proc is None:
        return
    try:
        proc.terminate()
        proc.wait(timeout=20)
    except Exception:
        try:
            proc.kill()
            proc.wait(timeout=10)
        except Exception:
            pass
    try:
        proc._egorbot_log.close()
    except Exception:
        pass
    _free_port(SERVER_PORT)


def _wait_until_ready(proc, url: str, timeout: int = 600) -> bool:
    """Wait for the port to open and for the app to answer 200.

    The first request triggers AutoSetup (recipe import + SQLite migrations), which
    takes a while on a cold app, so failures are retried rather than fatal.
    """
    deadline = time.time() + timeout

    # 1. TCP: the host has bound the socket.
    while time.time() < deadline:
        if proc.poll() is not None:
            return False
        try:
            with socket.create_connection((SERVER_HOST, SERVER_PORT), timeout=2):
                break
        except OSError:
            time.sleep(1)
    else:
        return False

    # 2. HTTP: AutoSetup finished and the page renders.
    last_err = ""
    while time.time() < deadline:
        if proc.poll() is not None:
            return False
        try:
            with urllib.request.urlopen(url, timeout=120) as resp:
                if resp.status == 200:
                    resp.read()
                    return True
                last_err = f"HTTP {resp.status}"
        except urllib.error.HTTPError as ex:
            last_err = f"HTTP {ex.code}"
        except Exception as ex:
            last_err = str(ex)
        time.sleep(2)

    common.post_log(f"[ORCHARD] App did not become ready in {timeout}s (last: {last_err})")
    return False


# ═══════════════════════════════════════════════════════════════════════════════
#  Load generation
# ═══════════════════════════════════════════════════════════════════════════════

def _load_cmd(bombardier: Path, load_cpus: list, connections: int, duration: int, url: str) -> list:
    return _taskset_prefix(load_cpus) + [
        str(bombardier),
        "-d", f"{duration}s",
        "-c", str(connections),
        "-t", "2s",
        "-l",
        "--insecure",
        "--fasthttp",
        "-p", "r",
        "-o", "json",
        "--header", ACCEPT_HEADER,
        "--header", "Connection: keep-alive",
        url,
    ]


def _run_load(bombardier: Path, load_cpus: list, connections: int,
              duration: int, url: str, out_file):
    """Run bombardier once and return the parsed `result` object (or None)."""
    cmd = _load_cmd(bombardier, load_cpus, connections, duration, url)
    proc = subprocess.run(cmd, capture_output=True, text=True, errors="replace")
    output = proc.stdout.strip()

    if out_file is not None:
        out_file.write_text(output or proc.stderr, encoding="utf-8")

    if proc.returncode != 0 or not output:
        common.post_log(f"[ORCHARD] bombardier failed (exit {proc.returncode}): "
                        f"{(proc.stderr or output)[:500]}")
        return None

    try:
        # -p r still prints a leading intro line on some builds; take the JSON part.
        start = output.index("{")
        return json.loads(output[start:])["result"]
    except Exception as ex:
        common.post_log(f"[ORCHARD] Could not parse bombardier output ({ex}): {output[:500]}")
        return None


def _start_load(bombardier: Path, load_cpus: list, connections: int, duration: int, url: str):
    """Start bombardier in the background — the profiler needs the app under load
    while it samples it."""
    return subprocess.Popen(_load_cmd(bombardier, load_cpus, connections, duration, url),
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def _stop_load(proc):
    if proc is None:
        return
    try:
        proc.terminate()
        proc.wait(timeout=10)
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass


def _rps(result) -> float:
    return float(result["rps"]["mean"])


def _errors(result) -> int:
    """Non-2xx responses + transport errors — a run full of 500s is meaningless."""
    return int(result.get("req1xx", 0) + result.get("req3xx", 0) +
               result.get("req4xx", 0) + result.get("req5xx", 0) +
               result.get("others", 0))


def _latency_ms(result, percentile: str):
    try:
        return float(result["latency"]["percentiles"][percentile]) / 1000.0
    except Exception:
        return None


# ═══════════════════════════════════════════════════════════════════════════════
#  Reporting
# ═══════════════════════════════════════════════════════════════════════════════

def _fmt(value, digits=0):
    if value is None:
        return "n/a"
    return f"{value:,.{digits}f}"


def _median(values):
    values = [v for v in values if v is not None]
    return statistics.median(values) if values else None


def _summarize(label: str, samples: list) -> dict:
    rps = [s["rps"] for s in samples]
    mean = statistics.fmean(rps) if rps else 0.0
    stdev = statistics.stdev(rps) if len(rps) > 1 else 0.0
    return {
        "label": label,
        "samples": samples,
        "count": len(rps),
        "mean": mean,
        "stdev": stdev,
        "cv": (stdev / mean * 100.0) if mean else 0.0,
        "min": min(rps) if rps else 0.0,
        "max": max(rps) if rps else 0.0,
        "p50": _median([s["p50"] for s in samples]),
        "p90": _median([s["p90"] for s in samples]),
        "p99": _median([s["p99"] for s in samples]),
        "errors": sum(s["errors"] for s in samples),
    }


def _write_report(summaries: list, cfg_rows: list, used_core_roots: bool) -> Path:
    lines = []
    lines.append("### OrchardCore CMS — throughput (requests/sec, higher is better)")
    lines.append("")
    lines.append("| Runtime | RPS | StdDev | Noise (CV) | Min .. Max | Ratio | Median latency (p50 / p90 / p99) |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|")

    baseline = summaries[0]["mean"] if summaries else 0.0
    for s in summaries:
        if not s["count"]:
            lines.append(f"| {s['label']} | **failed** | | | | | |")
            continue
        if s is summaries[0] or not baseline:
            ratio = "baseline"
        else:
            delta = (s["mean"] / baseline - 1.0) * 100.0
            ratio = f"{s['mean'] / baseline:.3f} ({delta:+.1f}%)"
        lat = " / ".join(f"{_fmt(s[p], 2)} ms" for p in ("p50", "p90", "p99"))
        lines.append(
            f"| {s['label']} | {_fmt(s['mean'])} | {_fmt(s['stdev'])} | {s['cv']:.1f}% | "
            f"{_fmt(s['min'])} .. {_fmt(s['max'])} | {ratio} | {lat} |")

    lines.append("")
    lines.append(f"RPS is the mean of {summaries[0]['count'] if summaries else 0} measured intervals per runtime; "
                 "*Noise (CV)* is their coefficient of variation — treat differences "
                 "smaller than the noise as inconclusive.")

    failed = [s for s in summaries if not s["count"]]
    if failed:
        lines.append("")
        lines.append("⚠️ No measurements for: " + ", ".join(s["label"] for s in failed) +
                     " (see the agent log in the artifacts).")

    bad = [s for s in summaries if s["errors"]]
    if bad:
        lines.append("")
        lines.append("⚠️ Non-2xx responses were served by: " +
                     ", ".join(f"{s['label']} ({s['errors']})" for s in bad) +
                     " — the numbers below are not trustworthy.")

    lines.append("")
    lines.append("<details>")
    lines.append("<summary>Configuration and per-interval results</summary>")
    lines.append("")
    for row in cfg_rows:
        lines.append(f"- {row}")
    lines.append("")
    lines.append("| Runtime | Process | Interval | RPS | p50 | p90 | p99 | non-2xx |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|")
    for s in summaries:
        for sample in s["samples"]:
            lines.append(
                f"| {s['label']} | {sample['process']} | {sample['round']} | {_fmt(sample['rps'])} | "
                f"{_fmt(sample['p50'], 2)} | {_fmt(sample['p90'], 2)} | {_fmt(sample['p99'], 2)} | "
                f"{sample['errors']} |")
    lines.append("")
    if used_core_roots:
        lines.append("The runtime assemblies are taken from each commit's `Core_Root`, which is not "
                     "ReadyToRun-compiled — absolute numbers are therefore lower than a released "
                     "runtime; only the comparison between the rows above is meaningful.")
        lines.append("")
    lines.append("</details>")
    lines.append("")

    report = common.ARTIFACTS_DIR / "OrchardCore-report-github.md"
    report.write_text("\n".join(lines), encoding="utf-8")
    return report


# ═══════════════════════════════════════════════════════════════════════════════
#  Profiling (a separate run — perf's JIT knobs would skew the RPS numbers)
# ═══════════════════════════════════════════════════════════════════════════════

def _run_profiling(bombardier: Path, entries: list, run_dirs: dict, app_cpus: list,
                   load_cpus: list, connections: int, url: str, logs_dir: Path):
    """Sample each runtime under load and produce hot asm, flamegraphs and counters.

    This runs after the measurement phase and with its own server processes: the
    JIT knobs perf needs (frame pointers, no W^X, perf maps) change the numbers,
    so they must not be present while RPS is measured.
    """
    platform_mod = common._platform_mod
    if platform_mod is None or not hasattr(platform_mod, "ensure_perf"):
        common.post_log("[ORCHARD] Profiling was requested but this platform has no perf support — skipping")
        return

    perf = platform_mod.ensure_perf()
    if perf is None:
        return

    flamegraph_dir = platform_mod.ensure_flamegraph(common.WORK_DIR)
    perf_out_dir = common.ARTIFACTS_DIR / "perf"
    common.ensure_dirs(perf_out_dir)
    platform_mod.dump_perf_events(perf, perf_out_dir)

    # Directory name = the group the service renders the artifact table under.
    bench_dir = perf_out_dir / "OrchardCore.Cms"
    common.ensure_dirs(bench_dir)

    perf_env = platform_mod.perf_profiling_env()
    warmup = common.CFG.orchard_warmup
    # Keep the load running until well past the last perf command.
    load_secs = (warmup + PERF_RECORD_SECS + PERF_LOW_SECS + PERF_STAT_SECS + 30)

    # Sampling frequency per core, chosen so the whole run stays around
    # PERF_SAMPLE_BUDGET samples no matter how many cores the VM has.
    cores = max(1, len(app_cpus))
    high_freq = _clamp(PERF_SAMPLE_BUDGET // (cores * PERF_RECORD_SECS), PERF_FREQ_MIN, PERF_FREQ_MAX)
    # The speedscope profile has to stay small enough for a browser to open.
    low_freq = _clamp(PERF_SPEEDSCOPE_BUDGET // (cores * PERF_LOW_SECS), PERF_LOW_FREQ_MIN, PERF_FREQ_MAX)
    common.post_log(f"[ORCHARD] Sampling {cores} core(s) at -F {high_freq} for {PERF_RECORD_SECS}s "
                    f"(speedscope pass: -F {low_freq} for {PERF_LOW_SECS}s)")

    # Each runtime costs a full start + warmup + sampling + symbolization pass, so a
    # 10-commit range would spend an hour here. Profile the first few (the baseline
    # and what it is compared against) and say so.
    profiled = entries[:MAX_PROFILED_RUNTIMES]
    if len(profiled) < len(entries):
        common.post_log(f"[ORCHARD] Profiling only the first {len(profiled)} of {len(entries)} "
                        f"runtimes: {', '.join(l for l, _ in profiled)}")

    for label, _ in profiled:
        common.post_log(f"[ORCHARD] === Profiling {label} ===")
        _free_port(SERVER_PORT)
        log_path = logs_dir / f"{_safe(label)}_perf_server.log"
        proc = None
        load = None
        try:
            proc = _start_server(run_dirs[label], app_cpus, log_path, extra_env=perf_env)
            if not _wait_until_ready(proc, url):
                common.post_log(f"[ORCHARD] [{label}] The app failed to start under the profiler, skipping")
                continue

            load = _start_load(bombardier, load_cpus, connections, load_secs, url)
            common.post_log(f"[ORCHARD] [{label}] Warming up for {warmup}s before sampling...")
            time.sleep(warmup)

            if proc.poll() is not None or load.poll() is not None:
                common.post_log(f"[ORCHARD] [{label}] App or load generator died before sampling, skipping")
                continue

            platform_mod.record_perf_data(
                perf, proc.pid, bench_dir, label,
                high_freq=high_freq, high_secs=PERF_RECORD_SECS,
                low_freq=low_freq, low_secs=PERF_LOW_SECS,
                stat_secs=PERF_STAT_SECS)
        finally:
            _stop_load(load)
            # perf inject needs the *complete* jitdump file, so the app has to be
            # gone before the recorded data is symbolized.
            _stop_server(proc, log_path)
            time.sleep(3)

        common.post_log(f"[ORCHARD] [{label}] Symbolizing and rendering artifacts...")
        # A whole web app spreads its time much thinner than a microbenchmark, so a
        # 2% cut-off would leave the annotated assembly nearly empty.
        platform_mod.postprocess_perf_data(perf, bench_dir, label, flamegraph_dir, percent_limit=1)

    common.post_log("[ORCHARD] Profiling completed ✓")


# ═══════════════════════════════════════════════════════════════════════════════
#  Pipeline
# ═══════════════════════════════════════════════════════════════════════════════

def _runtimes() -> list:
    """(label, core_root) for every commit/PR that produced a Core_Root, in the
    order the user asked for (the first one is the baseline)."""
    entries = []
    for item in common.CFG.gh_commits_and_prs:
        core_root = common.CORE_ROOTS_DIR / item
        if (core_root / "corerun").exists() or (core_root / "libcoreclr.so").exists():
            entries.append((item, core_root))
        else:
            common.post_log(f"[ORCHARD] WARNING: no Core_Root for '{item}' — skipping")
    if not entries:
        common.post_log("[ORCHARD] No Core_Roots available — running on the SDK runtime")
        entries.append(("dotnet-sdk", None))
    return entries


def run_orchard_benchmarks():
    if common.TARGET_OS != "linux":
        raise RuntimeError(f"The OrchardCore benchmark is Linux-only (got {common.TARGET_OS})")

    cfg = common.CFG
    cpus = _available_cpus()
    app_cpus, load_cpus = _split_cpus(cpus)
    connections = cfg.orchard_connections or max(8, 8 * len(app_cpus))
    url = f"http://{SERVER_HOST}:{SERVER_PORT}{BENCH_URL_PATH}"

    common.post_log(f"[ORCHARD] CPUs available: {len(cpus)} ({_cpu_list(cpus)})")
    common.post_log(f"[ORCHARD] App cores: {_cpu_list(app_cpus)} | load generator core(s): {_cpu_list(load_cpus)}")
    common.post_log(f"[ORCHARD] Connections: {connections}, warmup: {cfg.orchard_warmup}s, "
                    f"{cfg.orchard_processes} process(es) x {cfg.orchard_rounds} x {cfg.orchard_round_duration}s")

    bombardier = _bombardier_path()
    repo = _prepare_repo()
    publish_dir = _publish(repo)

    entries = _runtimes()
    used_core_roots = any(core_root is not None for _, core_root in entries)
    run_dirs = {label: _make_run_dir(label, publish_dir, core_root) for label, core_root in entries}
    samples = {label: [] for label, _ in entries}

    logs_dir = common.ARTIFACTS_DIR / "orchard"
    common.ensure_dirs(logs_dir)

    # Interleave the processes across runtimes: if the machine drifts (noisy
    # neighbour, thermals), every runtime is affected the same way.
    for process_idx in range(1, cfg.orchard_processes + 1):
        for label, _ in entries:
            common.post_log(f"[ORCHARD] === {label} — process {process_idx}/{cfg.orchard_processes} ===")
            _free_port(SERVER_PORT)
            log_path = logs_dir / f"{_safe(label)}_p{process_idx}_server.log"
            proc = None
            try:
                proc = _start_server(run_dirs[label], app_cpus, log_path)
                if not _wait_until_ready(proc, url):
                    tail = ""
                    try:
                        tail = "\n".join(log_path.read_text(encoding="utf-8", errors="replace")
                                         .splitlines()[-30:])
                    except Exception:
                        pass
                    common.post_log(f"[ORCHARD] [{label}] The app failed to start:\n{tail}")
                    continue

                common.post_log(f"[ORCHARD] [{label}] Warming up for {cfg.orchard_warmup}s...")
                _run_load(bombardier, load_cpus, connections, cfg.orchard_warmup, url,
                          logs_dir / f"{_safe(label)}_p{process_idx}_warmup.json")

                for round_idx in range(1, cfg.orchard_rounds + 1):
                    result = _run_load(bombardier, load_cpus, connections,
                                       cfg.orchard_round_duration, url,
                                       logs_dir / f"{_safe(label)}_p{process_idx}_r{round_idx}.json")
                    if result is None:
                        continue
                    sample = {
                        "process": process_idx,
                        "round": round_idx,
                        "rps": _rps(result),
                        "p50": _latency_ms(result, "50"),
                        "p90": _latency_ms(result, "90"),
                        "p99": _latency_ms(result, "99"),
                        "errors": _errors(result),
                    }
                    samples[label].append(sample)
                    common.post_log(f"[ORCHARD] [{label}] p{process_idx} interval {round_idx}: "
                                    f"{sample['rps']:,.0f} RPS (p50 {_fmt(sample['p50'], 2)} ms"
                                    f"{', ' + str(sample['errors']) + ' non-2xx' if sample['errors'] else ''})")
            finally:
                _stop_server(proc, log_path)
                time.sleep(3)

    summaries = [_summarize(label, samples[label]) for label, _ in entries]
    for s in summaries:
        if s["count"]:
            common.post_log(f"[ORCHARD] {s['label']}: {s['mean']:,.0f} RPS "
                            f"± {s['stdev']:,.0f} ({s['cv']:.1f}%) over {s['count']} intervals")

    cfg_rows = [
        f"OrchardCore `{ORCHARD_COMMIT[:8]}`, Blog recipe, SQLite, `{BENCH_URL_PATH}`, "
        f"{common.TARGET_OS}-{common.TARGET_ARCH}, {cfg.bench_tfm}, self-contained",
        f"{len(cpus)} core(s) visible — app pinned to `{_cpu_list(app_cpus)}`, "
        f"bombardier pinned to `{_cpu_list(load_cpus)}`",
        f"{connections} connections, {cfg.orchard_warmup}s warmup, "
        f"{cfg.orchard_processes} process(es) x {cfg.orchard_rounds} x {cfg.orchard_round_duration}s measured",
        "`DOTNET_GCDynamicAdaptationMode=0` (DATAS off), `DOTNET_HillClimbing_Disable=1`, "
        "`DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`",
    ]
    report = _write_report(summaries, cfg_rows, used_core_roots)
    common.post_log(f"[ORCHARD] Report written to {report.name}")

    if not any(s["count"] for s in summaries):
        raise RuntimeError("The OrchardCore benchmark produced no measurements at all")

    # Profiling is best-effort: it must never cost the user the results above.
    if cfg.perf_enabled:
        try:
            _run_profiling(bombardier, entries, run_dirs, app_cpus, load_cpus,
                           connections, url, logs_dir)
        except Exception as ex:
            common.post_log(f"[ORCHARD] Profiling failed ({type(ex).__name__}: {ex}) — "
                            f"the benchmark results are unaffected")
