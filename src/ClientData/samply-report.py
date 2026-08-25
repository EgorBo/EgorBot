#!/usr/bin/env python3
"""Convert a Samply profile to self-contained SpeedScope and hot-assembly reports."""

from __future__ import annotations

import argparse
import bisect
import gzip
import json
import queue
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Iterable


SERVER_START_TIMEOUT_SECONDS = 45
API_TIMEOUT_SECONDS = 120
SYMBOL_BATCH_SIZE = 1000


def log(message: str) -> None:
    print(f"[SAMPLY-REPORT] {message}", flush=True)


def load_profile(path: Path) -> dict[str, Any]:
    opener = gzip.open if path.suffix == ".gz" else open
    with opener(path, "rt", encoding="utf-8") as file:
        profile = json.load(file)
    if not isinstance(profile, dict):
        raise ValueError("Samply profile root is not a JSON object")
    return profile


def column_value(table: dict[str, Any], column: str, index: int, default: Any = None) -> Any:
    values = table.get(column)
    if not isinstance(values, list) or index < 0 or index >= len(values):
        return default
    return values[index]


def table_length(table: dict[str, Any]) -> int:
    length = table.get("length", 0)
    return length if isinstance(length, int) and length >= 0 else 0


def string_value(strings: list[Any], index: Any, default: str = "") -> str:
    if isinstance(index, int) and 0 <= index < len(strings):
        value = strings[index]
        if isinstance(value, str):
            return value
    return default


def parse_hex(value: Any) -> int | None:
    if isinstance(value, int):
        return value
    if isinstance(value, str):
        try:
            return int(value, 16)
        except ValueError:
            return None
    return None


def is_jit_library(lib: dict[str, Any] | None) -> bool:
    if not lib:
        return False
    name = str(lib.get("name") or lib.get("debugName") or "").lower()
    return name.startswith("jit-") and name.endswith(".dump")


def library_name(lib: dict[str, Any] | None) -> str:
    if not lib:
        return "unknown"
    name = str(lib.get("name") or lib.get("debugName") or "unknown")
    return Path(name).name


def frame_display_name(record: dict[str, Any], libs: list[dict[str, Any]]) -> str:
    lib_index = record.get("lib_index")
    lib = libs[lib_index] if isinstance(lib_index, int) and 0 <= lib_index < len(libs) else None
    function = str(record.get("function") or record.get("raw_name") or "")
    address = record.get("address")

    if is_jit_library(lib):
        if function and not function.startswith("0x"):
            return function if function.startswith("[") else f"[JIT] {function}"
        suffix = f"0x{address:x}" if isinstance(address, int) else "unknown"
        return f"[JIT] {suffix}"

    if lib:
        suffix = function if function else (
            f"0x{address:x}" if isinstance(address, int) else "unknown"
        )
        return f"{library_name(lib)}!{suffix}"

    if function:
        return function
    if isinstance(address, int):
        return f"0x{address:x}"
    return "[unknown]"


