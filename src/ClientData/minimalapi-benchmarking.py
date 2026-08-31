#!/usr/bin/env python3
"""
ASP.NET Core minimal API throughput benchmark for EgorBot.

The fixed workload accepts route, query-string, header, and source-generated JSON
inputs, performs a deterministic quote calculation, and returns JSON. It is
published self-contained once, then each compared Core_Root is overlaid onto a
private copy before bombardier measures it.

Exports:
    run_minimalapi_benchmarks() -- full pipeline, writes *-report-github.md
"""

import json
import os
import re
import shutil
import signal
import socket
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

# Injected by the common module's load_sibling_module().
common = None  # type: ignore

BOMBARDIER_VERSION = "v1.2.6"
BOMBARDIER_URL = (
    "https://github.com/codesenberg/bombardier/releases/download/"
    "{version}/bombardier-{os}-{arch}{extension}"
)

APP_NAME = "MinimalApiBenchmark"
SERVER_HOST = "127.0.0.1"
SERVER_PORT = 5015
BASE_URL = f"http://{SERVER_HOST}:{SERVER_PORT}"
HEALTH_URL = f"{BASE_URL}/health"
BENCH_URL = (
    f"{BASE_URL}/api/customers/48271/quotes"
    "?taxRate=0.19"
    "&asOf=2026-08-31T02%3A10%3A48Z"
    "&currency=eur"
    "&campaign=fall-launch"
)

PERF_SAMPLE_BUDGET = 60000
PERF_SPEEDSCOPE_BUDGET = 3000
PERF_FREQ_MIN = 99
PERF_FREQ_MAX = 999
PERF_LOW_FREQ_MIN = 19
PERF_RECORD_SECS = 10
PERF_LOW_SECS = 5
PERF_STAT_SECS = 6
MAX_PROFILED_RUNTIMES = 4

SAMPLY_TOP_FUNCTIONS = 30
SAMPLY_WARMUP_SECS = 8
SAMPLY_RECORD_SECS = 25
SAMPLY_EXIT_AFTER_SECS = 40
SAMPLY_TIMEOUT_SECS = 20 * 60
SAMPLY_SAMPLE_BUDGET = 30000
SAMPLY_RATE_MIN = 19
SAMPLY_RATE_MAX = 499

APP_ENV = {
    "ASPNETCORE_ENVIRONMENT": "Production",
    "ASPNETCORE_URLS": "",
    "DOTNET_HillClimbing_Disable": "1",
    "DOTNET_EnableWriteXorExecute": "1",
    "DOTNET_gcServer": "1",
}


def _available_cpus() -> list:
    try:
        cpus = sorted(os.sched_getaffinity(0))
        if cpus:
            return cpus
    except (AttributeError, OSError):
        pass
    return list(range(os.cpu_count() or 1))


def _split_cpus(cpus: list) -> tuple:
    if len(cpus) < 2:
        return cpus, cpus
    midpoint = (len(cpus) + 1) // 2
    return cpus[:midpoint], cpus[midpoint:]


def _cpu_list(cpus: list) -> str:
    return ",".join(str(cpu) for cpu in cpus)


def _taskset_prefix(cpus: list) -> list:
    if common.TARGET_OS != "linux" or not shutil.which("taskset"):
        return []
    return ["taskset", "-c", _cpu_list(cpus)]


def _clamp(value: int, low: int, high: int) -> int:
    return max(low, min(high, value))


def _safe(label: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.~-]", "_", label)


def _bombardier_path() -> Path:
    destination = common.WORK_DIR / common.make_exe("bombardier")
    if destination.exists():
        return destination

    arch = "arm64" if common.TARGET_ARCH == "arm64" else "amd64"
    asset_os = "darwin" if common.TARGET_OS == "osx" else common.TARGET_OS
    extension = ".exe" if common.TARGET_OS == "windows" else ""
    url = BOMBARDIER_URL.format(
        version=BOMBARDIER_VERSION,
        os=asset_os,
        arch=arch,
        extension=extension,
    )
    common.post_log(f"[MINIMALAPI] Downloading bombardier ({asset_os}-{arch})...")
    common.download(url, destination)
    if common.TARGET_OS != "windows":
        destination.chmod(0o755)
    return destination


