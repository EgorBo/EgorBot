using EgorBot.Cloud;
using EgorBot.Data;
using EgorBot.GitHub;
using EgorBot.Services.GitHub;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Services;

/// <summary>
/// Orchestrates the lifecycle of benchmark jobs:
/// create → provision VMs → wait for results → post to GitHub → deallocate.
/// </summary>
public class JobOrchestrator
{
    private readonly BotDbContext _db;
    private readonly GitHubService _github;
    private readonly IEnumerable<ICloudProvider> _cloudProviders;
    private readonly ScriptGenerator _scriptGenerator;
    private readonly LogStore _logStore;
    private readonly ILogger<JobOrchestrator> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public JobOrchestrator(
        BotDbContext db,
        GitHubService github,
        IEnumerable<ICloudProvider> cloudProviders,
        ScriptGenerator scriptGenerator,
        LogStore logStore,
        ILogger<JobOrchestrator> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _github = github;
        _cloudProviders = cloudProviders;
        _scriptGenerator = scriptGenerator;
        _logStore = logStore;
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Creates a Job and SubJobs from a parsed BotCommand and dispatches them to cloud providers.
    /// </summary>
    public async Task CreateAndDispatchJobAsync(BotCommand command, CancellationToken ct = default)
    {
        // Upload benchmark code to a gist (skip if no GitHub token configured)
        string? snippetUrl = null;
        if (!string.IsNullOrEmpty(command.BenchmarkCode))
        {
            try
            {
                var jobId = Guid.NewGuid().ToString("N");
                snippetUrl = await _github.CreateBenchmarkGistAsync(command.BenchmarkCode, jobId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create gist (no GitHub token?), continuing without snippet URL");
            }
        }

        var job = new Job
        {
            Requester = command.Requester,
            Repository = $"{command.Owner}/{command.Repository}",
            PrNumber = command.PrNumber,
            Commits = command.Commits.Count > 0 ? string.Join(",", command.Commits) : null,
            BenchmarkSnippetUrl = snippetUrl,
            EnablePerf = command.EnablePerf,
            RawCommand = command.BenchmarkCode?[..Math.Min(command.BenchmarkCode.Length, 200)],
            Status = JobStatus.Running,
            GitHubCommentId = command.CommentId,
            GitHubIssueOrPrNumber = command.IssueOrPrNumber,
        };

        // Create sub-jobs for each platform
        foreach (var platform in command.Platforms)
        {
            var provider = SelectProvider(platform);
            job.SubJobs.Add(new SubJob
            {
                JobId = job.Id,
                TargetOs = platform.Os,
                TargetArch = platform.Arch,
                HardwareProfile = platform.HardwareProfile,
                CloudProvider = provider.Name,
                Status = SubJobStatus.Provisioning,
            });
        }

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created job {JobId} with {SubJobCount} sub-job(s)", job.Id, job.SubJobs.Count);

        // Dispatch each sub-job
        foreach (var subJob in job.SubJobs)
        {
            _ = Task.Run(() => DispatchSubJobAsync(job, subJob, command), ct);
        }
    }

    private async Task DispatchSubJobAsync(Job job, SubJob subJob, BotCommand command)
    {
        try
        {
            var hostAddress = _config["Bot:PublicAddress"] ?? "localhost:5000";
            var script = _scriptGenerator.Generate(new ScriptParameters
            {
                HostAddress = hostAddress,
                SubJobId = subJob.Id,
                PrNumber = job.PrNumber,
                Commits = job.Commits?.Split(',').ToList() ?? [],
                BenchmarkSnippetUrl = job.BenchmarkSnippetUrl ?? "",
                EnablePerf = job.EnablePerf,
                PerfEvent = command.PerfEvent,
                BdnArgs = command.BdnArgs,
            });

            var spec = new CloudMachineSpec(subJob.TargetOs, subJob.TargetArch, subJob.HardwareProfile);
            var provider = _cloudProviders.First(p => p.Name == subJob.CloudProvider);

            var instanceId = await provider.ProvisionAsync(subJob.Id, spec, script);

            // Update sub-job with cloud instance info
            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var dbSubJob = await db.SubJobs.FindAsync(subJob.Id);
            if (dbSubJob is not null)
            {
                dbSubJob.CloudInstanceId = instanceId;
                dbSubJob.Status = SubJobStatus.Running;
                await db.SaveChangesAsync();
            }

            _logger.LogInformation("Sub-job {SubJobId} provisioned on {Provider} (instance: {InstanceId})",
                subJob.Id, provider.Name, instanceId);
        }
        catch (NotImplementedException)
        {
            _logger.LogWarning("Cloud provider {Provider} not yet implemented for sub-job {SubJobId}",
                subJob.CloudProvider, subJob.Id);

            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var dbSubJob = await db.SubJobs.FindAsync(subJob.Id);
            if (dbSubJob is not null)
            {
                dbSubJob.Status = SubJobStatus.Failed;
                dbSubJob.ErrorMessage = $"Cloud provider '{subJob.CloudProvider}' not yet implemented";
                dbSubJob.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            await TryCompleteJobAsync(subJob.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch sub-job {SubJobId}", subJob.Id);

            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var dbSubJob = await db.SubJobs.FindAsync(subJob.Id);
            if (dbSubJob is not null)
            {
                dbSubJob.Status = SubJobStatus.Failed;
                dbSubJob.ErrorMessage = ex.Message;
                dbSubJob.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            await TryCompleteJobAsync(subJob.JobId);
        }
    }

    /// <summary>
    /// Called when a sub-job reports completion (success or failure).
    /// </summary>
    public async Task CompleteSubJobAsync(string subJobId, bool success, string? artifactPath, string? errorMessage)
    {
        var subJob = await _db.SubJobs.FindAsync(subJobId);
        if (subJob is null)
        {
            _logger.LogWarning("Sub-job {SubJobId} not found", subJobId);
            return;
        }

        subJob.Status = success ? SubJobStatus.Completed : SubJobStatus.Failed;
        subJob.CompletedAt = DateTime.UtcNow;
        subJob.ResultArtifactPath = artifactPath;
        subJob.ErrorMessage = errorMessage;

        await _db.SaveChangesAsync();

        // Try to deallocate the cloud instance
        if (subJob.CloudInstanceId is not null)
        {
            try
            {
                var provider = _cloudProviders.FirstOrDefault(p => p.Name == subJob.CloudProvider);
                if (provider is not null)
                    await provider.DeallocateAsync(subJob.CloudInstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deallocate instance {InstanceId}", subJob.CloudInstanceId);
            }
        }

        await TryCompleteJobAsync(subJob.JobId);
    }

    /// <summary>
    /// Checks if all sub-jobs of a job are done and, if so, marks the job complete and posts results.
    /// </summary>
    private async Task TryCompleteJobAsync(string jobId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var job = await db.Jobs.Include(j => j.SubJobs).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null) return;

        var allDone = job.SubJobs.All(s =>
            s.Status is SubJobStatus.Completed or SubJobStatus.Failed or SubJobStatus.TimedOut);

        if (!allDone) return;

        var anySuccess = job.SubJobs.Any(s => s.Status == SubJobStatus.Completed);
        job.Status = anySuccess ? JobStatus.Completed : JobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;

        // Build result markdown
        job.ResultMarkdown = BuildResultMarkdown(job);
        await db.SaveChangesAsync();

        // Post result to GitHub
        if (job.GitHubIssueOrPrNumber.HasValue)
        {
            var parts = job.Repository.Split('/');
            var github = scope.ServiceProvider.GetRequiredService<GitHubService>();
            await github.PostCommentAsync(parts[0], parts[1], job.GitHubIssueOrPrNumber.Value, job.ResultMarkdown);
        }

        _logger.LogInformation("Job {JobId} completed with status {Status}", jobId, job.Status);

        // Keep logs around for a while so the web UI can still show them.
        // Schedule cleanup after 10 minutes.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(10));
            foreach (var subJob in job.SubJobs)
                _logStore.Cleanup(subJob.Id);
        });
    }

    private static string BuildResultMarkdown(Job job)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## EgorBot Benchmark Results");
        sb.AppendLine();
        sb.AppendLine($"**Job:** `{job.Id}`  ");
        sb.AppendLine($"**Requested by:** @{job.Requester}  ");
        sb.AppendLine($"**Duration:** {(job.CompletedAt - job.CreatedAt)?.TotalMinutes:F1} minutes  ");
        sb.AppendLine();

        foreach (var sub in job.SubJobs)
        {
            var icon = sub.Status == SubJobStatus.Completed ? "✅" : "❌";
            sb.AppendLine($"### {icon} {sub.TargetOs} / {sub.TargetArch} ({sub.HardwareProfile})");
            sb.AppendLine($"- **Status:** {sub.Status}");
            sb.AppendLine($"- **Provider:** {sub.CloudProvider}");

            if (sub.ErrorMessage is not null)
                sb.AppendLine($"- **Error:** {sub.ErrorMessage}");

            if (sub.ResultArtifactPath is not null)
                sb.AppendLine($"- **Artifacts:** [Download]({sub.ResultArtifactPath})");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private ICloudProvider SelectProvider(CloudMachineSpec spec)
    {
        // If the spec explicitly requests a provider (e.g. "WSL"), use it
        if (spec.PreferredProvider is not null)
        {
            var explicit_ = _cloudProviders.FirstOrDefault(p =>
                p.Name.Equals(spec.PreferredProvider, StringComparison.OrdinalIgnoreCase));
            if (explicit_ is not null)
                return explicit_;
        }

        var preferred = _config["Bot:DefaultCloudProvider"] ?? "Local";
        var provider = _cloudProviders.FirstOrDefault(p =>
            p.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase) && p.SupportsSpec(spec));

        return provider
            ?? _cloudProviders.FirstOrDefault(p => p.SupportsSpec(spec))
            ?? throw new InvalidOperationException($"No cloud provider supports {spec}");
    }

    private IServiceScope CreateScope() => _scopeFactory.CreateScope();
}