def collect_frame_records(
    profile: dict[str, Any],
) -> tuple[list[list[dict[str, Any]]], list[tuple[int, int]]]:
    libs = profile.get("libs")
    threads = profile.get("threads")
    if not isinstance(libs, list) or not isinstance(threads, list):
        raise ValueError("Samply profile is missing libs or threads")

    records_by_thread: list[list[dict[str, Any]]] = []
    lookups: set[tuple[int, int]] = set()

    for thread_index, thread in enumerate(threads):
        if not isinstance(thread, dict):
            records_by_thread.append([])
            continue

        frame_table = thread.get("frameTable") or {}
        func_table = thread.get("funcTable") or {}
        resource_table = thread.get("resourceTable") or {}
        native_symbols = thread.get("nativeSymbols") or {}
        strings = thread.get("stringArray") or []
        frame_records: list[dict[str, Any]] = []

        for frame_index in range(table_length(frame_table)):
            func_index = column_value(frame_table, "func", frame_index, -1)
            raw_name = ""
            resource_index = -1
            if isinstance(func_index, int) and func_index >= 0:
                raw_name = string_value(
                    strings, column_value(func_table, "name", func_index), ""
                )
                resource_index = column_value(func_table, "resource", func_index, -1)

            lib_index: int | None = None
            if isinstance(resource_index, int) and resource_index >= 0:
                candidate = column_value(resource_table, "lib", resource_index)
                if isinstance(candidate, int) and 0 <= candidate < len(libs):
                    lib_index = candidate

            address = column_value(frame_table, "address", frame_index)
            if not isinstance(address, int) or address < 0:
                address = None

            function: str | None = None
            function_start: int | None = None
            function_size: int | None = None
            native_symbol_index = column_value(frame_table, "nativeSymbol", frame_index)
            if isinstance(native_symbol_index, int) and native_symbol_index >= 0:
                candidate_lib = column_value(
                    native_symbols, "libIndex", native_symbol_index
                )
                if isinstance(candidate_lib, int) and 0 <= candidate_lib < len(libs):
                    lib_index = candidate_lib
                function = string_value(
                    strings,
                    column_value(native_symbols, "name", native_symbol_index),
                    "",
                ) or None
                candidate_start = column_value(
                    native_symbols, "address", native_symbol_index
                )
                if isinstance(candidate_start, int) and candidate_start >= 0:
                    function_start = candidate_start
                candidate_size = column_value(
                    native_symbols, "functionSize", native_symbol_index
                )
                if isinstance(candidate_size, int) and candidate_size > 0:
                    function_size = candidate_size

            record = {
                "thread_index": thread_index,
                "frame_index": frame_index,
                "lib_index": lib_index,
                "address": address,
                "raw_name": raw_name,
                "function": function,
                "function_start": function_start,
                "function_size": function_size,
            }
            frame_records.append(record)

            if lib_index is not None and address is not None:
                lookups.add((lib_index, address))

        records_by_thread.append(frame_records)

    return records_by_thread, sorted(lookups)


def post_json(url: str, payload: dict[str, Any]) -> dict[str, Any]:
    request = urllib.request.Request(
        url,
        data=json.dumps(payload, separators=(",", ":")).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "User-Agent": "EgorBot-samply-report",
        },
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=API_TIMEOUT_SECONDS) as response:
        body = response.read().decode("utf-8")
    result = json.loads(body)
    if not isinstance(result, dict):
        raise ValueError(f"Unexpected API response from {url}")
    return result


