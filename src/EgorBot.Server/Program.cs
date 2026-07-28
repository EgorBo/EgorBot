using System.Text.Json;
using EgorBot.Server.Data;
using EgorBot.Server.Models;
using EgorBot.Server.Services;
using EgorBot.Shared;
using EgorBot.Server.Services.CloudInit;
using EgorBot.Server.Services.CloudProviders;
using EgorBot.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Load optional appsettings.Local.json (gitignored) for real credentials
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ── Database ─────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=egorbot.db"));

// ── Cloud providers ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<ICloudProvider, AzureCloudProvider>();
builder.Services.AddSingleton<ICloudProvider, AwsCloudProvider>();
builder.Services.AddSingleton<ICloudProvider, HelixCloudProvider>();
builder.Services.AddSingleton<ICloudProvider, DockerCloudProvider>();
builder.Services.AddSingleton<CloudProviderFactory>();

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CloudInitBuilder>();
builder.Services.AddSingleton<RuntimeSettings>();
builder.Services.AddSingleton<CorePoolManager>();
builder.Services.AddSingleton<ResultProcessor>();
builder.Services.AddSingleton<LogUploadService>();
builder.Services.AddCors();
builder.Services.AddSingleton<INotificationService, ConsoleNotificationService>();
builder.Services.AddSingleton<INotificationService, TelegramNotificationService>();
builder.Services.AddHostedService<TelegramCommandService>();
builder.Services.AddSingleton<JobOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobOrchestrator>());

var app = builder.Build();

// ── Log resolved config for diagnostics ──────────────────────────────────────
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var serviceBaseUrl = app.Configuration["EgorBot:ServiceBaseUrl"];
    startupLogger.LogInformation("Config: EgorBot:ServiceBaseUrl = {ServiceBaseUrl}", serviceBaseUrl);
    startupLogger.LogInformation("Config: Environment = {Env}", app.Environment.EnvironmentName);
}

// ── Auto-create database ─────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var dbLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database");
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated() never alters an existing database, so columns added to the model
    // after the file was created have to be patched in by hand — otherwise every query
    // fails with "SQLite Error 1: 'no such column'".
    (string Table, string Column, string Type)[] addedColumns =
    [
        ("Jobs", "PerfStatEvents", "TEXT NULL"),
    ];

    var connection = db.Database.GetDbConnection();
    await connection.OpenAsync();
    foreach (var (table, column, type) in addedColumns)
    {
        var exists = false;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await check.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) continue;

        // Values are compile-time constants from the list above, not user input.
        var alterSql = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + type + ";";
        await db.Database.ExecuteSqlRawAsync(alterSql);
        dbLogger.LogWarning("Added missing column {Table}.{Column} to the existing database", table, column);
    }
}

// ── Request logging middleware ───────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    // Skip logging for high-frequency internal endpoints (heartbeat, logs, status)
    var path = ctx.Request.Path.Value ?? "";
    var isQuiet = path.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/logs", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/status", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);

    if (isQuiet)
    {
        await next();
        return;
    }

    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HTTP");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    logger.LogInformation("→ {Method} {Path}{Query}",
        ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString);
    try
    {
        await next();
    }
    finally
    {
        sw.Stop();
        logger.LogInformation("← {Method} {Path} → {StatusCode} ({ElapsedMs}ms)",
            ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
    }
});

// ── Static files for web UI ──────────────────────────────────────────────────
app.UseCors();  // enable CORS (configured below per-endpoint)
app.UseDefaultFiles();
app.UseStaticFiles();

// ═════════════════════════════════════════════════════════════════════════════
//  Public API endpoints
// ═════════════════════════════════════════════════════════════════════════════

var api = app.MapGroup("/api");

