# EgorBot.Server — Public API

Base URL: `http://<host>:5000`

All endpoints return JSON unless otherwise noted.

---

## POST /api/jobs

Start one or more benchmark jobs.

### Request Body

```json
{
  "platforms": ["arm", "windows_x64"],
  "kind": "bdn",
  "commitsAndPrs": "PR_12345;main",
  "bdnArguments": "--filter *MyBenchmark*",
  "benchmarkCode": "using BenchmarkDotNet.Attributes; ...",
  "useProfiler": false,
  "useGcProfiler": false,
  "requestedBy": "user123",
  "sourceUrl": "https://github.com/dotnet/runtime/issues/12345#issuecomment-..."
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `platforms` | `string[]` | **Yes** | Target platforms/aliases. At least one required. Examples: `"arm"`, `"aws_graviton4"`, `"windows_x64"`, `"osx"`. |
| `kind` | `string` | No | `"bdn"` (default) — BenchmarkDotNet microbenchmarks, or `"orchard"` — OrchardCore CMS throughput. `"orchard"` requires a Linux/macOS x64/arm64 platform and a non-empty `commitsAndPrs`, and ignores `bdnArguments` and `benchmarkCode`. |
| `commitsAndPrs` | `string` | **Yes** | Semicolon-separated commits or PRs to compare. PRs prefixed with `PR_`. Can be empty (runs with default SDK). |
| `bdnArguments` | `string?` | No | Extra BenchmarkDotNet CLI arguments (e.g. `--filter *Span*`). |
| `benchmarkCode` | `string?` | No | C# benchmark source code. If omitted, uses `dotnet/performance` benchmarks. |
| `useProfiler` | `bool` | No | Enable perf profiler recording. Default: `false`. |
| `useGcProfiler` | `bool` | No | Enable a separate OrchardCore dotnet-trace GC profiling pass. May be combined with `useProfiler`. Default: `false`. |
| `attempts` | `int` | No | Repeat count. For `"orchard"` it is the number of server processes per runtime. Default: `1`. |
| `requestedBy` | `string?` | No | GitHub login used for the rolling per-user job limit. Missing values share the `(anonymous)` limit bucket. |
| `sourceUrl` | `string?` | No | URL of the originating GitHub comment. |

### Response — `200 OK`

```json
{
  "groupId": "a1b2c3d4-...",
  "jobs": [
    { "id": "e5f6g7h8-...", "platform": "aws_graviton4" },
    { "id": "i9j0k1l2-...", "platform": "helix_windows_x64" }
  ]
}
```

### Errors

| Status | Condition |
|---|---|
| `400` | No platforms specified, unknown target, local target in production, a target the requested `kind` cannot run on, or `"orchard"` without commits. |
| `429` | The request would exceed the requester's rolling 24-hour job limit. One job is counted per target platform. |

The global limit is configured with `EgorBot:MaxJobsPerUser24Hours` and defaults
to 16. A `429` response includes the normalized user, current usage, effective
limit, requested job count, and the earliest retry time when one is available:

```json
{
  "code": "job_limit_reached",
  "error": "@jkotas has used 16 of 16 jobs in the rolling 24-hour window.",
  "user": "jkotas",
  "limit": 16,
  "used": 16,
  "requested": 1,
  "windowHours": 24,
  "retryAt": "2026-08-22T10:15:00Z"
}
```

---

## GET /api/jobs

List recent jobs (paginated).

### Query Parameters

| Param | Type | Default | Description |
|---|---|---|---|
| `page` | `int` | `1` | Page number (1-based). |
| `pageSize` | `int` | `20` | Results per page (1–100). |

### Response — `200 OK`

```json
{
  "jobs": [
    {
      "id": "e5f6g7h8-...",
      "groupId": "a1b2c3d4-...",
      "status": "Completed",
      "platform": "aws_graviton4",
      "commitsAndPrs": "PR_12345;main",
      "createdAt": "2026-02-17T10:00:00Z",
      "startedAt": "2026-02-17T10:00:05Z",
      "completedAt": "2026-02-17T10:45:00Z",
      "hasResult": true,
      "errorMessage": null
    }
  ],
  "total": 42,
  "page": 1,
  "pageSize": 20
}
```

---

## GET /api/jobs/{id}/status

Get the current status of a job.

### Response — `200 OK`

```json
{
  "id": "e5f6g7h8-...",
  "status": "Running",
  "platform": "aws_graviton4",
  "commitsAndPrs": "PR_12345;main",
  "createdAt": "2026-02-17T10:00:00Z",
  "startedAt": "2026-02-17T10:00:05Z",
  "completedAt": null,
  "errorMessage": null,
  "hasResult": false,
  "sourceUrl": "https://github.com/dotnet/runtime/issues/12345#issuecomment-..."
}
```

### Job Statuses

| Status | Description |
|---|---|
| `Pending` | Queued, not yet started. |
| `Provisioning` | Cloud infrastructure being provisioned. |
| `Running` | Agent is running benchmarks. |
| `Completed` | Finished successfully, results available. |
| `Failed` | Agent or infrastructure error. |
| `TimedOut` | Exceeded the job timeout (default: 60 min). |
| `Cancelled` | Cancelled by the system. |

### Errors

| Status | Condition |
|---|---|
| `404` | Job not found. |

---

## GET /api/jobs/{id}/result

Get the benchmark results as Markdown.

### Response — `200 OK` (Content-Type: `text/markdown`)

Returns the BDN results table in Markdown format when the job completed successfully.

If the job failed or results aren't ready yet, returns JSON:

```json
{ "error": "Results not yet available.", "status": "Running" }
```

### Errors

| Status | Condition |
|---|---|
| `404` | Job not found. |

---

## GET /api/jobs/{id}/logs

Get all log entries for a job.

### Query Parameters

| Param | Type | Default | Description |
|---|---|---|---|
| `tail` | `int?` | all | Return only the last N log entries. |

### Response — `200 OK`

```json
{
  "skipped": 150,
  "logs": [
    { "id": 151, "timestamp": "2026-02-17T10:05:00.000Z", "message": "[STAGE 1/6] Environment set up..." },
    { "id": 152, "timestamp": "2026-02-17T10:05:01.000Z", "message": "Installing dependencies..." }
  ]
}
```

`skipped` is `null` when all logs are returned (no `tail` parameter or `tail` >= total).

---

## GET /api/jobs/{id}/logs/stream

Server-Sent Events (SSE) endpoint for live log streaming.

### Response — `200 OK` (Content-Type: `text/event-stream`)

Each event is a JSON object:

```
data: {"id":1,"timestamp":"2026-02-17T10:05:00.000Z","message":"Starting..."}

data: {"id":2,"timestamp":"2026-02-17T10:05:01.000Z","message":"Building..."}
```

When the job finishes, a final event is sent and the stream closes:

```
data: {"done":true,"status":"Completed"}
```

---

## GET /health

Health check endpoint.

### Response — `200 OK`

```
healthy
```

---

## GET /jobs/{id}

Web UI page for viewing a job. Serves `job.html` which uses the API endpoints above to display live logs and results.