def symbolize_addresses(
    server_url: str,
    profile: dict[str, Any],
    lookups: list[tuple[int, int]],
) -> dict[tuple[int, int], dict[str, Any]]:
    libs = profile["libs"]
    memory_map: list[list[str]] = []
    library_remap: dict[int, int] = {}
    for lib_index, lib in enumerate(libs):
        if not isinstance(lib, dict):
            continue
        debug_name = lib.get("debugName")
        breakpad_id = lib.get("breakpadId")
        if isinstance(debug_name, str) and isinstance(breakpad_id, str):
            library_remap[lib_index] = len(memory_map)
            memory_map.append([debug_name, breakpad_id])

    queryable = [pair for pair in lookups if pair[0] in library_remap]
    resolved: dict[tuple[int, int], dict[str, Any]] = {}
    found_modules: dict[str, bool] = {}
    module_errors: dict[str, list[Any]] = {}

    log(
        f"Symbolication input: {len(lookups)} unique addresses, "
        f"{len(queryable)} queryable, {len(memory_map)} libraries"
    )

    for start in range(0, len(queryable), SYMBOL_BATCH_SIZE):
        batch = queryable[start : start + SYMBOL_BATCH_SIZE]
        stack = [[library_remap[lib_index], address] for lib_index, address in batch]
        response = post_json(
            f"{server_url}/symbolicate/v5",
            {"jobs": [{"memoryMap": memory_map, "stacks": [stack]}]},
        )
        results = response.get("results")
        if not isinstance(results, list) or not results:
            log(f"WARNING: symbolication batch {start // SYMBOL_BATCH_SIZE + 1} had no result")
            continue
        result = results[0]
        if not isinstance(result, dict):
            continue

        response_stacks = result.get("stacks")
        frames = (
            response_stacks[0]
            if isinstance(response_stacks, list)
            and response_stacks
            and isinstance(response_stacks[0], list)
            else []
        )
        for fallback_index, frame in enumerate(frames):
            if not isinstance(frame, dict):
                continue
            frame_index = frame.get("frame", fallback_index)
            if not isinstance(frame_index, int) or not 0 <= frame_index < len(batch):
                continue
            function = frame.get("function")
            function_offset = parse_hex(frame.get("function_offset"))
            if not isinstance(function, str) or function_offset is None:
                continue
            lib_index, address = batch[frame_index]
            function_size = parse_hex(frame.get("function_size"))
            resolved[(lib_index, address)] = {
                "function": function,
                "function_start": address - function_offset,
                "function_size": function_size,
                "file": frame.get("file"),
                "line": frame.get("line"),
            }

        current_found = result.get("found_modules")
        if isinstance(current_found, dict):
            found_modules.update(
                {str(key): bool(value) for key, value in current_found.items()}
            )
        current_errors = result.get("module_errors")
        if isinstance(current_errors, dict):
            module_errors.update(
                {
                    str(key): value if isinstance(value, list) else [value]
                    for key, value in current_errors.items()
                }
            )

        log(
            f"Symbolication batch {start // SYMBOL_BATCH_SIZE + 1}: "
            f"{len(batch)} requested, {len(resolved)} total resolved"
        )

    found_count = sum(1 for found in found_modules.values() if found)
    log(
        f"Symbolication complete: {len(resolved)}/{len(queryable)} addresses resolved; "
        f"{found_count}/{len(found_modules)} reported modules found"
    )
    for module, errors in sorted(module_errors.items()):
        summaries = []
        for error in errors[:3]:
            if isinstance(error, dict):
                summaries.append(str(error.get("message") or error.get("name") or error))
            else:
                summaries.append(str(error))
        log(f"WARNING: symbolication errors for {module}: {'; '.join(summaries)}")

    return resolved


def apply_symbolication(
    profile: dict[str, Any],
    records_by_thread: list[list[dict[str, Any]]],
    symbols: dict[tuple[int, int], dict[str, Any]],
) -> None:
    libs = profile["libs"]
    managed_frames = 0
    native_frames = 0
    unresolved_frames = 0

    for records in records_by_thread:
        for record in records:
            lib_index = record.get("lib_index")
            address = record.get("address")
            if isinstance(lib_index, int) and isinstance(address, int):
                symbol = symbols.get((lib_index, address))
                if symbol:
                    record.update(symbol)

            record["display_name"] = frame_display_name(record, libs)
            lib = (
                libs[lib_index]
                if isinstance(lib_index, int) and 0 <= lib_index < len(libs)
                else None
            )
            function = record.get("function")
            record["is_managed"] = (
                is_jit_library(lib)
                and isinstance(function, str)
                and bool(function)
                and not function.startswith("0x")
            )
            record["is_native"] = lib is not None and not is_jit_library(lib)

            if record["is_managed"]:
                managed_frames += 1
            elif record["is_native"] and isinstance(function, str) and function:
                native_frames += 1
            elif lib is not None:
                unresolved_frames += 1

    log(
        f"Frame inventory: {managed_frames} managed JIT frames, "
        f"{native_frames} resolved native frames, {unresolved_frames} unresolved library frames"
    )


