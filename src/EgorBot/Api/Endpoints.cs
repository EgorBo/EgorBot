using EgorBot.Cloud;
using EgorBot.Data;
using EgorBot.GitHub;
using EgorBot.Services;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Api;

public static class Endpoints
{
    public static void MapBotApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ── Sub-job callbacks (called by remote machines) ──

        api.MapPost("/subjobs/{subJobId}/complete", HandleSubJobComplete)
            .DisableAntiforgery();

        api.MapPost("/subjobs/{subJobId}/logs", HandleSubJobLogs)
            .DisableAntiforgery();

        api.MapPost("/subjobs/{subJobId}/metrics", HandleSubJobMetrics)
            .DisableAntiforgery();

        // ── Web UI API ──

        api.MapGet("/jobs", HandleListJobs);
        api.MapGet("/jobs/{jobId}", HandleGetJob);
        api.MapGet("/subjobs/{subJobId}/logs", HandleGetLogs);
        api.MapGet("/subjobs/{subJobId}/metrics", HandleGetMetrics);

        // ── Legacy callback (compatible with existing script) ──

        app.MapPost("/StopJob", HandleLegacyStopJob).DisableAntiforgery();

        // ── Test endpoints (for local testing without GitHub) ──

        api.MapPost("/test/submit", HandleTestSubmit);
        api.MapPost("/test/fake-complete/{subJobId}", HandleTestFakeComplete);
    }

    /// <summary>
    /// Called by the remote machine when it finishes (or fails).
    /// Accepts multipart form data with artifact zip files.
    /// </summary>
    private static async Task<IResult> HandleSubJobComplete(
        string subJobId,
        HttpRequest request,
        JobOrchestrator orchestrator,
        ILogger<Program> logger,
        IConfiguration config)
    {
        var success = request.Query["success"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        var errorMessage = request.Query["error"].FirstOrDefault();

        string? artifactPath = null;

        // Save uploaded files
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var artifactDir = Path.Combine(config["Bot:ArtifactsPath"] ?? "artifacts", subJobId);
            Directory.CreateDirectory(artifactDir);

            foreach (var file in form.Files)
            {
                var filePath = Path.Combine(artifactDir, file.FileName);
                await using var stream = File.Create(filePath);
                await file.CopyToAsync(stream);
                logger.LogInformation("Saved artifact {FileName} for sub-job {SubJobId}", file.FileName, subJobId);
            }

            artifactPath = artifactDir;
        }

        await orchestrator.CompleteSubJobAsync(subJobId, success, artifactPath, errorMessage);

        return Results.Ok(new { status = "ok", subJobId });
    }

    /// <summary>
    /// Receives streaming log lines from the remote machine.
    /// </summary>
    private static async Task<IResult> HandleSubJobLogs(
        string subJobId,
        HttpRequest request,
        LogStore logStore)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            logStore.AppendLog(subJobId, line);
        }

        return Results.Ok();
    }

    /// <summary>
    /// Receives CPU/memory metrics from the remote machine.
    /// </summary>
    private static async Task<IResult> HandleSubJobMetrics(
        string subJobId,
        HttpRequest request,
        LogStore logStore)
    {
        var metrics = await request.ReadFromJsonAsync<MetricsPayload>();
        if (metrics is not null)
            logStore.AppendMetrics(subJobId, metrics.CpuPercent, metrics.MemoryMb);

        return Results.Ok();
    }

    private record MetricsPayload(double CpuPercent, double MemoryMb);

    /// <summary>
    /// Legacy endpoint compatible with the existing script's curl call.
    /// </summary>
    private static async Task<IResult> HandleLegacyStopJob(
        HttpRequest request,
        JobOrchestrator orchestrator,
        ILogger<Program> logger,
        IConfiguration config)
    {
        var jobId = request.Query["jobId"].FirstOrDefault() ?? "";
        var success = request.Query["success"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

        string? artifactPath = null;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var artifactDir = Path.Combine(config["Bot:ArtifactsPath"] ?? "artifacts", jobId);
            Directory.CreateDirectory(artifactDir);

            foreach (var file in form.Files)
            {
                var filePath = Path.Combine(artifactDir, file.FileName);
                await using var stream = File.Create(filePath);
                await file.CopyToAsync(stream);
            }

            artifactPath = artifactDir;
        }

        await orchestrator.CompleteSubJobAsync(jobId, success, artifactPath, success ? null : "Remote script reported failure");

        return Results.Ok(new { status = "ok" });
    }

    // ── Web UI API handlers ──

    private static async Task<IResult> HandleListJobs(BotDbContext db)
    {
        var jobs = await db.Jobs
            .Include(j => j.SubJobs)
            .OrderByDescending(j => j.CreatedAt)
            .Take(100)
            .Select(j => new
            {
                j.Id,
                j.Requester,
                j.Repository,
                j.PrNumber,
                j.Status,
                j.CreatedAt,
                j.CompletedAt,
                SubJobCount = j.SubJobs.Count,
                CompletedSubJobs = j.SubJobs.Count(s => s.Status == SubJobStatus.Completed || s.Status == SubJobStatus.Failed || s.Status == SubJobStatus.TimedOut),
            })
            .ToListAsync();

        return Results.Ok(jobs);
    }

    private static async Task<IResult> HandleGetJob(string jobId, BotDbContext db)
    {
        var job = await db.Jobs
            .Include(j => j.SubJobs)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job is null) return Results.NotFound();

        return Results.Ok(new
        {
            job.Id,
            job.Requester,
            job.Repository,
            job.PrNumber,
            job.Commits,
            job.BenchmarkSnippetUrl,
            job.EnablePerf,
            job.Status,
            job.CreatedAt,
            job.CompletedAt,
            job.ResultMarkdown,
            SubJobs = job.SubJobs.Select(s => new
            {
                s.Id,
                s.TargetOs,
                s.TargetArch,
                s.HardwareProfile,
                s.CloudProvider,
                s.Status,
                s.CreatedAt,
                s.CompletedAt,
                s.ErrorMessage,
                s.ResultArtifactPath,
            }),
        });
    }

    private static IResult HandleGetLogs(string subJobId, LogStore logStore, HttpRequest request)
    {
        var from = int.TryParse(request.Query["from"], out var f) ? f : 0;
        var logs = logStore.GetLogs(subJobId, from);
        return Results.Ok(new { logs, nextFrom = from + logs.Count });
    }

    private static IResult HandleGetMetrics(string subJobId, LogStore logStore, HttpRequest request)
    {
        var from = int.TryParse(request.Query["from"], out var f) ? f : 0;
        var metrics = logStore.GetMetrics(subJobId, from);
        return Results.Ok(new { metrics, nextFrom = from + metrics.Count });
    }

    // ── Test helpers ──

    private record TestSubmitRequest(
        string? Command,
        string? BenchmarkCode,
        string Requester = "test-user",
        string Repository = "dotnet/runtime",
        int? PrNumber = null);

    /// <summary>
    /// Simulate a bot command without GitHub.
    /// POST /api/test/submit with JSON body.
    /// Example: { "command": "-amd -perf", "benchmarkCode": "[Benchmark] public void Foo() {}" }
    /// </summary>
    private static async Task<IResult> HandleTestSubmit(
        TestSubmitRequest request,
        BotDbContext db,
        IEnumerable<ICloudProvider> cloudProviders,
        ScriptGenerator scriptGenerator,
        LogStore logStore,
        JobOrchestrator orchestrator,
        ILogger<Program> logger,
        IConfiguration config)
    {
        // Build a synthetic comment body so we can reuse the parser
        var commentBody = $"@EgorBot {request.Command ?? "-amd"}";
        if (!string.IsNullOrEmpty(request.BenchmarkCode))
            commentBody += $"\n```cs\n{request.BenchmarkCode}\n```";

        var command = GitHub.CommandParser.TryParse(
            commentBody, request.Requester, 0,
            request.PrNumber ?? 0, request.PrNumber.HasValue,
            "dotnet", "runtime");

        if (command is null)
            return Results.BadRequest(new { error = "Could not parse command" });

        // Check if any platform requests a real cloud provider (e.g. WSL)
        bool hasRealProvider = command.Platforms.Any(p => p.PreferredProvider is not null);

        if (hasRealProvider)
        {
            // Use the full orchestrator pipeline (provisions real VMs / WSL processes)
            await orchestrator.CreateAndDispatchJobAsync(command);
            var job2 = await db.Jobs.Include(j => j.SubJobs)
                .OrderByDescending(j => j.CreatedAt)
                .FirstAsync(j => j.Requester == command.Requester);

            logger.LogInformation("Test job {JobId} dispatched via orchestrator with {Count} sub-job(s)", job2.Id, job2.SubJobs.Count);

            return Results.Ok(new
            {
                jobId = job2.Id,
                dashboardUrl = $"/job.html?id={job2.Id}",
                subJobs = job2.SubJobs.Select(s => new { s.Id, s.TargetOs, s.TargetArch, s.HardwareProfile, s.CloudProvider }),
                hint = "Job dispatched to cloud provider. Watch logs at the dashboard URL.",
            });
        }

        // Fallback: create a passive test job (no cloud provisioning)
        var job = new Job
        {
            Requester = command.Requester,
            Repository = request.Repository,
            PrNumber = command.PrNumber,
            Commits = command.Commits.Count > 0 ? string.Join(",", command.Commits) : null,
            BenchmarkSnippetUrl = null,
            EnablePerf = command.EnablePerf,
            RawCommand = commentBody[..Math.Min(commentBody.Length, 200)],
            Status = JobStatus.Running,
        };

        foreach (var platform in command.Platforms)
        {
            var providerName = config["Bot:DefaultCloudProvider"] ?? "Local";
            job.SubJobs.Add(new SubJob
            {
                JobId = job.Id,
                TargetOs = platform.Os,
                TargetArch = platform.Arch,
                HardwareProfile = platform.HardwareProfile,
                CloudProvider = providerName,
                Status = SubJobStatus.Running,
            });
        }

        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        foreach (var sub in job.SubJobs)
        {
            logStore.AppendLog(sub.Id, $"[test] Sub-job created: {sub.TargetOs}/{sub.TargetArch} ({sub.HardwareProfile})");
            logStore.AppendLog(sub.Id, $"[test] Cloud provider: {sub.CloudProvider}");
            logStore.AppendLog(sub.Id, "[test] Waiting for completion. POST /api/test/fake-complete/{subJobId} to finish.");
        }

        logger.LogInformation("Test job {JobId} created with {Count} sub-job(s)", job.Id, job.SubJobs.Count);

        return Results.Ok(new
        {
            jobId = job.Id,
            dashboardUrl = $"/job.html?id={job.Id}",
            subJobs = job.SubJobs.Select(s => new { s.Id, s.TargetOs, s.TargetArch, s.HardwareProfile }),
            hint = "POST /api/subjobs/{subJobId}/logs to push log lines, POST /api/test/fake-complete/{subJobId} to complete",
        });
    }

    /// <summary>
    /// Quickly mark a sub-job as complete (for testing).
    /// POST /api/test/fake-complete/{subJobId}?success=true
    /// </summary>
    private static async Task<IResult> HandleTestFakeComplete(
        string subJobId,
        HttpRequest request,
        JobOrchestrator orchestrator,
        LogStore logStore)
    {
        var success = !request.Query["success"].FirstOrDefault()?.Equals("false", StringComparison.OrdinalIgnoreCase) ?? true;

        logStore.AppendLog(subJobId, $"[test] Fake completion triggered (success={success})");

        await orchestrator.CompleteSubJobAsync(subJobId, success, null, success ? null : "Simulated failure");

        return Results.Ok(new { status = "ok", subJobId, success });
    }
}