// POST /api/jobs — Start a new benchmark job
api.MapPost("/jobs", async (StartJobRequest request, AppDbContext db, JobOrchestrator orchestrator, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("StartJob");
    log.LogInformation("POST /api/jobs called. Platforms=[{Platforms}], CommitsAndPrs={Commits}, HasCode={HasCode}",
        string.Join(",", request.Platforms ?? []),
        request.CommitsAndPrs,
        request.BenchmarkCode is not null);

    // Validate
    if (request.Platforms is not { Count: > 0 })
    {
        log.LogWarning("Validation failed: no platforms");
        return Results.BadRequest(new { error = "At least one platform/target is required." });
    }

    // Normalize & validate targets (resolve aliases, OS prefix)
    var normalizedPlatforms = new List<string>();
    foreach (var raw in request.Platforms)
    {
        if (!TargetCatalog.TryResolve(raw, out _))
        {
            log.LogWarning("Validation failed: unknown target '{Target}'", raw);
            return Results.BadRequest(new
            {
                error = $"Unknown target: '{raw}'. Valid targets: {string.Join(", ", TargetCatalog.GetAllTargetNames())}."
            });
        }

        var normalized = TargetCatalog.Resolve(raw);
        normalizedPlatforms.Add(normalized);
    }

    // CommitsAndPrs can be empty — the agent will run benchmarks with the default SDK runtime
    var commitsAndPrs = request.CommitsAndPrs ?? "";

    // These end up interpolated into the VM bootstrap command line
    // (CloudInitBuilder → --gh_commits_and_prs "..."), so anything that could break
    // out of the quoting must be rejected here rather than executed on the VM.
    foreach (var commitRef in commitsAndPrs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!SafeCommitRef().IsMatch(commitRef))
        {
            log.LogWarning("Validation failed: unsafe commit ref '{Ref}'", commitRef);
            return Results.BadRequest(new
            {
                error = $"Invalid commit/PR reference: '{commitRef}'. Allowed characters: letters, digits, '_', '-', '.', '/', '~', '^'."
            });
        }
    }

    var groupId = Guid.NewGuid();
    var jobs = new List<object>();
    log.LogInformation("Creating job group {GroupId}", groupId);

    // Custom perf events end up in a `perf stat -e ...` command line on the VM.
    var perfStatEvents = string.IsNullOrWhiteSpace(request.PerfStatEvents) ? null : request.PerfStatEvents.Trim();
    if (perfStatEvents is not null && !SafePerfEvents().IsMatch(perfStatEvents))
    {
        log.LogWarning("Validation failed: unsafe perf events '{Events}'", perfStatEvents);
        return Results.BadRequest(new
        {
            error = "Invalid perf event list. Use comma-separated event names, e.g. " +
                    "'l1d_cache,l1d_cache_refill,cycles'. Allowed characters: letters, digits, '_', '-', '.', '/', ':', '=', ','."
        });
    }

    foreach (var platform in normalizedPlatforms)
    {
        // On Linux, UseProfiler triggers perf record via the platform agent module.
        // On non-Linux, we use BDN's built-in EventPipeProfiler instead (--profiler EP).
        // Asking for perf events only makes sense together with the profiler.
        var useProfiler = request.UseProfiler || perfStatEvents is not null;
        var bdnArgs = request.BdnArguments;
        if (useProfiler)
        {
            var osFamily = TargetCatalog.GetTarget(platform).OsFamily;
            if (!osFamily.Equals("linux", StringComparison.OrdinalIgnoreCase))
            {
                useProfiler = false;
                bdnArgs = string.IsNullOrWhiteSpace(bdnArgs)
                    ? "--profiler EP"
                    : bdnArgs + " --profiler EP";
            }
        }

        // Ensure --filter is always present so BDN doesn't prompt interactively
        if (bdnArgs is null || !bdnArgs.Contains("--filter", StringComparison.OrdinalIgnoreCase))
        {
            bdnArgs = string.IsNullOrWhiteSpace(bdnArgs)
                ? "--filter \"*\""
                : bdnArgs + " --filter \"*\"";
        }

        var job = new BenchmarkJob
        {
            GroupId = groupId,
            Platform = platform,
            CommitsAndPrs = commitsAndPrs,
            BdnArguments = bdnArgs,
            BenchmarkCode = request.BenchmarkCode,
            UseProfiler = useProfiler,
            PerfStatEvents = useProfiler ? perfStatEvents : null,
            Attempts = request.Attempts,
            RequestedBy = request.RequestedBy,
            SourceUrl = request.SourceUrl,
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        log.LogInformation("Job {JobId} saved to DB for platform {Platform}", job.Id, platform);

        orchestrator.Enqueue(job.Id);
        log.LogInformation("Job {JobId} enqueued to orchestrator", job.Id);
        jobs.Add(new { id = job.Id, platform });
    }

    log.LogInformation("Returning {Count} jobs for group {GroupId}", jobs.Count, groupId);
    return Results.Ok(new { groupId, jobs });
});

