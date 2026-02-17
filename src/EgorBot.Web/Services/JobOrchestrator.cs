using System.Collections.Concurrent;
using System.Threading.Channels;
using EgorBot.Web.Data;
using EgorBot.Web.Models;
using EgorBot.Web.Services.CloudInit;
using EgorBot.Web.Services.CloudProviders;
using EgorBot.Web.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Web.Services;

/// <summary>
/// Outcome posted by the agent (or timeout logic) to signal job completion.
/// </summary>
public sealed record JobOutcome(bool Success, string? ResultMarkdown = null, string? Error = null);

/// <summary>
/// Background service that manages the lifecycle of benchmark jobs:
/// dequeues pending jobs, provisions VMs, waits for agent completion, cleans up.
/// </summary>
public sealed class JobOrchestrator : BackgroundService
{
    private readonly Channel<Guid> _jobQueue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JobOutcome>> _completions = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _heartbeats = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CloudProviderFactory _providerFactory;
    private readonly CloudInitBuilder _cloudInitBuilder;
    private readonly IEnumerable<INotificationService> _notifiers;
    private readonly ILogger<JobOrchestrator> _logger;
    private readonly int _maxConcurrentJobs;
    private readonly TimeSpan _jobTimeout;

    public JobOrchestrator(
        IServiceScopeFactory scopeFactory,
        CloudProviderFactory providerFactory,
        CloudInitBuilder cloudInitBuilder,
        IEnumerable<INotificationService> notifiers,
        IConfiguration config,
        ILogger<JobOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _providerFactory = providerFactory;
        _cloudInitBuilder = cloudInitBuilder;
        _notifiers = notifiers;
        _logger = logger;
        _maxConcurrentJobs = config.GetValue("EgorBot:MaxConcurrentJobs", 4);
        _jobTimeout = TimeSpan.FromMinutes(config.GetValue("EgorBot:JobTimeoutMinutes", 60));
    }

    /// <summary>Enqueue a job ID for processing.</summary>
    public void Enqueue(Guid jobId)
    {
        _logger.LogInformation("Enqueuing job {JobId}", jobId);
        _jobQueue.Writer.TryWrite(jobId);
    }

    /// <summary>
    /// Called by the internal API when the agent reports completion.
    /// </summary>
    public void CompleteJob(Guid jobId, JobOutcome outcome)
    {
        _logger.LogInformation("CompleteJob called for {JobId}: Success={Success}", jobId, outcome.Success);
        if (_completions.TryGetValue(jobId, out var tcs))
        {
            tcs.TrySetResult(outcome);
            _logger.LogInformation("TCS signaled for job {JobId}", jobId);
        }
        else
        {
            _logger.LogWarning("CompleteJob called for unknown job {JobId} — TCS not found", jobId);
        }
    }

    /// <summary>
    /// Called by the internal API when the agent sends a heartbeat.
    /// </summary>
    public void RecordHeartbeat(Guid jobId)
    {
        _heartbeats[jobId] = DateTime.UtcNow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobOrchestrator started. MaxConcurrent={Max}, Timeout={Timeout}",
            _maxConcurrentJobs, _jobTimeout);

        // Startup recovery: clean up stale jobs
        await RecoverStaleJobsAsync(stoppingToken);

        var semaphore = new SemaphoreSlim(_maxConcurrentJobs, _maxConcurrentJobs);

