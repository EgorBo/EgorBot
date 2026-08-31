# EgorBot v2

Benchmark-as-a-service for [dotnet/runtime](https://github.com/dotnet/runtime).
Triggered from GitHub comments, runs BDN microbenchmarks on dedicated hardware, and posts results back.
Mostly intended for triaging performance regressions/improvements from PRs (before/after).

## GitHub Usage

Mention `@EgorBot` in a PR or issue comment with a C# benchmark snippet:

````
@EgorBot -arm -amd --envvars DOTNET_JitDisasm:Bench

```cs
using BenchmarkDotNet.Attributes;

public class MyBenchmarks
{
    [Benchmark]
    public void Bench() { /* ... */ }
}
```
````

EgorBot will build `dotnet/runtime` for the PR and main (or each commit passed via `-commits`), run the benchmark, and post BDN results back as a comment. `--envvars` and [other BDN arguments](https://benchmarkdotnet.org/articles/guides/console-args.html) can be passed through to customize the run.

### Command format

```
@EgorBot [targets...] [options...] [BDN arguments...]
```

Everything after `@EgorBot` on the same line is parsed as space-separated tokens.
A fenced code block (` ```cs `) in the same comment provides the benchmark source.
Once EgorBot-specific options are no longer recognized, the remaining tokens are passed verbatim as arguments to BDN (e.g. `--filter "*MyBench*"`).

### Options

| Option | Description |
|---|---|
| `orchard` | Run the [OrchardCore CMS](https://github.com/OrchardCMS/OrchardCore) throughput benchmark instead of BenchmarkDotNet (see below). |
| `minimalapi` | Run the fixed ASP.NET Core minimal API JSON throughput benchmark instead of BenchmarkDotNet (see below). |
| `-commits SHA1,SHA2,...` | Commits to compare (comma or semicolon-separated). Supports `SHA~N` syntax and ranges. Example: `530201,530201~1` (compare 530201 vs previous commit) or `07e1dc...530201` (range of commits) |
| `-pr <number>` | Target a specific PR (this argument is implied when running in a PR context). |
| `-profiler` | Run an extra profiling pass: Linux `perf` or macOS Samply. |
| `-perf_events a,b,c` | Custom events for `perf stat` (implies `-profiler`, Linux only). Example: `-perf_events l1d_cache,l1d_cache_refill,l2d_cache_refill,bus_access,cycles,instructions`. The events a machine supports are listed in the `perf_events.txt` artifact attached to every profiled run. |

For custom BDN snippets, macOS Samply profiling produces a self-contained SpeedScope flamegraph
and sampled annotated assembly for every runtime. It uses the same published benchmark bits for
each run and applies the temporary CoreCLR jitdump-discovery fix to every compared revision.

**NOTE:** 32-bit arm and windows targets are currently not available (let me know if you need them).</br>
**NOTE:** mono runtime is not currently supported</br>
**NOTE:** NativeAOT (NAOT) runtime is not currently supported

### Targets

Targets specify where to run. Format: `{os}_{cloud}_{cpu}`. If `os` is omitted, defaults to `ubuntu24`. If `cloud` is omitted, defaults to `azure` (or `helix` for macOS). If no target is specified at all it defaults to `macos15_helix_arm64` (baremetal Apple Silicon via Helix).

You don't have to spell out the full name — EgorBot resolves shorthands:

| Shorthand | Resolves to | Notes |
|---|---|---|
| `-arm` or `-arm64` | `macos15_helix_arm64` | Apple Silicon via Helix |
| `-amd` or `-x64` | `ubuntu24_azure_turin` | Preferred AMD x64 |
| `-intel` | `ubuntu24_azure_emeraldrapids` | Preferred Intel x64 |
| | | |
| `-linux_x64` | `ubuntu24_azure_turin` | Linux AMD x64 |
| `-linux_arm64` | `ubuntu24_azure_cobalt100` | Linux Arm64 |
| `-windows_x64` | `windows_azure_turin` | Windows AMD x64 |
| `-windows_arm64` | `windows_azure_cobalt100` | Windows Arm64 |
| `-osx_arm64` | `macos26_helix_arm64` | macOS Apple Silicon |
| `-osx_x64` | `macos15_helix_x64` | macOS Intel x64 |

**NOTE:** 32-bit arm and windows targets are currently not available (let me know if you need them).

Full target list:

| Target | Arch | Cloud | CPU |
|---|---|---|---|
| `ubuntu24_azure_turin` | x64 | Azure | AMD Turin |
| `ubuntu24_azure_genoa` | x64 | Azure | AMD Genoa |
| `ubuntu24_azure_milano` | x64 | Azure | AMD Milano |
| `ubuntu24_azure_emeraldrapids` | x64 | Azure | Intel Emerald Rapids |
| `ubuntu24_azure_cascadelake` | x64 | Azure | Intel Cascade Lake |
| `ubuntu24_azure_cobalt100` | arm64 | Azure | Arm Cobalt 100 |
| `ubuntu24_azure_ampere` | arm64 | Azure | Arm Ampere |
| `windows_azure_emeraldrapids` | x64 | Azure | Intel Emerald Rapids |
| `windows_azure_cascadelake` | x64 | Azure | Intel Cascade Lake |
| `windows_azure_turin` | x64 | Azure | AMD Turin |
| `windows_azure_genoa` | x64 | Azure | AMD Genoa |
| `windows_azure_cobalt100` | arm64 | Azure | Arm Cobalt 100 |
| `windows_azure_ampere` | arm64 | Azure | Arm Ampere |
| | | | |
| `ubuntu24_aws_sapphirelake` | x64 | AWS | Intel Sapphire Lake |
| `ubuntu24_aws_icelake` | x64 | AWS | Intel Ice Lake |
| `ubuntu24_aws_genoa` | x64 | AWS | AMD Genoa |
| `ubuntu24_aws_turin` | x64 | AWS | AMD Turin |
| `ubuntu24_aws_milano` | x64 | AWS | AMD Milano |
| `ubuntu24_aws_graviton2` | arm64 | AWS | Arm Graviton 2 |
| `ubuntu24_aws_graviton3` | arm64 | AWS | Arm Graviton 3 |
| `ubuntu24_aws_graviton4` | arm64 | AWS | Arm Graviton 4 |
| `ubuntu24_aws_graviton5` | arm64 | AWS | Arm Graviton 5 |
| `windows_aws_icelake` | x64 | AWS | Intel Ice Lake |
| `windows_aws_genoa` | x64 | AWS | AMD Genoa |
| | | | |
| `macos15_helix_arm64` | arm64 | Helix | Apple Silicon |
| `macos15_helix_x64` | x64 | Helix | Intel |
| `macos26_helix_arm64` | arm64 | Helix | Apple Silicon |
| `ubuntu24_helix_x64` | x64 | Helix | — |
| `ubuntu24_helix_arm64` | arm64 | Helix | Arm |
| `ubuntu24_helix_arm32` | arm32 | Helix | Arm |
| `windows_helix_x64` | x64 | Helix | — |
| `windows_helix_arm64` | arm64 | Helix | Arm |

Multiple targets can be specified in a single command.</br>
**NOTE:** Use AWS targets only when absolutely necessary since these targets are not free for me.

### Default behavior

- **No target** → `macos15_helix_arm64`
- **In a PR comment with no `-commits`** → automatically compares `PR_<number>` vs `main`
- **No code block** → runs benchmarks from [dotnet/performance](https://github.com/dotnet/performance)
- **Unconsumed tokens** after options/targets are passed as BDN arguments (e.g. `--filter "*MyBench*"`)

### Examples

Compare a PR against main on ARM:
```
@EgorBot -arm
```

Compare two specific commits on AMD Genoa:
```
@EgorBot -genoa -commits abc1234,def5678
```

Compare a specific commit against its previous commit on Cobalt 100:
```
@EgorBot -azure_arm -commits abc1234,abc1234~1
```

Compare a range of commits on Apple Silicon via Helix for a specific dotnet/performance benchmark:
```
@EgorBot -arm -commits abc1234...def5678 --filter "*MyBench*"
```

## OrchardCore benchmark (`orchard`)

Instead of microbenchmarks, EgorBot can run a full ASP.NET Core app —
[OrchardCore CMS](https://github.com/OrchardCMS/OrchardCore) (Blog recipe, SQLite) — and report
requests/sec for each runtime build:

```
@EgorBot orchard -arm
```

No code snippet and no BDN arguments are needed (both are rejected). What happens on the machine:

1. OrchardCore is cloned at a pinned commit and published **self-contained** for the target RID.
2. For every commit/PR, the runtime files in a private copy of that publish are replaced with the
   ones from its `Core_Root` — so the same app binaries run on each runtime under test.
3. On Linux, the app is pinned with `taskset` to all cores but one and
   [bombardier](https://github.com/codesenberg/bombardier) runs on the remaining core. macOS has no
   equivalent hard CPU affinity, so both processes use all scheduler-visible CPUs.
4. After a warmup, several measured intervals are collected across two server processes. The report
   contains mean RPS, standard deviation, the coefficient of variation (**noise level**),
   min/max and latency percentiles.

| | |
|---|---|
| Targets | **Linux and macOS x64/arm64.** `-arm` and the default target mean `macos15_helix_arm64`, as with other benchmarks |
| Commits | required — run it from a PR, or pass `-pr <number>` / `-commits SHA1,SHA2` |
| `-profiler` | supported on Linux — see below; skipped on macOS |
| `-gcprofiler` | supported on Linux and macOS — separate dotnet-trace GC pass |
| `-perf_events a,b,c` | supported on Linux — custom events for `perf stat` (implies `-profiler`) |

With `-profiler`, an extra pass runs after the measurements: each runtime is started again
(this time with frame pointers, perf maps and W^X disabled), warmed up, put under load and
sampled with `perf record` / `perf stat`. It produces the same artifacts as a BDN run —
annotated hot assembly, flamegraph, function report, counters and a speedscope profile per
runtime. It is a *separate* run precisely because those JIT knobs would skew the RPS numbers.

With `-gcprofiler`, each runtime gets another independent server process and load pass.
After warmup, EgorBot captures a 30-second `dotnet-trace` GC trace and reports collection
counts by generation, p95/p99 and total pause time, time paused, and allocated bytes.
Outlier-prone maximum-pause and peak-heap metrics are omitted from the comparison table.
Raw `.nettrace` and metrics JSON files are linked from the tracking issue.
`-gcprofiler` and `-profiler` can be enabled together.

Example:

```
@EgorBot orchard -amd -commits abc1234,abc1234~1
@EgorBot orchard -arm -profiler
@EgorBot orchard -arm -gcprofiler
@EgorBot orchard -amd -profiler -gcprofiler
```

## ASP.NET Core minimal API benchmark (`minimalapi`)

This fixed macro-benchmark measures a realistic JSON POST endpoint without a database:

```
@EgorBot minimalapi -amd
```

The endpoint binds a customer ID from the route; float, `DateTimeOffset`, and string values
from the query; a tenant from a header; and a nested JSON quote request containing strings,
dates, floats, quantities, customer data, shipping data, and several line items. It calculates
discounts, tax, shipping, and delivery dates, then serializes a JSON response. Both request and
response use System.Text.Json source-generated metadata contracts, with reflection serialization
disabled.

Like OrchardCore, the app is published self-contained once and copied per runtime. Runtime files
in each copy are replaced from the corresponding `Core_Root`, then bombardier collects throughput
and latency intervals from fresh server processes. Hill climbing is disabled for stable warmup;
tiered compilation remains enabled. On Linux, the available CPUs are split evenly between the app
and bombardier. The load defaults to 8 connections per app core, with a minimum of 64 and maximum
of 512.

| | |
|---|---|
| Targets | **Linux and Windows x64/arm64; macOS arm64.** macOS x64 and 32-bit targets are rejected |
| Commits | required — run it from a PR, or pass `-pr <number>` / `-commits SHA1,SHA2` |
| `-profiler` | Linux `perf` or macOS Samply, in a separate pass; not available on Windows |
| `-perf_events a,b,c` | custom Linux `perf stat` events (implies `-profiler`) |
| `-gcprofiler` | not supported |

Examples:

```
@EgorBot minimalapi -amd -commits abc1234,abc1234~1
@EgorBot minimalapi -windows_arm64 -commits abc1234,abc1234~1
@EgorBot minimalapi -arm -profiler
```


## Architecture

```
┌─────────────────────┐       ┌─────────────────────┐
│  EgorBot.Github     │───────│  EgorBot.Server     │
│ (Polls GH comments) │       │ (Orchestrates jobs) │
└─────────────────────┘       └──────┬──────────────┘
                                     │
                    ┌────────────────┼───────────────┐
               ┌──────────┐   ┌──────────┐   ┌───────────┐
               │  Azure   │   │   AWS    │   │   Helix   │
               │  VMs     │   │ Instances│   │ Work Items│
               └────┬─────┘   └────┬─────┘   └─────┬─────┘
                    └──────────────┼───────────────┘
                           bdn-benchmarking-*.py
```

### Projects

| Project | Description |
|---|---|
| **EgorBot.Server** | REST API + job orchestrator. Provisions cloud infrastructure, waits for agent completion, processes results. Runs on port 5000. |
| **EgorBot.Github** | Polls GitHub for `@EgorBot` mentions in issue/PR comments, parses commands, and calls EgorBot.Server's API. Runs on port 5001. |
| **EgorBot.Shared** | Shared library: target catalog (hardware definitions, aliases), models. |

## API

See [docs/api.md](docs/api.md) for the public REST API documentation.