// PATCH /api/jobs/group/{groupId}/tracking-issue — set tracking issue URL for all jobs in a group
api.MapPatch("/jobs/group/{groupId:guid}/tracking-issue", async (Guid groupId, HttpContext ctx, AppDbContext db) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var url = (await reader.ReadToEndAsync()).Trim().Trim('"');
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "URL is required." });

    var jobs = await db.Jobs.Where(j => j.GroupId == groupId).ToListAsync();
    if (jobs.Count == 0)
        return Results.NotFound(new { error = "No jobs found for this group." });

    foreach (var job in jobs)
        job.TrackingIssueUrl = url;

    await db.SaveChangesAsync();
    return Results.Ok();
});

// GET /api/jobs — List recent jobs
api.MapGet("/jobs", async (AppDbContext db, int? page, int? pageSize) =>
{
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var skip = ((page ?? 1) - 1) * size;

    var jobs = await db.Jobs
        .OrderByDescending(j => j.CreatedAt)
        .Skip(skip).Take(size)
        .Select(job => new
        {
            job.Id,
            job.GroupId,
            Status = job.Status.ToString(),
            job.Platform,
            job.CommitsAndPrs,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            HasResult = job.ResultMarkdown != null,
            job.ErrorMessage,
        })
        .ToListAsync();

    var total = await db.Jobs.CountAsync();
    return Results.Ok(new { jobs, total, page = page ?? 1, pageSize = size });
});

// GET /api/jobs/{id}/status
api.MapGet("/jobs/{id:guid}/status", async (Guid id, AppDbContext db) =>
{
    var job = await db.Jobs.FindAsync(id);
    if (job is null)
        return Results.NotFound(new { error = "Job not found." });

    return Results.Ok(new
    {
        job.Id,
        Status = job.Status.ToString(),
        job.Platform,
        job.CommitsAndPrs,
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
        job.ErrorMessage,
        HasResult = job.ResultMarkdown != null,
        job.SourceUrl,
        job.LogsBlobUrl,
    });
});

// GET /api/jobs/{id}/result — returns the MD result
api.MapGet("/jobs/{id:guid}/result", async (Guid id, AppDbContext db) =>
{
    var job = await db.Jobs.FindAsync(id);
    if (job is null)
        return Results.NotFound(new { error = "Job not found." });

    if (job.Status == JobStatus.Failed || job.Status == JobStatus.TimedOut)
        return Results.Ok(new { error = job.ErrorMessage ?? "Job failed." });

    if (job.ResultMarkdown is null)
        return Results.Ok(new { error = "Results not yet available.", status = job.Status.ToString() });

    return Results.Text(job.ResultMarkdown, "text/markdown");
});

