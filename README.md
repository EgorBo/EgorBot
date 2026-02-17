# EgorBot v2

Benchmark-as-a-service for [dotnet/runtime](https://github.com/dotnet/runtime).
Triggered from GitHub comments, runs BDN microbenchmarks on dedicated hardware, and posts results back.
Mostly intended for triaging performance regressions/improvements from PRs (before/after).

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
                          egorbot-agent.py
                                         
```

### Projects

| Project | Description |
|---|---|
| **EgorBot.Server** | REST API + job orchestrator. Provisions cloud infrastructure, waits for agent completion, processes results. Runs on port 5000. |
| **EgorBot.Github** | Polls GitHub for `@EgorBot` mentions in issue/PR comments, parses commands, and calls EgorBot.Server's API. Runs on port 5001. |
| **EgorBot.Shared** | Shared library: target catalog (hardware definitions, aliases), models. |

### Agent (`egorbot-agent.py`)

A standalone Python 3 script (no pip dependencies) deployed via gist. The server generates a bootstrap script (bash or PowerShell) that downloads the agent onto the provisioned machine. The agent runs a 6-stage pipeline:

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
