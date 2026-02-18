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

EgorBot will build `dotnet/runtime` for the PR and main (or each commit passed via `-commits`), run the benchmark, and post BDN results back as a comment. `--envvars` and other BDN arguments can be passed through to customize the run.

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
| `-commits SHA1,SHA2,...` | Commits/branches to compare (comma or semicolon-separated). Supports `SHA~N` syntax. |
| `-pr <number>` | Target a specific PR (this argument is implied when running in a PR context). |
| `-profiler` | Enable perf profiler (Linux only). |

### Targets

Targets specify where to run. Format: `{os}_{cloud}_{cpu}`. If `os` is omitted, defaults to `ubuntu24`. If `cloud` is omitted, defaults to `azure`. If no target is specified at all it defaults to `macos26_helix_arm64` (baremetal Apple Silicon via Helix).

You don't have to spell out the full name — EgorBot resolves shorthands:

| Shorthand | Resolves to | Notes |
|---|---|---|
| `-arm` or `-arm64` | `macos26_helix_arm64` | Apple Silicon via Helix |
| `-amd` or `-x64` | `ubuntu24_azure_genoa` | Preferred AMD x64 |
| `-intel` | `ubuntu24_azure_cascadelake` | Preferred Intel x64 |
| `-genoa` | `ubuntu24_azure_genoa` | CPU suffix lookup |
| `-cobalt100` | `ubuntu24_azure_cobalt100` | CPU suffix lookup |
| `-azure_arm` | `ubuntu24_azure_cobalt100` | Cloud + vendor |
| `-windows_intel` | `windows_azure_cascadelake` | OS + vendor |
| `-linux` | `ubuntu24_azure_genoa` | OS-only → preferred default |
| `-windows` | `windows_azure_cascadelake` | OS-only → preferred default |

Full target list:

| Target | Arch | Cloud | CPU |
|---|---|---|---|
| `ubuntu24_azure_genoa` | x64 | Azure | AMD Genoa |
| `ubuntu24_azure_milano` | x64 | Azure | AMD Milano |
| `ubuntu24_azure_cascadelake` | x64 | Azure | Intel Cascade Lake |
| `ubuntu24_azure_cobalt100` | arm64 | Azure | Arm Cobalt 100 |
| `ubuntu24_azure_ampere` | arm64 | Azure | Arm Ampere |
| `windows_azure_cascadelake` | x64 | Azure | Intel Cascade Lake |
| `windows_azure_genoa` | x64 | Azure | AMD Genoa |
| | | | |
| `ubuntu24_aws_sapphirelake` | x64 | AWS | Intel Sapphire Lake |
| `ubuntu24_aws_icelake` | x64 | AWS | Intel Ice Lake |
| `ubuntu24_aws_genoa` | x64 | AWS | AMD Genoa |
| `ubuntu24_aws_turin` | x64 | AWS | AMD Turin |
| `ubuntu24_aws_milano` | x64 | AWS | AMD Milano |
| `ubuntu24_aws_graviton2` | arm64 | AWS | Arm Graviton 2 |
| `ubuntu24_aws_graviton3` | arm64 | AWS | Arm Graviton 3 |
| `ubuntu24_aws_graviton4` | arm64 | AWS | Arm Graviton 4 |
| `windows_aws_icelake` | x64 | AWS | Intel Ice Lake |
| `windows_aws_genoa` | x64 | AWS | AMD Genoa |
| | | | |
| `macos26_helix_arm64` | arm64 | Helix | Apple Silicon |
| `macos26_helix_x64` | x64 | Helix | Intel |
| `ubuntu24_helix_x64` | x64 | Helix | — |
| `ubuntu24_helix_arm64` | arm64 | Helix | Arm |
| `ubuntu24_helix_arm32` | arm32 | Helix | Arm |
| `windows_helix_x64` | x64 | Helix | — |
| `windows_helix_arm64` | arm64 | Helix | Arm |

Multiple targets can be specified in a single command.
NOTE: 

### Default behavior

- **No target** → `macos26_helix_arm64`
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

Compare a range of commits on Apple Silicon via Helix:
```
@EgorBot -arm -commits abc1234...def5678
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
                     egorbot-agent-{platform}.py
```

### Projects

| Project | Description |
|---|---|
| **EgorBot.Server** | REST API + job orchestrator. Provisions cloud infrastructure, waits for agent completion, processes results. Runs on port 5000. |
| **EgorBot.Github** | Polls GitHub for `@EgorBot` mentions in issue/PR comments, parses commands, and calls EgorBot.Server's API. Runs on port 5001. |
| **EgorBot.Shared** | Shared library: target catalog (hardware definitions, aliases), models. |

### Agent (`egorbot-agent-common.py` + platform modules)

A standalone Python 3 script (no pip dependencies) split into a shared entry point (`egorbot-agent-common.py`) and per-platform modules (`egorbot-agent-linux.py`, `egorbot-agent-windows.py`, `egorbot-agent-macos.py`). The server generates a bootstrap script (bash or PowerShell) that downloads both scripts onto the provisioned machine. The agent runs a 6-stage pipeline:

1. **Setup environment** — detect OS/arch, create working directories
2. **Install dependencies** — git, ninja, etc. (+ MinGit download on Windows if needed)
3. **Install .NET SDKs** — .NET 10 + 11 preview via `dotnet-install` scripts
4. **Build benchmarks** — either from a custom C# snippet or from `dotnet/performance`
5. **Build core_roots** — clone `dotnet/runtime`, build for each specified commit/PR
6. **Run benchmarks** — BDN with `--corerun` for each core_root, collect results

The agent sends live logs and heartbeats back to the server, and uploads a results zip on completion.

Requires .NET 10+ SDK. Agent requires Python 3.

## API

See [docs/api.md](docs/api.md) for the public REST API documentation.