// GET /api/jobs/{id}/logs/full — serve full job logs as plain text
api.MapGet("/jobs/{id:guid}/logs/full", async (Guid id, AppDbContext db) =>
{
    var logs = await db.JobLogs
        .Where(l => l.JobId == id)
        .OrderBy(l => l.Id)
        .Select(l => new { l.Timestamp, l.Message })
        .ToListAsync();

    if (logs.Count == 0)
        return Results.NotFound(new { error = "No logs found for this job." });

    var sb = new System.Text.StringBuilder(logs.Count * 120);
    foreach (var log in logs)
    {
        sb.Append(log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append("  ");
        sb.AppendLine(log.Message);
    }

    return Results.Text(sb.ToString(), "text/plain");
});

// GET /api/jobs/{id}/artifacts/{**path} — serve locally-stored perf artifacts
api.MapGet("/jobs/{id:guid}/artifacts/{**path}", async (Guid id, string path) =>
{
    if (string.IsNullOrEmpty(path))
        return Results.BadRequest(new { error = "Artifact path required." });

    // Prevent directory traversal (compare including the separator, so a sibling
    // directory sharing the id as a prefix can't be reached either)
    var artifactsBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "artifacts", id.ToString()))
                        + Path.DirectorySeparatorChar;
    var fullPath = Path.GetFullPath(Path.Combine(artifactsBase, path.Replace('/', Path.DirectorySeparatorChar)));

    if (!fullPath.StartsWith(artifactsBase, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Invalid artifact path." });

    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "Artifact not found." });

    var contentType = path switch
    {
        _ when path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) => "image/svg+xml",
        _ when path.EndsWith(".speedscope", StringComparison.OrdinalIgnoreCase) => "application/json",
        _ => "text/plain; charset=utf-8",
    };

    var bytes = await File.ReadAllBytesAsync(fullPath);
    return Results.File(bytes, contentType);
}).RequireCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// GET /api/jobs/{id}/logs — all log entries
api.MapGet("/jobs/{id:guid}/logs", async (Guid id, int? tail, AppDbContext db) =>
{
    IQueryable<JobLogEntry> query = db.JobLogs.Where(l => l.JobId == id);

    List<JobLogEntry> rawLogs;
    int? skipped = null;

    if (tail.HasValue && tail.Value > 0)
    {
        var totalCount = await query.CountAsync();
        if (totalCount > tail.Value)
            skipped = totalCount - tail.Value;

        rawLogs = await query
            .OrderByDescending(l => l.Id)
            .Take(tail.Value)
            .ToListAsync();
        rawLogs.Reverse(); // restore chronological order
    }
    else
    {
        rawLogs = await query.OrderBy(l => l.Id).ToListAsync();
    }

    var logs = rawLogs
        .Select(l => new { l.Id, timestamp = l.Timestamp.ToString("o"), l.Message })
        .ToList();

    return Results.Ok(new { skipped, logs });
});

// GET /api/jobs/{id}/logs/stream — SSE endpoint for live log streaming
api.MapGet("/jobs/{id:guid}/logs/stream", async (Guid id, long? after, AppDbContext db, HttpContext ctx, CancellationToken ct) =>
{
    // A missing job would otherwise fall back to default(JobStatus) == Pending and keep
    // this loop (and its DB connection) alive forever.
    if (!await db.Jobs.AnyAsync(j => j.Id == id, ct))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    long lastLogId = after ?? 0;
    var streamDeadline = DateTime.UtcNow.AddHours(4);

    while (!ct.IsCancellationRequested && DateTime.UtcNow < streamDeadline)
    {
        var newLogs = await db.JobLogs
            .Where(l => l.JobId == id && l.Id > lastLogId)
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Timestamp, l.Message })
            .ToListAsync(ct);

        foreach (var log in newLogs)
        {
            var json = JsonSerializer.Serialize(new { log.Id, timestamp = log.Timestamp.ToString("o"), log.Message },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            lastLogId = log.Id;
        }

        if (newLogs.Count > 0)
            await ctx.Response.Body.FlushAsync(ct);

        // Check if job is still running
        var status = await db.Jobs.Where(j => j.Id == id)
            .Select(j => j.Status)
            .FirstOrDefaultAsync(ct);

        if (status is JobStatus.Completed or JobStatus.Failed or JobStatus.TimedOut or JobStatus.Cancelled)
        {
            // Send final batch and close
            await ctx.Response.WriteAsync($"data: {{\"done\":true,\"status\":\"{status}\"}}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            break;
        }

        await Task.Delay(2000, ct);
    }
});

