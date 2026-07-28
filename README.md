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
| `-commits SHA1,SHA2,...` | Commits to compare (comma or semicolon-separated). Supports `SHA~N` syntax and ranges. Example: `530201,530201~1` (compare 530201 vs previous commit) or `07e1dc...530201` (range of commits) |
| `-pr <number>` | Target a specific PR (this argument is implied when running in a PR context). |
| `-profiler` | Enable perf profiler (Linux only, quite fragile, use `[EventPipeProfiler(EventPipeProfile.CpuSampling)]` instead). |
| `-perf_events a,b,c` | Custom events for `perf stat` (implies `-profiler`, Linux only). Example: `-perf_events l1d_cache,l1d_cache_refill,l2d_cache_refill,bus_access,cycles,instructions`. The events a machine supports are listed in the `perf_events.txt` artifact attached to every profiled run. |
| `-attempts <n>` | Repeat the whole benchmark run n times. |

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