def expand_stack(thread: dict[str, Any], stack_index: int) -> list[int]:
    stack_table = thread.get("stackTable") or {}
    prefixes = stack_table.get("prefix") or []
    frames = stack_table.get("frame") or []
    result: list[int] = []
    seen: set[int] = set()
    current: Any = stack_index

    while isinstance(current, int) and 0 <= current < len(frames):
        if current in seen:
            raise ValueError(f"Cycle in Samply stack table at index {current}")
        seen.add(current)
        frame_index = frames[current]
        if isinstance(frame_index, int):
            result.append(frame_index)
        current = prefixes[current] if current < len(prefixes) else None

    result.reverse()
    return result


def positive_sample_weight(samples: dict[str, Any], index: int) -> int:
    weight = column_value(samples, "weight", index, 1)
    return weight if isinstance(weight, int) and weight > 0 else 1


def build_speedscope_and_hot_functions(
    profile: dict[str, Any],
    records_by_thread: list[list[dict[str, Any]]],
) -> tuple[dict[str, Any], list[dict[str, Any]], int, int]:
    threads = profile["threads"]
    interval = profile.get("meta", {}).get("interval", 1.0)
    interval_ms = float(interval) if isinstance(interval, (int, float)) and interval > 0 else 1.0

    shared_frames: list[dict[str, str]] = []
    shared_frame_indexes: dict[str, int] = {}
    speedscope_profiles: list[dict[str, Any]] = []
    hot_functions: dict[tuple[int, int, str], dict[str, Any]] = {}
    total_samples = 0
    mixed_stack_count = 0

    def shared_frame_index(name: str) -> int:
        index = shared_frame_indexes.get(name)
        if index is None:
            index = len(shared_frames)
            shared_frame_indexes[name] = index
            shared_frames.append({"name": name})
        return index

    for thread_index, thread in enumerate(threads):
        if not isinstance(thread, dict):
            continue
        samples = thread.get("samples") or {}
        records = records_by_thread[thread_index]
        output_samples: list[list[int]] = []
        output_weights: list[float] = []
        thread_sample_weight = 0
        stack_cache: dict[int, list[int]] = {}

        for sample_index in range(table_length(samples)):
            stack_index = column_value(samples, "stack", sample_index)
            if not isinstance(stack_index, int):
                continue
            frame_indexes = stack_cache.get(stack_index)
            if frame_indexes is None:
                frame_indexes = expand_stack(thread, stack_index)
                stack_cache[stack_index] = frame_indexes
            frame_indexes = [index for index in frame_indexes if 0 <= index < len(records)]
            if not frame_indexes:
                continue

            weight = positive_sample_weight(samples, sample_index)
            names = [str(records[index]["display_name"]) for index in frame_indexes]
            speedscope_stack = [shared_frame_index(name) for name in names]
            duration = interval_ms * weight
            if output_samples and output_samples[-1] == speedscope_stack:
                output_weights[-1] += duration
            else:
                output_samples.append(speedscope_stack)
                output_weights.append(duration)
            thread_sample_weight += weight
            total_samples += weight

            stack_records = [records[index] for index in frame_indexes]
            if any(record["is_managed"] for record in stack_records) and any(
                record["is_native"] for record in stack_records
            ):
                mixed_stack_count += weight

            leaf = stack_records[-1]
            lib_index = leaf.get("lib_index")
            address = leaf.get("address")
            function_start = leaf.get("function_start")
            function = leaf.get("function")
            if (
                isinstance(lib_index, int)
                and isinstance(address, int)
                and isinstance(function_start, int)
                and isinstance(function, str)
                and function
            ):
                key = (lib_index, function_start, function)
                hot = hot_functions.setdefault(
                    key,
                    {
                        "lib_index": lib_index,
                        "function_start": function_start,
                        "function_size": leaf.get("function_size"),
                        "function": function,
                        "display_name": leaf["display_name"],
                        "self_samples": 0,
                        "addresses": {},
                    },
                )
                candidate_size = leaf.get("function_size")
                if (
                    not isinstance(hot.get("function_size"), int)
                    and isinstance(candidate_size, int)
                    and candidate_size > 0
                ):
                    hot["function_size"] = candidate_size
                hot["self_samples"] += weight
                hot["addresses"][address] = hot["addresses"].get(address, 0) + weight

        if output_samples:
            process_name = str(thread.get("processName") or "process")
            thread_name = str(thread.get("name") or "thread")
            pid = str(thread.get("pid") or "?")
            tid = str(thread.get("tid") or "?")
            speedscope_profiles.append(
                {
                    "type": "sampled",
                    "name": f"{process_name} ({pid}) - {thread_name} ({tid})",
                    "unit": "milliseconds",
                    "startValue": 0,
                    "endValue": sum(output_weights),
                    "samples": output_samples,
                    "weights": output_weights,
                    "_sampleWeight": thread_sample_weight,
                }
            )

    if not speedscope_profiles:
        raise ValueError("The Samply profile contains no non-empty sampled stacks")

    speedscope_profiles.sort(key=lambda item: item["_sampleWeight"], reverse=True)
    for item in speedscope_profiles:
        del item["_sampleWeight"]

    speedscope = {
        "$schema": "https://www.speedscope.app/file-format-schema.json",
        "name": str(profile.get("meta", {}).get("product") or "Samply profile"),
        "activeProfileIndex": 0,
        "exporter": "EgorBot samply-report.py",
        "shared": {"frames": shared_frames},
        "profiles": speedscope_profiles,
    }
    hot = sorted(
        hot_functions.values(),
        key=lambda item: int(item["self_samples"]),
        reverse=True,
    )
    return speedscope, hot, total_samples, mixed_stack_count