def _publish() -> Path:
    tfm = common.CFG.bench_tfm
    rid_os = "win" if common.TARGET_OS == "windows" else common.TARGET_OS
    rid = f"{rid_os}-{common.TARGET_ARCH}"
    project = Path(__file__).parent / "minimalapi.csproj"
    source = Path(__file__).parent / "minimalapi.cs"
    request = Path(__file__).parent / "minimalapi-request.json"
    nuget_config = Path(__file__).parent / "minimalapi-nuget.config"
    for required in (project, source, request, nuget_config):
        if not required.is_file():
            raise FileNotFoundError(f"Minimal API benchmark payload is missing: {required}")

    publish_dir = common.WORK_DIR / "minimalapi_publish" / rid
    executable = publish_dir / common.make_exe(APP_NAME)
    if executable.exists():
        common.post_log(f"[MINIMALAPI] Already published: {publish_dir}")
        return publish_dir

    common.post_log(
        f"[MINIMALAPI] Publishing fixed workload for {tfm}/{rid} (self-contained)..."
    )
    common.run(
        [
            "dotnet",
            "publish",
            str(project),
            "-c",
            "Release",
            "-f",
            tfm,
            "-r",
            rid,
            "--self-contained",
            "-p:TargetFramework=" + tfm,
            "-p:RestoreConfigFile=" + str(nuget_config),
            "-o",
            str(publish_dir),
        ],
        cwd=common.WORK_DIR,
        shell=False,
    )
    if not executable.exists():
        raise RuntimeError(
            f"Minimal API publish succeeded but {executable.name} was not produced"
        )
    return publish_dir


def _link_tree(source: Path, destination: Path):
    try:
        shutil.copytree(source, destination, copy_function=os.link)
    except Exception:
        shutil.rmtree(destination, ignore_errors=True)
        shutil.copytree(source, destination)


def _make_run_dir(label: str, publish_dir: Path, core_root) -> Path:
    runs_dir = (
        common.WORK_DIR
        / "minimalapi_runs"
        / f"{common.TARGET_OS}-{common.TARGET_ARCH}"
    )
    common.ensure_dirs(runs_dir)
    run_dir = runs_dir / _safe(label)
    if run_dir.exists():
        shutil.rmtree(run_dir)
    _link_tree(publish_dir, run_dir)

    if core_root is None:
        common.post_log(f"[MINIMALAPI] [{label}] Using the published SDK runtime")
        return run_dir

    replaced = 0
    symbols = 0
    for source in sorted(Path(core_root).iterdir()):
        if not source.is_file():
            continue
        destination = run_dir / source.name
        if not destination.exists():
            if source.name.endswith((".dbg", ".dwarf")):
                shutil.copy2(source, destination)
                symbols += 1
            continue
        destination.unlink()
        shutil.copy2(source, destination)
        replaced += 1

    common.post_log(
        f"[MINIMALAPI] [{label}] Replaced {replaced} runtime file(s) from Core_Root"
        f"{f' (+{symbols} native symbol file(s))' if symbols else ''}"
    )
    if replaced == 0:
        raise RuntimeError(
            f"No runtime files were replaced from {core_root}; "
            "the app would silently run on the SDK runtime"
        )
    return run_dir


def _runtime_entries() -> list:
    entries = []
    for label in common.CFG.gh_commits_and_prs:
        core_root = common.CORE_ROOTS_DIR / label
        markers = (
            core_root / common.make_exe("corerun"),
            core_root / "libcoreclr.so",
            core_root / "libcoreclr.dylib",
            core_root / "coreclr.dll",
        )
        if any(marker.exists() for marker in markers):
            entries.append((label, core_root))
        else:
            common.post_log(
                f"[MINIMALAPI] WARNING: no Core_Root for '{label}' -- skipping"
            )
    if not entries:
        common.post_log(
            "[MINIMALAPI] No Core_Roots available -- using the published SDK runtime"
        )
        entries.append(("dotnet-sdk", None))
    return entries


