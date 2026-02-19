using System.Collections.Concurrent;
using System.Threading.Channels;
using EgorBot.Server.Data;
using EgorBot.Server.Models;
using EgorBot.Server.Services.CloudInit;
using EgorBot.Server.Services.CloudProviders;
using EgorBot.Server.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Server.Services;

/// <summary>
/// Outcome posted by the agent (or timeout logic) to signal job completion.
/// </summary>
public sealed record JobOutcome(bool Success, string? ResultMarkdown = null, string? Error = null);

/// <summary>
/// Background service that manages the lifecycle of benchmark jobs:
/// dequeues pending jobs, provisions VMs, waits for agent completion, cleans up.
/// </summary>
public sealed class JobOrchestrator(
    IServiceScopeFactory scopeFactory,
    CloudProviderFactory providerFactory,
    CloudInitBuilder cloudInitBuilder,
    LogUploadService logUploadService,
    IEnumerable<INotificationService> notifiers,
    IConfiguration config,
    ILogger<JobOrchestrator> logger)
    : BackgroundService
{
    private readonly Channel<Guid> _jobQueue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JobOutcome>> _completions = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _heartbeats = new();

    private readonly int _maxConcurrentJobs = config.GetValue("EgorBot:MaxConcurrentJobs", 4);
    private readonly TimeSpan _jobTimeout = TimeSpan.FromMinutes(config.GetValue("EgorBot:JobTimeoutMinutes", 60));
    private readonly TimeSpan _helixJobTimeout = TimeSpan.FromMinutes(config.GetValue("EgorBot:HelixJobTimeoutMinutes", 150));

    /// <summary>Enqueue a job ID for processing.</summary>
    public void Enqueue(Guid jobId)
    {
        logger.LogInformation("Enqueuing job {JobId}", jobId);
        _jobQueue.Writer.TryWrite(jobId);
    }

    /// <summary>
    /// Called by the internal API when the agent reports completion.
    /// </summary>
    public void CompleteJob(Guid jobId, JobOutcome outcome)
    {
        logger.LogInformation("CompleteJob called for {JobId}: Success={Success}", jobId, outcome.Success);
        if (_completions.TryGetValue(jobId, out var tcs))
        {
            tcs.TrySetResult(outcome);
            logger.LogInformation("TCS signaled for job {JobId}", jobId);
        }
        else
        {
            logger.LogWarning("CompleteJob called for unknown job {JobId} — TCS not found", jobId);
        }
    }

    /// <summary>
    /// Called by the internal API when the agent sends a heartbeat.
    /// </summary>
    public void RecordHeartbeat(Guid jobId)
    {
        _heartbeats[jobId] = DateTime.UtcNow;
    }

    /// <summary>
    /// Cancel all active jobs, mark them as Cancelled, and deprovision their VMs.
    /// Returns the number of jobs cancelled.
    /// </summary>
    public async Task<int> CancelAllJobsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var activeJobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending
                     || j.Status == JobStatus.Provisioning
                     || j.Status == JobStatus.Running)
            .ToListAsync();

        foreach (var job in activeJobs)
        {
            logger.LogWarning("CancelAllJobs: cancelling job {JobId} (status={Status}, platform={Platform})",
                job.Id, job.Status, job.Platform);

            job.Status = JobStatus.Cancelled;
            job.ErrorMessage = "Cancelled by admin.";
            job.CompletedAt = DateTime.UtcNow;

            // Signal the TCS so ProcessJobAsync unblocks and proceeds to cleanup
            if (_completions.TryGetValue(job.Id, out var tcs))
                tcs.TrySetResult(new JobOutcome(Success: false, Error: "Cancelled by admin."));

            // Deprovision VM/instance
            if (job.CloudProviderInstanceId is not null)
            {
                try
                {
                    var provider = providerFactory.GetProvider(job.Platform);
                    await provider.DeprovisionAsync(job.CloudProviderInstanceId, CancellationToken.None);
                    logger.LogInformation("CancelAllJobs: deprovisioned {InstanceId} for job {JobId}",
                        job.CloudProviderInstanceId, job.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "CancelAllJobs: failed to deprovision {InstanceId} for job {JobId}",
                        job.CloudProviderInstanceId, job.Id);
                }
            }
        }

        if (activeJobs.Count > 0)
            await db.SaveChangesAsync();

        return activeJobs.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JobOrchestrator started. MaxConcurrent={Max}, Timeout={Timeout}",
            _maxConcurrentJobs, _jobTimeout);

        // Startup recovery: clean up stale jobs
        await RecoverStaleJobsAsync(stoppingToken);

        var semaphore = new SemaphoreSlim(_maxConcurrentJobs, _maxConcurrentJobs);

        await foreach (var jobId in _jobQueue.Reader.ReadAllAsync(stoppingToken))
        {
            logger.LogInformation("Dequeued job {JobId}, waiting for semaphore (available={Available}/{Max})",
                jobId, semaphore.CurrentCount, _maxConcurrentJobs);
            await semaphore.WaitAsync(stoppingToken);
            logger.LogInformation("Semaphore acquired for job {JobId}", jobId);

            // Fire-and-forget each job (semaphore controls concurrency)
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessJobAsync(jobId, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error processing job {JobId}", jobId);
                }
                finally
                {
                    semaphore.Release();
                    logger.LogInformation("Semaphore released for job {JobId}", jobId);
                }
            }, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Jobs.FindAsync([jobId], ct);
        if (job is null)
        {
            logger.LogWarning("Job {JobId} not found in DB", jobId);
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
            foreach (var n in notifiers)
                await n.OnJobStartedAsync(job);

            // 2. Build cloud-init script
            logger.LogInformation("[{JobId}] Building cloud-init script...", jobId);
            var cloudInitScript = cloudInitBuilder.Build(job);
            logger.LogInformation("[{JobId}] Cloud-init script length={Len}", jobId, cloudInitScript.Length);
            await AddLogAsync(db, jobId, "Cloud-init script generated.");

            // 3. Provision
            provider = providerFactory.GetProvider(job.Platform);
            logger.LogInformation("[{JobId}] Provisioning via {Provider}...", jobId, provider.Name);
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
            logger.LogInformation("[{JobId}] Provisioned. InstanceId={InstanceId}, IP={IP}",
                jobId, instanceId, result.IpAddress ?? "N/A");
            await AddLogAsync(db, jobId, $"Provisioned. InstanceId={instanceId}, IP={result.IpAddress ?? "N/A"}");

            // Notify: VM provisioned with SSH info
            foreach (var n in notifiers)
                await n.OnVmProvisionedAsync(job, provider.Name, result.IpAddress);

            // 4. Wait for agent to report completion (or timeout)
            var effectiveTimeout = provider.Name == "Helix" ? _helixJobTimeout : _jobTimeout;
            logger.LogInformation("[{JobId}] Waiting for agent completion (timeout={Timeout}min)...",
                jobId, effectiveTimeout.TotalMinutes);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                var outcome = await tcs.Task.WaitAsync(timeoutCts.Token);

                if (outcome.Success)
                {
                    job.Status = JobStatus.Completed;
                    job.ResultMarkdown = outcome.ResultMarkdown;
                    await AddLogAsync(db, jobId, "Job completed successfully.");
                    foreach (var n in notifiers)
                        await n.OnJobCompletedAsync(job);
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = outcome.Error ?? "Agent reported failure.";
                    await AddLogAsync(db, jobId, $"Job failed: {job.ErrorMessage}");
                    foreach (var n in notifiers)
                        await n.OnJobFailedAsync(job, job.ErrorMessage);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                job.Status = JobStatus.TimedOut;
                job.ErrorMessage = $"Job timed out after {effectiveTimeout.TotalMinutes} minutes.";
                await AddLogAsync(db, jobId, job.ErrorMessage);
                foreach (var n in notifiers)
                    await n.OnJobFailedAsync(job, job.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing job {JobId}", jobId);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            await AddLogAsync(db, jobId, $"Internal error: {ex.Message}");
            foreach (var n in notifiers)
                await n.OnJobFailedAsync(job, ex.Message);
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            // Upload full logs to Azure Blob Storage
            try
            {
                var logsBlobUrl = await logUploadService.UploadJobLogsAsync(db, jobId);
                if (logsBlobUrl is not null)
                {
                    job.LogsBlobUrl = logsBlobUrl;
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload logs for job {JobId}", jobId);
            }

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
                    logger.LogError(ex, "Failed to deprovision instance {InstanceId} for job {JobId}",
                        instanceId, jobId);
                }
            }

            _completions.TryRemove(jobId, out _);
            _heartbeats.TryRemove(jobId, out _);
        }
    }

    private async Task RecoverStaleJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var staleJobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Provisioning || j.Status == JobStatus.Running)
            .ToListAsync(ct);

        foreach (var job in staleJobs)
        {
            logger.LogWarning("Recovering stale job {JobId} (status={Status})", job.Id, job.Status);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = "Service restarted — job was in-progress and has been marked as failed.";
            job.CompletedAt = DateTime.UtcNow;

            // Attempt to deprovision if we have an instance ID
            if (job.CloudProviderInstanceId is not null)
            {
                try
                {
                    var provider = providerFactory.GetProvider(job.Platform);
                    await provider.DeprovisionAsync(job.CloudProviderInstanceId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to deprovision stale instance for job {JobId}", job.Id);
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