def write_speedscope(path: Path, speedscope: dict[str, Any]) -> None:
    collapsed_samples = 0
    for sampled_profile in speedscope["profiles"]:
        if len(sampled_profile["samples"]) != len(sampled_profile["weights"]):
            raise ValueError(f"SpeedScope sample/weight mismatch in {sampled_profile['name']}")
        collapsed_samples += len(sampled_profile["samples"])
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as file:
        json.dump(speedscope, file, ensure_ascii=False, separators=(",", ":"))
        file.write("\n")
    log(
        f"Wrote SpeedScope report: {path} "
        f"({len(speedscope['profiles'])} thread profiles, "
        f"{len(speedscope['shared']['frames'])} shared frames, "
        f"{collapsed_samples} collapsed samples, {path.stat().st_size} bytes)"
    )


def assembly_request(lib: dict[str, Any], hot: dict[str, Any]) -> dict[str, Any]:
    size = hot.get("function_size")
    request: dict[str, Any] = {
        "name": lib.get("name"),
        "codeId": lib.get("codeId"),
        "debugName": lib.get("debugName"),
        "debugId": lib.get("breakpadId"),
        "startAddress": hex(int(hot["function_start"])),
        "size": hex(size if isinstance(size, int) and size > 0 else 1),
        "continueUntilFunctionEnd": not isinstance(size, int) or size <= 0,
    }
    return {key: value for key, value in request.items() if value is not None}


def attribute_instruction_samples(
    hot: dict[str, Any], response: dict[str, Any]
) -> tuple[list[tuple[int, str, int]], int]:
    instructions = response.get("instructions")
    if not isinstance(instructions, list):
        return [], 0
    response_start = parse_hex(response.get("startAddress"))
    response_size = parse_hex(response.get("size"))
    if response_start is None:
        return [], 0

    decoded: list[tuple[int, str]] = []
    for instruction in instructions:
        if (
            isinstance(instruction, list)
            and len(instruction) >= 2
            and isinstance(instruction[0], int)
            and isinstance(instruction[1], str)
        ):
            decoded.append((instruction[0], instruction[1]))
    decoded.sort()
    if not decoded:
        return [], 0

    offsets = [offset for offset, _ in decoded]
    counts = [0] * len(decoded)
    for address, count in hot["addresses"].items():
        relative = int(address) - response_start
        if relative < 0 or (
            isinstance(response_size, int) and response_size > 0 and relative >= response_size
        ):
            continue
        instruction_index = bisect.bisect_right(offsets, relative) - 1
        if instruction_index >= 0:
            counts[instruction_index] += int(count)

    rows = [
        (offset, instruction, counts[index])
        for index, (offset, instruction) in enumerate(decoded)
    ]
    return rows, sum(counts)