// ═════════════════════════════════════════════════════════════════════════════
//  Internal API endpoints (called by the agent on VMs)
// ═════════════════════════════════════════════════════════════════════════════

var internalApi = app.MapGroup("/api/internal");

// POST /api/internal/jobs/{id}/logs — agent posts log lines
internalApi.MapPost("/jobs/{id:guid}/logs", async (Guid id, HttpContext ctx, AppDbContext db, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("AgentLogs");

    // Skip log persistence for validation-only (throwaway) jobs that aren't in the DB
    var jobExists = await db.Jobs.AnyAsync(j => j.Id == id);
    if (!jobExists)
    {
        log.LogDebug("[Job {JobId}] Job not found in DB (likely validation); discarding {Method} logs", id, ctx.Request.Method);
        return Results.Ok();
    }

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    var lines = JsonSerializer.Deserialize<List<string>>(body) ?? [];
    var now = DateTime.UtcNow;
    log.LogDebug("[Job {JobId}] Received {Count} log lines from agent", id, lines.Count);

    foreach (var line in lines)
    {
        db.JobLogs.Add(new JobLogEntry
        {
            JobId = id,
            Timestamp = now,
            Message = line,
        });
    }

    await db.SaveChangesAsync();
    return Results.Ok();
});

// POST /api/internal/jobs/{id}/heartbeat
internalApi.MapPost("/jobs/{id:guid}/heartbeat", (Guid id, JobOrchestrator orchestrator, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("Heartbeat");
    log.LogDebug("[Job {JobId}] Heartbeat received", id);
    orchestrator.RecordHeartbeat(id);
    return Results.Ok();
});

// POST /api/internal/jobs/{id}/complete — agent posts final results
internalApi.MapPost("/jobs/{id:guid}/complete", async (Guid id, HttpContext ctx,
    AppDbContext db, JobOrchestrator orchestrator, ResultProcessor resultProcessor,
    LogUploadService logUploadService, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("JobComplete");
    log.LogInformation("[Job {JobId}] Complete endpoint called", id);

    var form = await ctx.Request.ReadFormAsync();

    var successStr = form["success"].FirstOrDefault() ?? "false";
    var success = successStr.Equals("true", StringComparison.OrdinalIgnoreCase);
    log.LogInformation("[Job {JobId}] Success={Success}, FormFiles={FileCount}", id, success, form.Files.Count);

    string? markdown = null;
    string? error = null;

    try
    {
        if (success)
        {
            // Look for artifacts zip
            var artifactsFile = form.Files.GetFile("artifacts");
            if (artifactsFile is not null)
            {
                log.LogInformation("[Job {JobId}] Processing artifacts zip ({Size} bytes)", id, artifactsFile.Length);
                var job = await db.Jobs.FindAsync(id);

                // Buffer the zip to a MemoryStream so we can read it multiple times
                using var ms = new MemoryStream();
                await artifactsFile.OpenReadStream().CopyToAsync(ms);

                // Extract BDN markdown report
                ms.Position = 0;
                markdown = resultProcessor.ProcessArtifactsZip(ms, job?.CommitsAndPrs ?? "", id);
                log.LogInformation("[Job {JobId}] Result markdown length={Len}", id, markdown?.Length ?? 0);

                // Extract and save profiling artifacts locally (if profiling was enabled)
                if (job?.UseProfiler == true)
                {
                    ms.Position = 0;
                    var perfLinks = await logUploadService.UploadPerfArtifactsAsync(ms, id);
                    if (perfLinks is not null)
                    {
                        markdown += perfLinks;
                        log.LogInformation("[Job {JobId}] Appended perf artifact links to markdown", id);
                    }
                    else
                    {
                        log.LogWarning("[Job {JobId}] Profiling was enabled but no perf artifacts found in zip", id);
                    }
                }
            }
            else
            {
                markdown = "_No artifacts uploaded._";
                log.LogWarning("[Job {JobId}] No artifacts file in the upload", id);
            }
        }
        else
        {
            error = form["error"].FirstOrDefault() ?? "Agent reported failure.";
            log.LogWarning("[Job {JobId}] Agent reported failure: {Error}", id, error);
        }
    }
    catch (Exception ex)
    {
        // Never leave the orchestrator waiting: without a CompleteJob signal the job
        // would sit "Running" until the (multi-hour) timeout even though it finished.
        log.LogError(ex, "[Job {JobId}] Failed to process the completion payload", id);
        success = false;
        error = $"Failed to process benchmark artifacts: {ex.Message}";
    }

    orchestrator.CompleteJob(id, new JobOutcome(success, markdown, error));
    log.LogInformation("[Job {JobId}] CompleteJob signaled to orchestrator", id);
    return Results.Ok();
});