def _server_command(run_dir: Path, app_cpus: list) -> list:
    executable = run_dir / common.make_exe(APP_NAME)
    if not executable.exists():
        raise FileNotFoundError(f"Minimal API executable not found: {executable}")
    if common.TARGET_OS != "windows":
        executable.chmod(0o755)
    return _taskset_prefix(app_cpus) + [
        str(executable),
        "--urls",
        BASE_URL,
    ]


def _free_port():
    if common.TARGET_OS == "linux" and shutil.which("fuser"):
        subprocess.run(
            ["fuser", "-n", "tcp", "-k", str(SERVER_PORT)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    elif common.TARGET_OS == "osx" and shutil.which("lsof"):
        result = subprocess.run(
            ["lsof", f"-tiTCP:{SERVER_PORT}", "-sTCP:LISTEN"],
            capture_output=True,
            text=True,
            errors="replace",
            check=False,
        )
        for value in result.stdout.splitlines():
            try:
                os.kill(int(value), signal.SIGTERM)
            except (ValueError, ProcessLookupError, PermissionError):
                pass
    elif common.TARGET_OS == "windows":
        result = subprocess.run(
            ["netstat", "-ano", "-p", "tcp"],
            capture_output=True,
            text=True,
            errors="replace",
            check=False,
        )
        suffix = f":{SERVER_PORT}"
        for line in result.stdout.splitlines():
            columns = line.split()
            if (
                len(columns) >= 5
                and columns[1].endswith(suffix)
                and columns[3].upper() == "LISTENING"
            ):
                try:
                    pid = int(columns[4])
                    if pid != os.getpid():
                        os.kill(pid, signal.SIGTERM)
                except (ValueError, ProcessLookupError, PermissionError, OSError):
                    pass


def _start_server(
    run_dir: Path,
    app_cpus: list,
    log_path: Path,
    extra_env: dict = None,
):
    command = _server_command(run_dir, app_cpus)
    environment = {**os.environ, **APP_ENV, **(extra_env or {})}
    common.post_log(f"[MINIMALAPI] Starting: {' '.join(command)}")
    log = open(log_path, "w", encoding="utf-8", errors="replace")
    process = subprocess.Popen(
        command,
        cwd=str(run_dir),
        env=environment,
        stdout=log,
        stderr=subprocess.STDOUT,
    )
    process._egorbot_log = log
    return process


def _stop_process(process):
    if process is None:
        return
    try:
        process.terminate()
        process.wait(timeout=20)
    except Exception:
        try:
            process.kill()
            process.wait(timeout=10)
        except Exception:
            pass


def _stop_server(process):
    if process is None:
        return
    _stop_process(process)
    try:
        process._egorbot_log.close()
    except Exception:
        pass
    _free_port()


def _wait_until_ready(process, timeout: int = 120) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if process.poll() is not None:
            return False
        try:
            with socket.create_connection((SERVER_HOST, SERVER_PORT), timeout=1):
                break
        except OSError:
            time.sleep(0.25)
    else:
        return False

    last_error = ""
    while time.time() < deadline:
        if process.poll() is not None:
            return False
        try:
            with urllib.request.urlopen(HEALTH_URL, timeout=5) as response:
                if response.status in (200, 204):
                    response.read()
                    return True
                last_error = f"HTTP {response.status}"
        except urllib.error.HTTPError as error:
            last_error = f"HTTP {error.code}"
        except Exception as error:
            last_error = str(error)
        time.sleep(0.25)

    common.post_log(
        f"[MINIMALAPI] App did not become ready in {timeout}s (last: {last_error})"
    )
    return False


def _load_command(
    bombardier: Path,
    load_cpus: list,
    connections: int,
    duration: int,
) -> list:
    request_body = Path(__file__).parent / "minimalapi-request.json"
    return _taskset_prefix(load_cpus) + [
        str(bombardier),
        "-m",
        "POST",
        "-f",
        str(request_body),
        "-d",
        f"{duration}s",
        "-c",
        str(connections),
        "-t",
        "5s",
        "-l",
        "--insecure",
        "--fasthttp",
        "-p",
        "r",
        "-o",
        "json",
        "--header",
        "Content-Type: application/json",
        "--header",
        "Accept: application/json",
        "--header",
        "X-Tenant: alpine-eu",
        "--header",
        "Connection: keep-alive",
        BENCH_URL,
    ]


def _run_load(
    bombardier: Path,
    load_cpus: list,
    connections: int,
    duration: int,
    output_file,
):
    process = subprocess.run(
        _load_command(bombardier, load_cpus, connections, duration),
        capture_output=True,
        text=True,
        errors="replace",
        check=False,
    )
    output = process.stdout.strip()
    if output_file is not None:
        output_file.write_text(output or process.stderr, encoding="utf-8")
    if process.returncode != 0 or not output:
        common.post_log(
            f"[MINIMALAPI] bombardier failed (exit {process.returncode}): "
            f"{(process.stderr or output)[:500]}"
        )
        return None
    try:
        start = output.index("{")
        return json.loads(output[start:])["result"]
    except Exception as error:
        common.post_log(
            f"[MINIMALAPI] Could not parse bombardier output ({error}): "
            f"{output[:500]}"
        )
        return None


def _start_load(
    bombardier: Path,
    load_cpus: list,
    connections: int,
    duration: int,
):
    return subprocess.Popen(
        _load_command(bombardier, load_cpus, connections, duration),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def _result_errors(result) -> int:
    return int(
        result.get("req1xx", 0)
        + result.get("req3xx", 0)
        + result.get("req4xx", 0)
        + result.get("req5xx", 0)
        + result.get("others", 0)
    )


def _latency_ms(result, percentile: str):
    try:
        return float(result["latency"]["percentiles"][percentile]) / 1000.0
    except (KeyError, TypeError, ValueError):
        return None


def _format_number(value, digits=0):
    if value is None:
        return "n/a"
    return f"{value:,.{digits}f}"


def _median(values):
    present = [value for value in values if value is not None]
    return statistics.median(present) if present else None


def _summarize(label: str, samples: list) -> dict:
    rps = [sample["rps"] for sample in samples]
    mean = statistics.fmean(rps) if rps else 0.0
    stdev = statistics.stdev(rps) if len(rps) > 1 else 0.0
    return {
        "label": label,
        "samples": samples,
        "count": len(rps),
        "mean": mean,
        "stdev": stdev,
        "cv": stdev / mean * 100.0 if mean else 0.0,
        "min": min(rps) if rps else 0.0,
        "max": max(rps) if rps else 0.0,
        "p50": _median([sample["p50"] for sample in samples]),
        "p90": _median([sample["p90"] for sample in samples]),
        "p99": _median([sample["p99"] for sample in samples]),
        "errors": sum(sample["errors"] for sample in samples),
    }


def _write_report(summaries: list, configuration: list, used_core_roots: bool) -> Path:
    lines = [
        "### ASP.NET Core minimal API -- throughput (requests/sec, higher is better)",
        "",
        "| Runtime | RPS | StdDev | Noise (CV) | Min .. Max | Ratio | "
        "Median latency (p50 / p90 / p99) |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]

    baseline = summaries[0]["mean"] if summaries else 0.0
    for summary in summaries:
        if not summary["count"]:
            lines.append(f"| {summary['label']} | **failed** | | | | | |")
            continue
        if summary is summaries[0] or not baseline:
            ratio = "baseline"
        else:
            delta = (summary["mean"] / baseline - 1.0) * 100.0
            ratio = f"{summary['mean'] / baseline:.3f} ({delta:+.1f}%)"
        latency = " / ".join(
            f"{_format_number(summary[key], 2)} ms"
            for key in ("p50", "p90", "p99")
        )
        lines.append(
            f"| {summary['label']} | {_format_number(summary['mean'])} | "
            f"{_format_number(summary['stdev'])} | {summary['cv']:.1f}% | "
            f"{_format_number(summary['min'])} .. "
            f"{_format_number(summary['max'])} | {ratio} | {latency} |"
        )

    sample_count = summaries[0]["count"] if summaries else 0
    lines.extend(
        [
            "",
            f"RPS is the mean of {sample_count} measured intervals per runtime; "
            "*Noise (CV)* is their coefficient of variation.",
        ]
    )

    failed = [summary for summary in summaries if not summary["count"]]
    if failed:
        lines.extend(
            [
                "",
                "WARNING: No measurements for: "
                + ", ".join(summary["label"] for summary in failed)
                + " (see the agent log in the artifacts).",
            ]
        )

    bad = [summary for summary in summaries if summary["errors"]]
    if bad:
        lines.extend(
            [
                "",
                "WARNING: Non-2xx responses were served by: "
                + ", ".join(
                    f"{summary['label']} ({summary['errors']})" for summary in bad
                )
                + "; those numbers are not trustworthy.",
            ]
        )

    lines.extend(
        [
            "",
            "<details>",
            "<summary>Configuration and per-interval results</summary>",
            "",
        ]
    )
    lines.extend(f"- {row}" for row in configuration)
    lines.extend(
        [
            "",
            "| Runtime | Process | Interval | RPS | p50 | p90 | p99 | non-2xx |",
            "|---|---:|---:|---:|---:|---:|---:|---:|",
        ]
    )
    for summary in summaries:
        for sample in summary["samples"]:
            lines.append(
                f"| {summary['label']} | {sample['process']} | "
                f"{sample['round']} | {_format_number(sample['rps'])} | "
                f"{_format_number(sample['p50'], 2)} | "
                f"{_format_number(sample['p90'], 2)} | "
                f"{_format_number(sample['p99'], 2)} | {sample['errors']} |"
            )
    lines.append("")
    if used_core_roots:
        lines.extend(
            [
                "Runtime assemblies come from each commit's `Core_Root`, which is "
                "not ReadyToRun-compiled. Compare rows from this job; do not compare "
                "its absolute RPS with released-runtime measurements.",
                "",
            ]
        )
    lines.extend(["</details>", ""])

    report = common.ARTIFACTS_DIR / "MinimalApi-report-github.md"
    report.write_text("\n".join(lines), encoding="utf-8")
    return report


def _run_linux_profiling(
    bombardier: Path,
    entries: list,
    run_dirs: dict,
    app_cpus: list,
    load_cpus: list,
    connections: int,
    logs_dir: Path,
):
    platform = common._platform_mod
    if platform is None or not hasattr(platform, "ensure_perf"):
        common.post_log("[MINIMALAPI] Linux perf helpers are unavailable -- skipping")
        return
    perf = platform.ensure_perf()
    if perf is None:
        return

    flamegraph_dir = platform.ensure_flamegraph(common.WORK_DIR)
    perf_root = common.ARTIFACTS_DIR / "perf"
    artifact_dir = perf_root / "MinimalApi"
    common.ensure_dirs(perf_root, artifact_dir)
    platform.dump_perf_events(perf, perf_root)

    cores = max(1, len(app_cpus))
    high_frequency = _clamp(
        PERF_SAMPLE_BUDGET // (cores * PERF_RECORD_SECS),
        PERF_FREQ_MIN,
        PERF_FREQ_MAX,
    )
    low_frequency = _clamp(
        PERF_SPEEDSCOPE_BUDGET // (cores * PERF_LOW_SECS),
        PERF_LOW_FREQ_MIN,
        PERF_FREQ_MAX,
    )
    warmup = common.CFG.minimalapi_warmup
    load_seconds = (
        warmup + PERF_RECORD_SECS + PERF_LOW_SECS + PERF_STAT_SECS + 30
    )
    profiled = entries[:MAX_PROFILED_RUNTIMES]

    for label, _ in profiled:
        common.post_log(f"[MINIMALAPI] === perf profiling {label} ===")
        _free_port()
        process = None
        load = None
        log_path = logs_dir / f"{_safe(label)}_perf_server.log"
        try:
            process = _start_server(
                run_dirs[label],
                app_cpus,
                log_path,
                extra_env=platform.perf_profiling_env(),
            )
            if not _wait_until_ready(process):
                common.post_log(
                    f"[MINIMALAPI] [{label}] App failed to start under perf"
                )
                continue
            load = _start_load(
                bombardier, load_cpus, connections, load_seconds
            )
            time.sleep(warmup)
            if process.poll() is not None or load.poll() is not None:
                common.post_log(
                    f"[MINIMALAPI] [{label}] App or load generator exited before perf"
                )
                continue
            platform.record_perf_data(
                perf,
                process.pid,
                artifact_dir,
                label,
                high_freq=high_frequency,
                high_secs=PERF_RECORD_SECS,
                low_freq=low_frequency,
                low_secs=PERF_LOW_SECS,
                stat_secs=PERF_STAT_SECS,
                record_args=common.CFG.perf_record_args,
            )
        finally:
            _stop_process(load)
            _stop_server(process)
            time.sleep(2)

        platform.postprocess_perf_data(
            perf,
            artifact_dir,
            label,
            flamegraph_dir,
            percent_limit=1,
        )


def _run_samply_profiling(
    bombardier: Path,
    entries: list,
    run_dirs: dict,
    app_cpus: list,
    load_cpus: list,
    connections: int,
    logs_dir: Path,
):
    profiler_script = Path(__file__).parent / "profile-samply.sh"
    if not profiler_script.is_file():
        common.post_log(
            f"[MINIMALAPI] Samply wrapper is missing: {profiler_script}"
        )
        return

    platform = common._platform_mod
    artifact_dir = common.ARTIFACTS_DIR / "perf" / "MinimalApi"
    profile_root = common.WORK_DIR / "minimalapi_samply_profiles"
    tools_dir = common.WORK_DIR / "runtime" / "artifacts" / "tools"
    common.ensure_dirs(artifact_dir, profile_root, tools_dir)
    sample_rate = _clamp(
        SAMPLY_SAMPLE_BUDGET
        // (max(1, len(app_cpus)) * SAMPLY_EXIT_AFTER_SECS),
        SAMPLY_RATE_MIN,
        SAMPLY_RATE_MAX,
    )
    common.post_log(
        f"[MINIMALAPI] Samply sampling {len(app_cpus)} core(s) at "
        f"{sample_rate} Hz for up to {SAMPLY_EXIT_AFTER_SECS}s"
    )

    for label, core_root in entries[:MAX_PROFILED_RUNTIMES]:
        if core_root is None:
            common.post_log(
                f"[MINIMALAPI] [{label}] Samply requires a built Core_Root -- skipping"
            )
            continue
        if (
            platform is not None
            and hasattr(platform, "validate_profiler_core_root")
            and not platform.validate_profiler_core_root(core_root)
        ):
            common.post_log(
                f"[MINIMALAPI] [{label}] Core_Root is incomplete for Samply -- skipping"
            )
            continue

        safe_label = _safe(label)
        profile_dir = profile_root / safe_label
        if profile_dir.exists():
            shutil.rmtree(profile_dir)
        common.ensure_dirs(profile_dir)

        command = [
            "bash",
            str(profiler_script),
            *_server_command(run_dirs[label], app_cpus),
        ]
        environment = {
            **os.environ,
            **APP_ENV,
            "DOTNET_EnableWriteXorExecute": "0",
            "BENCHMARK_EXIT_AFTER_SECONDS": str(SAMPLY_EXIT_AFTER_SECS),
            "PROFILE_OUT": str(profile_dir),
            "TOP": str(SAMPLY_TOP_FUNCTIONS),
            "PYTHON_BIN": sys.executable,
            "SAMPLY_RATE": str(sample_rate),
            "SAMPLY_TOOLS_DIR": str(tools_dir),
            "SAMPLY_PROFILE_NAME": f"{label} / ASP.NET minimal API",
        }
        log_path = logs_dir / f"{safe_label}_samply_server.log"
        log = open(log_path, "w", encoding="utf-8", errors="replace")
        process = None
        load = None
        try:
            _free_port()
            common.post_log(f"[MINIMALAPI] === Samply profiling {label} ===")
            process = subprocess.Popen(
                command,
                cwd=str(run_dirs[label]),
                env=environment,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            if not _wait_until_ready(process, timeout=600):
                common.post_log(
                    f"[MINIMALAPI] [{label}] App failed to start under Samply"
                )
                continue

            _run_load(
                bombardier,
                load_cpus,
                connections,
                SAMPLY_WARMUP_SECS,
                None,
            )
            load = _start_load(
                bombardier,
                load_cpus,
                connections,
                SAMPLY_RECORD_SECS + 10,
            )
            process.wait(timeout=SAMPLY_TIMEOUT_SECS)
        except subprocess.TimeoutExpired:
            common.post_log(
                f"[MINIMALAPI] [{label}] Samply exceeded "
                f"{SAMPLY_TIMEOUT_SECS}s"
            )
        finally:
            _stop_process(load)
            _stop_process(process)
            log.close()
            _free_port()
            time.sleep(2)

        speedscope = profile_dir / "flamegraph.speedscope.json"
        assembly = profile_dir / "annotated-asm.txt"
        if speedscope.is_file() and assembly.is_file():
            shutil.copy2(
                speedscope,
                artifact_dir / f"{safe_label}.flamegraph.speedscope.json",
            )
            shutil.copy2(
                assembly,
                artifact_dir / f"{safe_label}.annotated-asm.txt",
            )
            shutil.rmtree(profile_dir, ignore_errors=True)
            common.post_log(
                f"[MINIMALAPI] [{label}] Samply reports generated"
            )
        else:
            status = profile_dir / "run-status.txt"
            if status.is_file():
                shutil.copy2(
                    status,
                    artifact_dir / f"{safe_label}.samply-diagnostics.txt",
                )
            common.post_log(
                f"[MINIMALAPI] [{label}] Samply did not produce both reports"
            )


def _run_profiling(
    bombardier: Path,
    entries: list,
    run_dirs: dict,
    app_cpus: list,
    load_cpus: list,
    connections: int,
    logs_dir: Path,
):
    if common.TARGET_OS == "linux":
        _run_linux_profiling(
            bombardier,
            entries,
            run_dirs,
            app_cpus,
            load_cpus,
            connections,
            logs_dir,
        )
    elif common.TARGET_OS == "osx":
        _run_samply_profiling(
            bombardier,
            entries,
            run_dirs,
            app_cpus,
            load_cpus,
            connections,
            logs_dir,
        )
    else:
        common.post_log(
            "[MINIMALAPI] Profiling is supported only by perf on Linux "
            "and Samply on macOS"
        )


def run_minimalapi_benchmarks():
    if common.TARGET_OS not in ("linux", "osx", "windows"):
        raise RuntimeError(
            f"The minimal API benchmark does not support {common.TARGET_OS}"
        )
    if common.TARGET_ARCH not in ("x64", "arm64"):
        raise RuntimeError(
            f"The minimal API benchmark does not support {common.TARGET_ARCH}"
        )
    if common.TARGET_OS == "osx" and common.TARGET_ARCH != "arm64":
        raise RuntimeError(
            "The minimal API benchmark supports only arm64 on macOS"
        )

    cfg = common.CFG
    cpus = _available_cpus()
    affinity_available = (
        common.TARGET_OS == "linux" and shutil.which("taskset") is not None
    )
    if affinity_available:
        app_cpus, load_cpus = _split_cpus(cpus)
    else:
        app_cpus = load_cpus = cpus
    connections = cfg.minimalapi_connections or max(
        64, min(512, 8 * len(app_cpus))
    )

    common.post_log(
        f"[MINIMALAPI] CPUs available: {len(cpus)} ({_cpu_list(cpus)})"
    )
    if affinity_available:
        common.post_log(
            f"[MINIMALAPI] App cores: {_cpu_list(app_cpus)} | "
            f"load generator core(s): {_cpu_list(load_cpus)}"
        )
    else:
        common.post_log(
            "[MINIMALAPI] CPU affinity is unavailable; app and load generator "
            "share all scheduler-visible CPUs"
        )
    common.post_log(
        f"[MINIMALAPI] Connections: {connections}, "
        f"warmup: {cfg.minimalapi_warmup}s, "
        f"{cfg.minimalapi_processes} process(es) x "
        f"{cfg.minimalapi_rounds} x {cfg.minimalapi_round_duration}s"
    )

    bombardier = _bombardier_path()
    publish_dir = _publish()
    entries = _runtime_entries()
    used_core_roots = any(core_root is not None for _, core_root in entries)
    run_dirs = {
        label: _make_run_dir(label, publish_dir, core_root)
        for label, core_root in entries
    }
    samples = {label: [] for label, _ in entries}
    logs_dir = common.ARTIFACTS_DIR / "minimalapi"
    common.ensure_dirs(logs_dir)

    for process_index in range(1, cfg.minimalapi_processes + 1):
        for label, _ in entries:
            common.post_log(
                f"[MINIMALAPI] === {label} -- process "
                f"{process_index}/{cfg.minimalapi_processes} ==="
            )
            _free_port()
            log_path = (
                logs_dir / f"{_safe(label)}_p{process_index}_server.log"
            )
            process = None
            try:
                process = _start_server(
                    run_dirs[label], app_cpus, log_path
                )
                if not _wait_until_ready(process):
                    tail = ""
                    try:
                        tail = "\n".join(
                            log_path.read_text(
                                encoding="utf-8", errors="replace"
                            ).splitlines()[-30:]
                        )
                    except OSError:
                        pass
                    common.post_log(
                        f"[MINIMALAPI] [{label}] App failed to start:\n{tail}"
                    )
                    continue

                common.post_log(
                    f"[MINIMALAPI] [{label}] Warming up for "
                    f"{cfg.minimalapi_warmup}s..."
                )
                _run_load(
                    bombardier,
                    load_cpus,
                    connections,
                    cfg.minimalapi_warmup,
                    logs_dir
                    / f"{_safe(label)}_p{process_index}_warmup.json",
                )

                for round_index in range(
                    1, cfg.minimalapi_rounds + 1
                ):
                    result = _run_load(
                        bombardier,
                        load_cpus,
                        connections,
                        cfg.minimalapi_round_duration,
                        logs_dir
                        / (
                            f"{_safe(label)}_p{process_index}"
                            f"_r{round_index}.json"
                        ),
                    )
                    if result is None:
                        continue
                    sample = {
                        "process": process_index,
                        "round": round_index,
                        "rps": float(result["rps"]["mean"]),
                        "p50": _latency_ms(result, "50"),
                        "p90": _latency_ms(result, "90"),
                        "p99": _latency_ms(result, "99"),
                        "errors": _result_errors(result),
                    }
                    samples[label].append(sample)
                    common.post_log(
                        f"[MINIMALAPI] [{label}] p{process_index} "
                        f"interval {round_index}: {sample['rps']:,.0f} RPS "
                        f"(p50 {_format_number(sample['p50'], 2)} ms"
                        f"{', ' + str(sample['errors']) + ' non-2xx' if sample['errors'] else ''})"
                    )
            finally:
                _stop_server(process)
                time.sleep(2)

    summaries = [
        _summarize(label, samples[label]) for label, _ in entries
    ]
    affinity_description = (
        f"{len(cpus)} core(s) visible -- app pinned to "
        f"`{_cpu_list(app_cpus)}`, bombardier pinned to "
        f"`{_cpu_list(load_cpus)}`"
        if affinity_available
        else f"{len(cpus)} core(s) visible -- app and bombardier shared all CPUs"
    )
    configuration = [
        "Fixed POST `/api/customers/{customerId}/quotes`: route + float, "
        "DateTimeOffset and string query parameters + header + nested JSON body",
        "System.Text.Json source-generated metadata contracts with reflection "
        "serialization disabled; JSON response includes calculated quote data",
        f"`{common.TARGET_OS}-{common.TARGET_ARCH}`, {cfg.bench_tfm}, "
        "self-contained",
        affinity_description,
        f"{connections} connections, {cfg.minimalapi_warmup}s warmup, "
        f"{cfg.minimalapi_processes} process(es) x "
        f"{cfg.minimalapi_rounds} x "
        f"{cfg.minimalapi_round_duration}s measured",
        "`DOTNET_HillClimbing_Disable=1`, `DOTNET_gcServer=1`",
    ]
    report = _write_report(summaries, configuration, used_core_roots)
    common.post_log(f"[MINIMALAPI] Report written to {report.name}")

    if not any(summary["count"] for summary in summaries):
        raise RuntimeError(
            "The minimal API benchmark produced no measurements"
        )

    if cfg.perf_enabled:
        try:
            _run_profiling(
                bombardier,
                entries,
                run_dirs,
                app_cpus,
                load_cpus,
                connections,
                logs_dir,
            )
        except Exception as error:
            common.post_log(
                f"[MINIMALAPI] Profiling failed "
                f"({type(error).__name__}: {error}); "
                "throughput results are unaffected"
            )
