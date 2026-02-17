# EgorBot v2

A web service for running [BenchmarkDotNet](https://benchmarkdotnet.org/) microbenchmarks against [dotnet/runtime](https://github.com/dotnet/runtime) commits and pull requests. It provisions cloud VMs (Azure or AWS), builds `core_root` for each specified commit/PR, runs the benchmarks, and returns the results as Markdown.

## Architecture

```
┌────────────┐       POST /api/jobs        ┌──────────────┐      cloud-init       ┌──────────────┐
│  Client /  │ ─────────────────────────── │EgorBot.Server│ ───────────────────── │  Cloud VM    │
│  Web UI    │ ◄── SSE /api/jobs/{id}/     │  (ASP.NET)   │ ◄── logs/heartbeat ── │  (Agent.py)  │
│            │     logs/stream             │              │ ◄── POST /complete ── │              │
└────────────┘                             └──────────────┘                       └──────────────┘
```

1. Client submits a job with target(s) and commit(s)/PR(s)
2. The service provisions a VM on the appropriate cloud provider
3. A Python agent runs on the VM: clones dotnet/runtime, builds core_roots, runs BDN benchmarks
4. Agent streams logs back via HTTP; uploads results as a zip on completion
5. Service extracts Markdown results from the BDN output

## Targets

Instead of raw OS+arch pairs, jobs use **target names** that map to specific hardware:

| Target | Cloud | Arch | CPU | VM Type |
|---|---|---|---|---|
| `azure_genoa` | Azure | x64 | AMD EPYC 9V74 | Standard_D*ads_v6 |
| `azure_genoasmt1` | Azure | x64 | AMD EPYC 9V74 (SMT1) | Standard_F*ams_v6 |
| `azure_milano` | Azure | x64 | AMD EPYC 7763 | Standard_D*ads_v5 |
| `azure_cascadelake` | Azure | x64 | Intel Cascade Lake | Standard_D*ds_v5 |
| `azure_cobalt100` | Azure | arm64 | Cobalt 100 (Neoverse-N2) | Standard_D*pds_v6 |
| `azure_ampere` | Azure | arm64 | Neoverse-N1 | Standard_D*pds_v5 |
| `aws_sapphirelake` | AWS | x64 | Intel Sapphire Lake | c7i |
| `aws_icelake` | AWS | x64 | Intel Ice Lake | c6i |
| `aws_genoa` | AWS | x64 | AMD EPYC 9R14 | c7a |
| `aws_turin` | AWS | x64 | AMD EPYC 9R45 | m8a |
| `aws_milano` | AWS | x64 | AMD EPYC Milan | c6a |
| `aws_graviton2` | AWS | arm64 | Graviton2 | c6g |
| `aws_graviton3` | AWS | arm64 | Graviton3 | c7g |
| `aws_graviton4` | AWS | arm64 | Graviton4 | c8g |
| `local` | Local | auto | Local machine | — |

**Aliases:** `arm` = `azure_cobalt100`, `intel` = `azure_cascadelake`, `x64` / `amd` = `azure_genoa`, `aws_arm` = `aws_graviton4`

Targets can be prefixed with an OS when it differs from the default (linux): `windows_azure_cobalt100`

## Public API

### `POST /api/jobs` — Submit a benchmark job

```json
{
  "platforms": ["arm", "azure_genoa"],
  "commitsAndPrs": "PR_12345;main",
  "bdnArguments": "--filter *MyBench*",
  "benchmarkCode": "using BenchmarkDotNet.Attributes; ...",
  "useProfiler": false
}
```

**Response** `200 OK`:
```json
{
  "groupId": "aaaaaaaa-...",
  "jobs": [
    { "id": "bbbbbbbb-...", "platform": "azure_cobalt100" },
    { "id": "cccccccc-...", "platform": "azure_genoa" }
  ]
}
```

### `GET /api/jobs` — List recent jobs

Query params: `page` (default 1), `pageSize` (default 20, max 100).

**Response** `200 OK`:
```json
{
  "jobs": [ { "id": "...", "groupId": "...", "status": "Running", "platform": "azure_genoa", ... } ],
  "total": 42,
  "page": 1,
  "pageSize": 20
}
```

### `GET /api/jobs/{id}/status` — Job status

**Response** `200 OK`:
```json
{
  "id": "...",
  "status": "Running",
  "platform": "azure_genoa",
  "commitsAndPrs": "PR_12345;main",
  "createdAt": "...",
  "startedAt": "...",
  "completedAt": null,
  "errorMessage": null,
  "hasResult": false
}
```

Status values: `Pending`, `Provisioning`, `Running`, `Completed`, `Failed`, `TimedOut`, `Cancelled`.

### `GET /api/jobs/{id}/result` — Benchmark results (Markdown)

Returns `text/markdown` with the BDN results table when the job is completed.

### `GET /api/jobs/{id}/logs` — All log entries (JSON)

Returns an array of `{ id, timestamp, message }` objects.

### `GET /api/jobs/{id}/logs/stream` — Live log stream (SSE)

Server-Sent Events endpoint. Each event is a JSON log entry. Sends `{"done": true, "status": "Completed"}` when the job finishes.

### `GET /health` — Health check

Returns `200 OK` with body `healthy`.

## Web UI

The root URL (`/`) serves a static HTML dashboard listing jobs. Individual job pages are at `/jobs/{id}`.

## Configuration

Settings are loaded in order (later sources override earlier):

1. `appsettings.json` — defaults
2. `appsettings.{Environment}.json` — environment-specific
3. `appsettings.Local.json` — **gitignored**, for real credentials
4. Environment variables (use `__` as section separator, e.g. `Telegram__BotToken`)

Key settings:

| Setting | Description |
|---|---|
| `EgorBot:ServiceBaseUrl` | Base URL the agent calls back to |
| `EgorBot:AgentScriptUrl` | URL to the Python agent script |
| `EgorBot:MaxConcurrentJobs` | Max parallel jobs |
| `EgorBot:JobTimeoutMinutes` | Per-job timeout |
| `Azure:SubscriptionId` | Azure subscription for VM provisioning |
| `Aws:AccessKey` / `Aws:SecretKey` | AWS credentials |
| `Telegram:BotToken` | Telegram bot token (optional) |
| `Telegram:AdminChatId` | Telegram chat for notifications (optional) |

## Running locally

```bash
cd src/EgorBot.Server
dotnet run --urls "http://localhost:5000"
```

Use `"local"` as the target to run benchmarks as a local process (no cloud VM).

## Agent

The Python agent (`egorbot-agent.py`) runs on the provisioned VM (or locally). It:

1. Installs OS dependencies
2. Installs .NET SDKs
3. Builds the benchmark project
4. Clones dotnet/runtime, builds `core_root` for each commit/PR
5. Runs BDN with all core_roots
6. Uploads results back to the service