// ═════════════════════════════════════════════════════════════════════════════
//  Web UI fallback: serve job.html for /jobs/{id} routes
// ═════════════════════════════════════════════════════════════════════════════

app.MapGet("/jobs/{id:guid}", async (Guid id, HttpContext ctx) =>
{
    var jobHtmlPath = Path.Combine(app.Environment.WebRootPath, "job.html");
    if (File.Exists(jobHtmlPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(jobHtmlPath);
    }
    else
    {
        ctx.Response.StatusCode = 404;
    }
});

// Health check
app.MapGet("/health", () => Results.Ok("healthy"));

// ═════════════════════════════════════════════════════════════════════════════
//  Scratch log — generic text log for ad-hoc debugging (e.g. Helix machines)
// ═════════════════════════════════════════════════════════════════════════════

var _scratchLogs = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<(DateTime Time, string Text)>>();

// POST /api/scratch/{name} — append text (request body) to a named log
app.MapPost("/api/scratch/{name}", async (string name, HttpContext ctx) =>
{
    // Bounded on every axis — this endpoint is reachable from the internet and used
    // to be an unbounded in-memory sink (one POST loop could OOM the server).
    const int MaxLogs = 32, MaxEntriesPerLog = 500, MaxTextLength = 64 * 1024;

    using var reader = new StreamReader(ctx.Request.Body);
    var buffer = new char[MaxTextLength];
    var read = await reader.ReadBlockAsync(buffer, 0, MaxTextLength);
    var text = new string(buffer, 0, read);

    if (!_scratchLogs.ContainsKey(name) && _scratchLogs.Count >= MaxLogs)
        return Results.BadRequest(new { error = "Too many scratch logs." });

    var queue = _scratchLogs.GetOrAdd(name, _ => new());
    queue.Enqueue((DateTime.UtcNow, text));
    while (queue.Count > MaxEntriesPerLog)
        queue.TryDequeue(out _);

    return Results.Ok();
});

// GET /api/scratch/{name} — view full raw log as plain text
app.MapGet("/api/scratch/{name}", (string name) =>
{
    if (!_scratchLogs.TryGetValue(name, out var queue) || queue.IsEmpty)
        return Results.Text("(empty)\n", "text/plain");

    var sb = new System.Text.StringBuilder();
    foreach (var (time, text) in queue)
    {
        sb.Append(time.ToString("HH:mm:ss.fff"));
        sb.Append("  ");
        sb.AppendLine(text);
    }
    return Results.Text(sb.ToString(), "text/plain");
});

// DELETE /api/scratch/{name} — clear a named log
app.MapDelete("/api/scratch/{name}", (string name) =>
{
    _scratchLogs.TryRemove(name, out _);
    return Results.Ok();
});

app.Run();

/// <summary>Partial class to enable WebApplicationFactory&lt;Program&gt; in tests.</summary>
public partial class Program
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_./~^-]+$")]
    private static partial System.Text.RegularExpressions.Regex SafeCommitRef();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_.:=/,-]+$")]
    private static partial System.Text.RegularExpressions.Regex SafePerfEvents();
}