        await foreach (var jobId in _jobQueue.Reader.ReadAllAsync(stoppingToken))
        {
            _logger.LogInformation("Dequeued job {JobId}, waiting for semaphore (available={Available}/{Max})",
                jobId, semaphore.CurrentCount, _maxConcurrentJobs);
            await semaphore.WaitAsync(stoppingToken);
            _logger.LogInformation("Semaphore acquired for job {JobId}", jobId);

            // Fire-and-forget each job (semaphore controls concurrency)
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessJobAsync(jobId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error processing job {JobId}", jobId);
                }
                finally
                {
                    semaphore.Release();
                    _logger.LogInformation("Semaphore released for job {JobId}", jobId);
                }
            }, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Jobs.FindAsync([jobId], ct);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} not found in DB", jobId);
            return;
        }

        string? instanceId = null;
        ICloudProvider? provider = null;
        var tcs = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completions[jobId] = tcs;
        _heartbeats[jobId] = DateTime.UtcNow;

        try
        {
            // 1. Update status → Provisioning
            job.Status = JobStatus.Provisioning;
            job.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await AddLogAsync(db, jobId, $"Job started. Platform={job.Platform}, Commits={job.CommitsAndPrs}");

            // Notify
            foreach (var n in _notifiers)
                await n.OnJobStartedAsync(job);

            // 2. Build cloud-init script
            _logger.LogInformation("[{JobId}] Building cloud-init script...", jobId);
            var cloudInitScript = _cloudInitBuilder.Build(job);
            _logger.LogInformation("[{JobId}] Cloud-init script length={Len}", jobId, cloudInitScript.Length);
            await AddLogAsync(db, jobId, "Cloud-init script generated.");

            // 3. Provision
            provider = _providerFactory.GetProvider(job.Platform);
            _logger.LogInformation("[{JobId}] Provisioning via {Provider}...", jobId, provider.Name);
            await AddLogAsync(db, jobId, $"Provisioning via {provider.Name}...");

            var request = new ProvisionRequest(
                JobId: jobId.ToString(),
                CloudInitScript: cloudInitScript,
                Platform: job.Platform,
                Job: job);

            var result = await provider.ProvisionAsync(request, ct);
            instanceId = result.InstanceId;
            job.CloudProviderInstanceId = instanceId;
            job.Status = JobStatus.Running;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[{JobId}] Provisioned. InstanceId={InstanceId}, IP={IP}",
                jobId, instanceId, result.IpAddress ?? "N/A");
            await AddLogAsync(db, jobId, $"Provisioned. InstanceId={instanceId}, IP={result.IpAddress ?? "N/A"}");

            // Notify: VM provisioned with SSH info
            foreach (var n in _notifiers)
                await n.OnVmProvisionedAsync(job, provider.Name, result.IpAddress);

            // 4. Wait for agent to report completion (or timeout)
            _logger.LogInformation("[{JobId}] Waiting for agent completion (timeout={Timeout}min)...",
                jobId, _jobTimeout.TotalMinutes);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_jobTimeout);

            try
            {
                var outcome = await tcs.Task.WaitAsync(timeoutCts.Token);

                if (outcome.Success)
                {
                    job.Status = JobStatus.Completed;
                    job.ResultMarkdown = outcome.ResultMarkdown;
                    await AddLogAsync(db, jobId, "Job completed successfully.");
                    foreach (var n in _notifiers)
                        await n.OnJobCompletedAsync(job);
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = outcome.Error ?? "Agent reported failure.";
                    await AddLogAsync(db, jobId, $"Job failed: {job.ErrorMessage}");
                    foreach (var n in _notifiers)
                        await n.OnJobFailedAsync(job, job.ErrorMessage);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                job.Status = JobStatus.TimedOut;
                job.ErrorMessage = $"Job timed out after {_jobTimeout.TotalMinutes} minutes.";
                await AddLogAsync(db, jobId, job.ErrorMessage);
                foreach (var n in _notifiers)
                    await n.OnJobFailedAsync(job, job.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", jobId);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            await AddLogAsync(db, jobId, $"Internal error: {ex.Message}");
            foreach (var n in _notifiers)
                await n.OnJobFailedAsync(job, ex.Message);
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            // Always deprovision
            if (instanceId is not null && provider is not null)
            {
                try
                {
                    await provider.DeprovisionAsync(instanceId, CancellationToken.None);
                    await AddLogAsync(db, jobId, "Instance deprovisioned.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deprovision instance {InstanceId} for job {JobId}",
                        instanceId, jobId);
                }
            }

            _completions.TryRemove(jobId, out _);
            _heartbeats.TryRemove(jobId, out _);
        }
    }

    private async Task RecoverStaleJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var staleJobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Provisioning || j.Status == JobStatus.Running)
            .ToListAsync(ct);

        foreach (var job in staleJobs)
        {
            _logger.LogWarning("Recovering stale job {JobId} (status={Status})", job.Id, job.Status);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = "Service restarted — job was in-progress and has been marked as failed.";
            job.CompletedAt = DateTime.UtcNow;

            // Attempt to deprovision if we have an instance ID
            if (job.CloudProviderInstanceId is not null)
            {
                try
                {
                    var provider = _providerFactory.GetProvider(job.Platform);
                    await provider.DeprovisionAsync(job.CloudProviderInstanceId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deprovision stale instance for job {JobId}", job.Id);
                }
            }
        }

        if (staleJobs.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private static async Task AddLogAsync(AppDbContext db, Guid jobId, string message)
    {
        db.JobLogs.Add(new JobLogEntry
        {
            JobId = jobId,
            Timestamp = DateTime.UtcNow,
            Message = message,
        });
        await db.SaveChangesAsync();
    }
}