def write_assembly_report(
    path: Path,
    server_url: str,
    profile: dict[str, Any],
    hot_functions: list[dict[str, Any]],
    total_samples: int,
    top: int,
) -> int:
    libs = profile["libs"]
    sections: list[str] = []
    successful = 0
    marked_instructions = 0
    attempts = 0
    max_attempts = min(len(hot_functions), max(top * 5, top))

    for hot in hot_functions[:max_attempts]:
        if successful >= top:
            break
        lib_index = hot["lib_index"]
        if not isinstance(lib_index, int) or not 0 <= lib_index < len(libs):
            continue
        lib = libs[lib_index]
        if not isinstance(lib, dict):
            continue

        attempts += 1
        try:
            response = post_json(
                f"{server_url}/asm/v1",
                assembly_request(lib, hot),
            )
        except Exception as error:
            log(f"WARNING: assembly lookup failed for {hot['display_name']}: {error}")
            continue

        if "error" in response:
            log(
                f"WARNING: assembly lookup failed for {hot['display_name']}: "
                f"{response['error']}"
            )
            continue

        rows, attributed = attribute_instruction_samples(hot, response)
        if not rows:
            log(f"WARNING: no instructions returned for {hot['display_name']}")
            continue
        if attributed == 0:
            log(
                f"WARNING: no sampled addresses matched instructions for "
                f"{hot['display_name']}"
            )
            continue

        self_samples = int(hot["self_samples"])
        all_percent = 100.0 * self_samples / total_samples if total_samples else 0.0
        syntax = response.get("syntax")
        syntax_name = syntax[0] if isinstance(syntax, list) and syntax else "unknown syntax"
        arch = str(response.get("arch") or "unknown architecture")
        lines = [
            str(hot["display_name"]),
            f"  {self_samples} self samples ({all_percent:.2f}% of all samples), "
            f"{arch} {syntax_name}",
            "",
            "   self%  samples  offset   instruction",
        ]
        for offset, instruction, count in rows:
            marker = ">>" if count else "  "
            percent = 100.0 * count / self_samples if self_samples else 0.0
            lines.append(
                f"{marker} {percent:6.2f} {count:8d}  +0x{offset:04x}  {instruction}"
            )
            if count:
                marked_instructions += 1

        sections.append("\n".join(lines))
        successful += 1
        log(
            f"Assembly {successful}/{top}: {hot['display_name']} - "
            f"{self_samples} self samples, {attributed} attributed"
        )

    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as file:
        if sections:
            file.write("\n\n".join(sections))
            file.write("\n")
        else:
            file.write("No sampled functions could be disassembled.\n")

    log(
        f"Wrote assembly report: {path} ({successful} functions from {attempts} API attempts, "
        f"{marked_instructions} sampled instructions, {path.stat().st_size} bytes)"
    )
    return marked_instructions


