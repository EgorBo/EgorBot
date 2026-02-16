# EgorBot (v2)

A rewrite of [EgorBot](https://gist.github.com/EgorBo/e73bd616303bfa3782e8baa74c247b23) — a GitHub bot that runs [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) benchmarks on cloud VMs (Azure, AWS) triggered by `@EgorBot` mentions in GitHub PR/issue comments.

## What It Does

1. **Polls GitHub** (`dotnet/runtime` by default) for new `@EgorBot` comments every 30 seconds.
2. **Parses the command** — target platform(s), commit(s), perf flags, BDN arguments, and optional C# benchmark code from a fenced code block.
3. **Provisions cloud VMs** (Azure ARM deployments or AWS EC2 instances) with a generated bash cloud-init script.
4. **Streams logs & metrics** — the remote VM POSTs stdout lines and CPU/memory snapshots back to the bot's HTTP API in real time.
5. **Collects results** — when the VM finishes, it POSTs artifact zips back; the bot marks the job complete, posts a Markdown summary comment on GitHub, and deallocates the VM.

## Architecture Overview

```
┌──────────────┐  polls   ┌─────────────────────┐  provisions  ┌─────────────┐
│    GitHub     │◄────────►│   EgorBot (ASP.NET)  │─────────────►│  Cloud VM   │
│ (PR comments) │          │                     │               │ (Azure/EC2) │
└──────────────┘          │  ┌───────────────┐   │◄──────────────│             │
                          │  │  SQLite DB    │   │  logs/metrics │             │
                          │  │  (Jobs,       │   │  + artifacts  └─────────────┘
                          │  │   SubJobs)    │   │
                          │  └───────────────┘   │
                          │  ┌───────────────┐   │
                          │  │  LogStore     │   │  (in-memory)
                          │  │  (live logs)  │   │
                          │  └───────────────┘   │
                          │  ┌───────────────┐   │
                          │  │  Web UI       │   │  index.html / job.html
                          │  └───────────────┘   │
                          └─────────────────────┘
```

**Single ASP.NET project** (`src/EgorBot`), .NET 10, SQLite via EF Core, Octokit for GitHub.

## Project Structure

```
src/EgorBot/
├── Program.cs                          # DI setup, middleware, startup
├── Api/Endpoints.cs                    # All HTTP endpoints (minimal API)
├── Cloud/
│   ├── ICloudProvider.cs               # Abstraction: Provision / Deallocate
│   └── Implementations/
│       ├── AzureCloudProvider.cs        # Azure VMs via ARM template deployments
│       ├── Ec2CloudProvider.cs          # AWS EC2 instances
│       └── LocalExecution.cs           # Stub for local/dev (not implemented)
├── Data/
│   ├── BotDbContext.cs                 # EF Core DbContext (SQLite)
│   ├── Job.cs                          # Job entity + JobStatus enum
│   └── SubJob.cs                       # SubJob entity + TargetOs/TargetArch/VmCpu enums
├── Services/
│   ├── JobOrchestrator.cs              # Core lifecycle: create → dispatch → complete → post results
│   ├── ScriptGenerator.cs              # Generates the cloud-init bash wrapper script
│   ├── LogStore.cs                     # In-memory live log/metrics storage
│   ├── TimeoutWatchdogService.cs       # Background: marks stuck sub-jobs as timed-out
│   └── GitHub/
│       ├── CommandParser.cs            # Parses @EgorBot commands from comment text
│       ├── GitHubMonitorService.cs     # Background: polls GitHub for new mentions
│       └── GitHubService.cs            # Octokit wrapper (comments, gists, reactions)
└── wwwroot/
    ├── index.html                      # Dashboard — lists all jobs
    └── job.html                        # Job detail — live logs, metrics, status per sub-job
```

## Data Model

### Job
Top-level benchmark request. Fields: `Id`, `Requester`, `Repository`, `PrNumber`, `Commits`, `BenchmarkSnippetUrl`, `RawCommand`, `EnablePerf`, `Status` (Pending → Running → Completed/Failed/TimedOut), `GitHubCommentId`, `GitHubIssueOrPrNumber`, `ResultMarkdown`.

### SubJob
One per target platform within a Job. Fields: `Id`, `JobId`, `TargetOs`, `TargetArch`, `HardwareProfile`, `CloudProvider`, `CloudInstanceId`, `Status` (Provisioning → Running → Completed/Failed/TimedOut/Deallocating), `ErrorMessage`, `ResultArtifactPath`.

A Job is marked complete when **all** its SubJobs reach a terminal status.

## Key Flows

### Command Parsing (`CommandParser.TryParse`)
- Detects `@EgorBot` mention (case-insensitive).
- Extracts platform flags from dash-prefixed tokens (`-amd`, `-arm`, `-intel`, `-wsl_amd`, etc.).
- Parses `-commit`, `-perf`, `-perf_event` flags and BDN args.
- Extracts C# code from fenced `` ```cs `` blocks.
- Defaults to `-amd` (Ubuntu 24.04, x64) if no platform specified.

### Platform Aliases (subset)
| Alias | OS | Arch | Profile | Provider |
|---|---|---|---|---|
| `-amd` | Ubuntu2404 | X64 | amd | (default) |
| `-intel` | Ubuntu2404 | X64 | intel | (default) |
| `-arm` / `-arm64` | Ubuntu2404 | Arm64 | default | (default) |
| `-windows` | Windows2022 | X64 | default | (default) |
| `-wsl_amd` | Ubuntu2404 | X64 | amd | WSL |
| `-wsl` | Ubuntu2404 | X64 | default | WSL |

### Job Orchestration (`JobOrchestrator`)
1. Creates a GitHub Gist for the benchmark snippet.
2. Persists `Job` + `SubJob` records to SQLite.
3. For each SubJob, selects a cloud provider and fires-and-forgets `DispatchSubJobAsync`.
4. `DispatchSubJobAsync` generates a bash script via `ScriptGenerator`, calls `provider.ProvisionAsync(...)`.
5. On completion callback (`CompleteSubJobAsync`): marks SubJob done, deallocates VM, checks if all SubJobs finished → marks Job done, posts result Markdown to GitHub.

### Script Generation (`ScriptGenerator`)
Generates a bash wrapper that:
- Sets environment variables (`EGORBOT_HOST`, `EGORBOT_JOBID`, PR info, commits, perf settings).
- Runs a background log-streamer that tails `agent.log` and POSTs new lines to `/api/subjobs/{id}/logs` every 2 seconds.
- Executes the benchmark body (currently a stub/demo).
- On exit, flushes remaining logs and calls `/api/subjobs/{id}/complete?success=true|false`.

### Cloud Providers
- **Azure** (`AzureCloudProvider`): Deploys ARM templates, creates a resource group per sub-job, injects script as `customData` (cloud-init). Deletes the entire resource group on deallocation.
- **EC2** (`Ec2CloudProvider`): Launches EC2 instances with `UserData` script. Terminates instances on deallocation. Uses a semaphore (max 3 concurrent provisions).
- **Local** (`LocalExecution`): Stub — throws `NotImplementedException`. Serves as the default for dev/testing.

## HTTP API

### Callbacks (called by remote VMs)
| Method | Path | Description |
|---|---|---|
| POST | `/api/subjobs/{subJobId}/complete` | Report sub-job completion; accepts multipart artifact files |
| POST | `/api/subjobs/{subJobId}/logs` | Stream log lines (plain text body) |
| POST | `/api/subjobs/{subJobId}/metrics` | Report CPU/memory snapshot (JSON) |
| POST | `/StopJob` | Legacy compatibility endpoint |

### Web UI API
| Method | Path | Description |
|---|---|---|
| GET | `/api/jobs` | List recent 100 jobs |
| GET | `/api/jobs/{jobId}` | Job detail with sub-jobs |
| GET | `/api/subjobs/{subJobId}/logs?from=N` | Paginated log entries |
| GET | `/api/subjobs/{subJobId}/metrics?from=N` | Paginated metrics snapshots |

### Test Endpoints (no GitHub required)
| Method | Path | Description |
|---|---|---|
| POST | `/api/test/submit` | Submit a synthetic command (JSON body) |
| POST | `/api/test/fake-complete/{subJobId}` | Mark a sub-job done for testing |

## Background Services

- **`GitHubMonitorService`** — Polls GitHub issue/PR comments every 30s. Skips bot's own comments and already-processed ones. Adds 🚀 reaction on acknowledgment.
- **`TimeoutWatchdogService`** — Every 60s checks for sub-jobs in Provisioning/Running state older than `Bot:SubJobTimeoutMinutes` (default 120 min) and marks them timed out.

## Configuration (`appsettings.json`)

| Section | Key | Default | Description |
|---|---|---|---|
| `GitHub` | `Token` | `""` | GitHub PAT (anonymous if empty) |
| `GitHub` | `BotLogin` | `EgorBot` | Bot's GitHub username |
| `GitHub` | `Owner` / `Repo` | `dotnet` / `runtime` | Repository to monitor |
| `GitHub` | `PollIntervalSeconds` | `30` | Polling interval |
| `Bot` | `PublicAddress` | `localhost:5104` | Address VMs use to call back |
| `Bot` | `DefaultCloudProvider` | `Local` | `Local`, `Azure`, or `EC2` |
| `Bot` | `ArtifactsPath` | `artifacts` | Where uploaded artifacts are saved |
| `Bot` | `SubJobTimeoutMinutes` | `120` | Max sub-job runtime before timeout |
| `Azure` | `VmPassword` | `""` | VM admin password |
| `Azure` | `MaxCoresPerInstance` | `8` | Core cap per Azure VM |
| `Aws` | `Region` | `us-east-1` | AWS region |
| `Aws` | `AccessKeyId` / `SecretAccessKey` | `""` | AWS credentials (falls back to env/profile) |
| `Aws` | `Cores` / `DiskSizeGb` | `8` / `64` | EC2 instance sizing |
| `ConnectionStrings` | `BotDb` | `Data Source=egorbot.db` | SQLite connection string |

## Running Locally

```bash
cd src/EgorBot
dotnet run
```

The app starts on `http://0.0.0.0:5104`. Open the dashboard at `http://localhost:5104/`.

### Testing Without GitHub

Use the Python test script or curl:

```bash
# Python helper
python test-benchmark.py --command "-wsl_amd"

# Or directly via curl
curl -X POST http://localhost:5104/api/test/submit \
  -H "Content-Type: application/json" \
  -d '{"command": "-amd", "benchmarkCode": "using BenchmarkDotNet.Attributes;\npublic class Bench { [Benchmark] public int Test() => 42; }"}'
```

The response includes a `jobId` and `dashboardUrl`. Push fake logs and complete:

```bash
curl -X POST http://localhost:5104/api/subjobs/{subJobId}/logs --data-binary "Hello from test"
curl -X POST http://localhost:5104/api/test/fake-complete/{subJobId}?success=true
```

## Status / What's Implemented

- ✅ Command parsing (platforms, commits, perf, BDN args, code blocks)
- ✅ Job/SubJob persistence (SQLite + EF Core)
- ✅ Azure VM provisioning & deallocation (ARM template deployments)
- ✅ AWS EC2 provisioning & deallocation
- ✅ GitHub polling, comment posting, gist creation, reactions
- ✅ Cloud-init script generation (wrapper with log streaming)
- ✅ Live log & metrics streaming (in-memory store + polling API)
- ✅ Web dashboard (job list + job detail with live logs)
- ✅ Timeout watchdog
- ✅ Test endpoints for local development
- ⬜ Real benchmark body in `ScriptGenerator` (currently a stub/demo)
- ⬜ `LocalExecution` provider (throws `NotImplementedException`)
- ⬜ Hetzner cloud provider
- ⬜ Result artifact parsing / richer Markdown reports
- ⬜ dotnet/performance repo integration (filter-based runs)