class SamplyServer:
    def __init__(self, samply: Path, profile: Path):
        self._samply = samply
        self._profile = profile
        self._process: subprocess.Popen[str] | None = None
        self._lines: queue.Queue[str | None] = queue.Queue()
        self._reader: threading.Thread | None = None
        self._ready = threading.Event()

    def start(self) -> str:
        command = [
            str(self._samply),
            "load",
            str(self._profile),
            "--no-open",
            "--port",
            "3000+",
            "--verbose",
        ]
        log(f"Starting local Samply symbol server: {' '.join(command)}")
        self._process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        self._reader = threading.Thread(target=self._read_output, daemon=True)
        self._reader.start()

        deadline = time.monotonic() + SERVER_START_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if self._process.poll() is not None:
                raise RuntimeError(
                    f"Samply symbol server exited early with code {self._process.returncode}"
                )
            try:
                line = self._lines.get(timeout=0.5)
            except queue.Empty:
                continue
            if line is None:
                continue
            server_url = self._parse_server_url(line)
            if server_url:
                self._ready.set()
                log(f"Local Samply API: {server_url}")
                return server_url

        raise TimeoutError(
            f"Samply symbol server did not become ready in "
            f"{SERVER_START_TIMEOUT_SECONDS} seconds"
        )

    def _read_output(self) -> None:
        assert self._process is not None
        assert self._process.stdout is not None
        for line in self._process.stdout:
            line = line.rstrip()
            log(f"server: {line}")
            if not self._ready.is_set():
                self._lines.put(line)
        self._lines.put(None)

    @staticmethod
    def _parse_server_url(line: str) -> str | None:
        if "symbolServer=" not in line:
            return None
        try:
            query = urllib.parse.urlsplit(line.strip()).query
            values = urllib.parse.parse_qs(query).get("symbolServer")
            if values:
                return values[0].rstrip("/")
        except ValueError:
            return None
        return None

    def stop(self) -> None:
        if self._process is None:
            return
        if self._process.poll() is None:
            log("Stopping local Samply symbol server")
            self._process.terminate()
            try:
                self._process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self._process.kill()
                self._process.wait(timeout=10)
        if self._reader is not None:
            self._reader.join(timeout=2)
        log(f"Local Samply symbol server exit code: {self._process.returncode}")


def parse_args(argv: Iterable[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    server = parser.add_mutually_exclusive_group(required=True)
    server.add_argument("--samply", type=Path)
    server.add_argument("--server-url")
    parser.add_argument("--speedscope", type=Path, required=True)
    parser.add_argument("--assembly", type=Path, required=True)
    parser.add_argument("--top", type=int, default=20)
    return parser.parse_args(argv)


def main(argv: Iterable[str] | None = None) -> int:
    args = parse_args(argv)
    if args.top < 1:
        raise ValueError("--top must be at least 1")
    if not args.profile.is_file():
        raise FileNotFoundError(f"Profile not found: {args.profile}")

    profile = load_profile(args.profile)
    log(
        f"Loaded {args.profile}: {len(profile.get('threads', []))} threads, "
        f"{len(profile.get('libs', []))} libraries"
    )

    server_process: SamplyServer | None = None
    try:
        if args.server_url:
            server_url = args.server_url.rstrip("/")
        else:
            if not args.samply.is_file():
                raise FileNotFoundError(f"Samply binary not found: {args.samply}")
            server_process = SamplyServer(args.samply, args.profile)
            server_url = server_process.start()

        records_by_thread, lookups = collect_frame_records(profile)
        symbols = symbolize_addresses(server_url, profile, lookups)
        apply_symbolication(profile, records_by_thread, symbols)
        speedscope, hot_functions, total_samples, mixed_stack_count = (
            build_speedscope_and_hot_functions(profile, records_by_thread)
        )
        log(
            f"Sample inventory: {total_samples} weighted samples, "
            f"{mixed_stack_count} mixed managed/native samples, "
            f"{len(hot_functions)} self-hot disassemblable functions"
        )
        write_speedscope(args.speedscope, speedscope)
        marked_instructions = write_assembly_report(
            args.assembly,
            server_url,
            profile,
            hot_functions,
            total_samples,
            args.top,
        )

        if mixed_stack_count == 0:
            raise RuntimeError(
                "No sampled stack contained both a symbolized managed JIT frame "
                "and a native library frame"
            )
        if marked_instructions == 0:
            raise RuntimeError("No sampled instruction was emitted in the assembly report")

        log("Report validation completed successfully")
        return 0
    finally:
        if server_process is not None:
            server_process.stop()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        log(f"ERROR: {type(error).__name__}: {error}")
        sys.exit(1)
