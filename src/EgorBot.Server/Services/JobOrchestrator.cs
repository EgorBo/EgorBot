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
    CorePoolManager corePool,
    IEnumerable<INotificationService> notifiers,
    RuntimeSettings runtimeSettings,
    IConfiguration config,
    ILogger<JobOrchestrator> logger)
    : BackgroundService
{
    private readonly Channel<Guid> _jobQueue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JobOutcome>> _completions = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _heartbeats = new();

    /// <summary>Per-job cancellation sources, so admin cancellation can unblock jobs
    /// that are waiting for cores or for a VM (they are not waiting on the completion TCS).</summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCts = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _terminalGates = new();
    private readonly SemaphoreSlim _rentLifecycleGate = new(1, 1);

    /// <summary>Jobs cancelled by an admin. Also covers jobs that are still sitting in
    /// the queue, so they are not provisioned after the cancel.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelled = new();

    /// <summary>Number of jobs currently holding cores from the pool.</summary>
    private int _activeRents;
    private int _pendingRents;

    /// <summary>
    /// Core rents deliberately kept after cloud teardown failed. Releasing these would
    /// let the queue exceed the real cloud quota while the orphaned VM still exists.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (string Platform, int Cores)> _retainedRents = new();

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
    /// Cancel one active job. Its processing loop owns final cleanup, including returning
    /// any rented cores; queued jobs are marked so they cannot start after cancellation.
    /// Returns false when the job does not exist or is already terminal.
    /// </summary>
    public async Task<bool> CancelJobAsync(Guid jobId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.FindAsync(jobId);
        return job is not null
            && await CancelActiveJobAsync(db, job, "CancelJob");
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

        var cancelledCount = 0;
        foreach (var job in activeJobs)
        {
            // One bad job (unknown platform, cloud API error, ...) must not abort
            // the whole cancellation — otherwise some jobs stay active and keep
            // holding cores while the admin thinks everything was cancelled.
            try
            {
                if (await CancelActiveJobAsync(db, job, "CancelAllJobs"))
                    cancelledCount++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CancelAllJobs: error while cancelling job {JobId}", job.Id);
            }
        }

        // If nothing holds cores anymore, whatever the pool still counts as "used" was
        // leaked by an earlier job. Reclaim it — this is the escape hatch for jobs stuck
        // on "Waiting for N cores from pool" while no VM exists. When jobs are still
        // winding down we leave the pool alone; they return their own cores.
        await _rentLifecycleGate.WaitAsync();
        try
        {
            var inFlight = Volatile.Read(ref _activeRents);
            var pending = Volatile.Read(ref _pendingRents);
            if (inFlight == 0 && pending == 0 && _retainedRents.IsEmpty)
            {
                var leaked = corePool.ResetAll();
                if (leaked > 0)
                    logger.LogWarning("CancelAllJobs: released {Cores} leaked core(s) from the pool", leaked);
            }
            else if (!_retainedRents.IsEmpty)
            {
                logger.LogWarning(
                    "CancelAllJobs: {Count} rent(s) are retained because cloud teardown failed — skipping pool reset",
                    _retainedRents.Count);
            }
            else
            {
                logger.LogInformation(
                    "CancelAllJobs: {Active} active and {Pending} pending rent(s) — skipping pool reset",
                    inFlight,
                    pending);
            }
        }
        finally
        {
            _rentLifecycleGate.Release();
        }

        return cancelledCount;
    }

    /// <summary>
    /// Force-release all pool accounting, including rents retained after failed cloud
    /// teardown. This is intentionally admin-only because the orphaned VM must be
    /// removed separately before new provisioning is safe.
    /// </summary>
    public async Task<int> ResetCorePoolAsync()
    {
        await _rentLifecycleGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _activeRents) > 0
                || Volatile.Read(ref _pendingRents) > 0)
            {
                throw new InvalidOperationException(
                    "Cannot reset the core pool while jobs are renting or holding cores. Cancel them first.");
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var retainedJobs = await db.Jobs
                .Where(j => j.RentedCores > 0
                         && j.Status != JobStatus.Pending
                         && j.Status != JobStatus.Provisioning
                         && j.Status != JobStatus.Running)
                .ToListAsync();
            foreach (var job in retainedJobs)
            {
                job.RentedCores = 0;
                job.CloudProviderInstanceId = null;
            }
            if (retainedJobs.Count > 0)
                await db.SaveChangesAsync();

            _retainedRents.Clear();
            return corePool.ResetAll();
        }
        finally
        {
            _rentLifecycleGate.Release();
        }
    }

    private async Task<bool> CancelActiveJobAsync(
        AppDbContext db,
        BenchmarkJob job,
        string operation)
    {
        var gate = GetTerminalGate(job.Id);
        bool processOwnsCleanup;
        CancellationTokenSource? cts;
        await gate.WaitAsync();
        try
        {
            await db.Entry(job).ReloadAsync();
            if (!IsActive(job.Status))
                return false;

            logger.LogWarning(
                "{Operation}: cancelling job {JobId} (status={Status}, platform={Platform})",
                operation, job.Id, job.Status, job.Platform);

            job.Status = JobStatus.Cancelled;
            job.ErrorMessage = "Cancelled by admin.";
            job.CompletedAt = DateTime.UtcNow;
            _cancelled[job.Id] = 0;
            processOwnsCleanup = _jobCts.TryGetValue(job.Id, out cts);
            await db.SaveChangesAsync();
        }
        finally
        {
            gate.Release();
        }

        // Unblock jobs waiting for the agent to report completion.
        if (_completions.TryGetValue(job.Id, out var completion))
            completion.TrySetResult(new JobOutcome(Success: false, Error: "Cancelled by admin."));

        // This also removes a core-pool waiter. If the job already holds cores,
        // ProcessJobAsync returns them from its finally block after deprovisioning.
        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Operation}: failed to cancel token for {JobId}", operation, job.Id);
            }
        }

        // Normally ProcessJobAsync owns deprovisioning. Handle an active database row
        // left without a processing loop as a fallback, without racing a double teardown.
        if (!processOwnsCleanup && job.CloudProviderInstanceId is not null)
        {
            try
            {
                var provider = providerFactory.GetProvider(job.Platform);
                var instanceId = job.CloudProviderInstanceId;
                await provider.DeprovisionAsync(instanceId, CancellationToken.None);
                if (job.RentedCores > 0)
                {
                    _retainedRents.TryRemove(job.Id, out _);
                    corePool.Return(job.Platform, job.RentedCores);
                }
                job.CloudProviderInstanceId = null;
                job.RentedCores = 0;
                await db.SaveChangesAsync();
                logger.LogInformation("{Operation}: deprovisioned {InstanceId} for job {JobId}",
                    operation, instanceId, job.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Operation}: failed to deprovision {InstanceId} for job {JobId}",
                    operation, job.CloudProviderInstanceId, job.Id);
                if (job.RentedCores > 0
                    && _retainedRents.TryAdd(
                        job.Id, (job.Platform, job.RentedCores)))
                {
                    corePool.Restore(job.Platform, job.RentedCores);
                }
            }
        }

        return true;
    }

    private static bool IsActive(JobStatus status) =>
        status is JobStatus.Pending or JobStatus.Provisioning or JobStatus.Running;

    private SemaphoreSlim GetTerminalGate(Guid jobId) =>
        _terminalGates.GetOrAdd(jobId, static _ => new SemaphoreSlim(1, 1));

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

        // Cores actually taken from the pool (0 = nothing rented yet). Must be
        // returned verbatim: DefaultCores can change while the job runs (admin
        // `cores N` command) and returning a different amount leaks the difference.
        var rentedCores = 0;
        var releaseRentedCores = true;

        var tcs = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completions[jobId] = tcs;
        _heartbeats[jobId] = DateTime.UtcNow;

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _jobCts[jobId] = jobCts;
        var jobToken = jobCts.Token;

        try
        {
            // 0. The job may have been cancelled while it was still queued.
            if (_cancelled.ContainsKey(jobId))
            {
                logger.LogWarning("[{JobId}] Job was cancelled before it started — skipping", jobId);
                job.Status = JobStatus.Cancelled;
                job.ErrorMessage = "Cancelled by admin.";
                await AddLogAsync(db, jobId, "Job was cancelled before it started.");
                return;
            }

            // 1. Update status → Provisioning
            job.Status = JobStatus.Provisioning;
            job.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(jobToken);
            await AddLogAsync(db, jobId, $"Job started. Platform={job.Platform}, Commits={job.CommitsAndPrs}");

            // Notify
            foreach (var n in notifiers)
                await n.OnJobStartedAsync(job);

            // 2. Build cloud-init script
            logger.LogInformation("[{JobId}] Building cloud-init script...", jobId);
            var cloudInitScript = cloudInitBuilder.Build(job);
            logger.LogInformation("[{JobId}] Cloud-init script length={Len}", jobId, cloudInitScript.Length);
            await AddLogAsync(db, jobId, "Cloud-init script generated.");

            // 2b. Acquire cores from the pool (waits if quota is exhausted)
            var poolState = corePool.GetPoolState(job.Platform);
            var requestedCores = runtimeSettings.DefaultCores;
            var coresToRent = CoreCountPolicy.Negotiate(requestedCores, poolState.Total);

            if (coresToRent == 0)
            {
                throw new InvalidOperationException(
                    $"Cannot run on {job.Platform}: {requestedCores} cores were requested but the pool only " +
                    $"holds {poolState.Total}, which is below the {CoreCountPolicy.MinimumClampedCores}-core minimum. " +
                    $"Raise the cloud quota or lower the core count.");
            }

            if (coresToRent != requestedCores)
            {
                logger.LogWarning("[{JobId}] Requested {Requested} cores but the pool for {Platform} holds {Total} — using {Cores}",
                    jobId, requestedCores, job.Platform, poolState.Total, coresToRent);
                await AddLogAsync(db, jobId,
                    $"Requested {requestedCores} cores, but this pool holds {poolState.Total} — " +
                    $"running on {coresToRent} cores (largest power of two that fits).");
            }

            logger.LogInformation("[{JobId}] Requesting {Cores} cores from pool for {Platform} (used {Used}/{Total}, {Waiters} waiting)...",
                jobId, coresToRent, job.Platform, poolState.Used, poolState.Total, poolState.Waiters);
            await AddLogAsync(db, jobId,
                $"Waiting for {coresToRent} cores from pool ({poolState.Used}/{poolState.Total} in use, {poolState.Waiters} job(s) already queued)...");
            await _rentLifecycleGate.WaitAsync(jobToken);
            try
            {
                Interlocked.Increment(ref _pendingRents);
            }
            finally
            {
                _rentLifecycleGate.Release();
            }

            try
            {
                await corePool.RentAsync(job.Platform, coresToRent, jobToken);
            }
            catch
            {
                Interlocked.Decrement(ref _pendingRents);
                throw;
            }

            await _rentLifecycleGate.WaitAsync(CancellationToken.None);
            try
            {
                Interlocked.Decrement(ref _pendingRents);
                rentedCores = coresToRent;
                job.RentedCores = rentedCores;
                Interlocked.Increment(ref _activeRents);
            }
            finally
            {
                _rentLifecycleGate.Release();
            }
            logger.LogInformation("[{JobId}] Acquired {Cores} cores", jobId, coresToRent);
            await AddLogAsync(db, jobId, $"Acquired {coresToRent} cores from pool.");

            // 3. Provision
            provider = providerFactory.GetProvider(job.Platform);
            logger.LogInformation("[{JobId}] Provisioning via {Provider}...", jobId, provider.Name);
            await AddLogAsync(db, jobId, $"Provisioning via {provider.Name}...");

            var request = new ProvisionRequest(
                JobId: jobId.ToString(),
                CloudInitScript: cloudInitScript,
                Platform: job.Platform,
                Job: job,
                Cores: coresToRent);

            var result = await provider.ProvisionAsync(request, jobToken);
            instanceId = result.InstanceId;
            job.CloudProviderInstanceId = instanceId;
            job.Status = JobStatus.Running;
            await db.SaveChangesAsync(jobToken);
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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                var outcome = await tcs.Task.WaitAsync(timeoutCts.Token);
                var proposedStatus = outcome.Success ? JobStatus.Completed : JobStatus.Failed;
                var proposedError = outcome.Success
                    ? null
                    : outcome.Error ?? "Agent reported failure.";
                await SetTerminalStateAsync(
                    db, job, proposedStatus, proposedError, outcome.ResultMarkdown);

                if (job.Status == JobStatus.Completed)
                {
                    await SafeAddLogAsync(db, jobId, "Job completed successfully.");
                    foreach (var n in notifiers)
                        await n.OnJobCompletedAsync(job);
                }
                else
                {
                    var prefix = job.Status == JobStatus.Cancelled ? "Job cancelled" : "Job failed";
                    await SafeAddLogAsync(db, jobId, $"{prefix}: {job.ErrorMessage}");
                    foreach (var n in notifiers)
                        await n.OnJobFailedAsync(job, job.ErrorMessage ?? prefix);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                    && !jobToken.IsCancellationRequested)
            {
                await SetTerminalStateAsync(
                    db,
                    job,
                    JobStatus.TimedOut,
                    $"Job timed out after {effectiveTimeout.TotalMinutes} minutes.");
                await SafeAddLogAsync(db, jobId, job.ErrorMessage!);
                foreach (var n in notifiers)
                    await n.OnJobFailedAsync(job, job.ErrorMessage!);
            }
        }
        catch (OperationCanceledException) when (_cancelled.ContainsKey(jobId))
        {
            logger.LogWarning("[{JobId}] Job cancelled by admin", jobId);
            await SetTerminalStateAsync(
                db, job, JobStatus.Cancelled, "Cancelled by admin.");
            await SafeAddLogAsync(db, jobId, "Job cancelled by admin.");
            foreach (var n in notifiers)
                await n.OnJobFailedAsync(job, job.ErrorMessage!);
        }
        catch (ProvisioningCleanupException ex)
        {
            instanceId = ex.InstanceId;
            job.CloudProviderInstanceId = instanceId;
            logger.LogError(ex, "Provisioning cleanup failed for job {JobId}; teardown will be retried", jobId);

            await SetTerminalStateAsync(
                db, job, JobStatus.Failed, ex.Message);
            await SafeAddLogAsync(db, jobId, job.ErrorMessage!);
            foreach (var n in notifiers)
                await n.OnJobFailedAsync(job, job.ErrorMessage!);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing job {JobId}", jobId);
            await SetTerminalStateAsync(db, job, JobStatus.Failed, ex.Message);
            await SafeAddLogAsync(db, jobId, $"Internal error: {ex.Message}");
            foreach (var n in notifiers)
                await n.OnJobFailedAsync(job, job.ErrorMessage ?? ex.Message);
        }
        finally
        {
            // Everything in here is best-effort: a failure while saving the status or
            // deprovisioning must never skip the core return, otherwise the pool leaks
            // and every later job hangs on "Waiting for N cores from pool".
            try
            {
                job.CompletedAt = DateTime.UtcNow;

                // Generate self-hosted log URL BEFORE saving status,
                // so LogsBlobUrl is set when the GitHub poller first sees the terminal status.
                try
                {
                    var logsBlobUrl = await logUploadService.UploadJobLogsAsync(jobId);
                    if (logsBlobUrl is not null)
                    {
                        job.LogsBlobUrl = logsBlobUrl;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to upload logs for job {JobId}", jobId);
                }

                try
                {
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to save final state of job {JobId}", jobId);
                }

                // Do not release the cloud-quota lease until teardown succeeds. Otherwise
                // a queued job can race the still-existing VM and fail quota validation.
                if (instanceId is not null)
                {
                    if (provider is null)
                    {
                        releaseRentedCores = false;
                        logger.LogError(
                            "Cannot deprovision instance {InstanceId} for job {JobId}: cloud provider is unavailable",
                            instanceId, jobId);
                    }
                    else
                    {
                        try
                        {
                            await provider.DeprovisionAsync(instanceId, CancellationToken.None);
                            job.CloudProviderInstanceId = null;
                            job.RentedCores = 0;
                            await SafeAddLogAsync(db, jobId, "Instance deprovisioned.");
                            try
                            {
                                await db.SaveChangesAsync(CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(
                                    ex,
                                    "Instance {InstanceId} was deprovisioned, but cleanup state for job {JobId} could not be saved",
                                    instanceId,
                                    jobId);
                            }
                        }
                        catch (Exception ex)
                        {
                            releaseRentedCores = false;
                            logger.LogError(ex, "Failed to deprovision instance {InstanceId} for job {JobId}; retaining {Cores} pool cores",
                                instanceId, jobId, rentedCores);
                            await SafeAddLogAsync(db, jobId,
                                $"Instance deprovision failed; retaining {rentedCores} pool cores to avoid exceeding cloud quota.");
                            try
                            {
                                job.RentedCores = rentedCores;
                                await db.SaveChangesAsync(CancellationToken.None);
                            }
                            catch (Exception saveEx)
                            {
                                logger.LogError(
                                    saveEx,
                                    "Failed to persist retained core lease for job {JobId}",
                                    jobId);
                            }
                        }
                    }
                }
            }
            finally
            {
                // Return exactly what was rented (DefaultCores may have changed meanwhile),
                // and only if the rent actually succeeded and cloud teardown completed.
                if (rentedCores > 0)
                {
                    if (releaseRentedCores)
                    {
                        corePool.Return(job.Platform, rentedCores);
                        logger.LogInformation("[{JobId}] Returned {Cores} cores to pool", jobId, rentedCores);
                    }
                    else
                    {
                        _retainedRents[jobId] = (job.Platform, rentedCores);
                        logger.LogWarning("[{JobId}] Retained {Cores} pool cores because instance teardown did not complete",
                            jobId, rentedCores);
                    }

                    Interlocked.Decrement(ref _activeRents);
                }

                _completions.TryRemove(jobId, out _);
                _heartbeats.TryRemove(jobId, out _);
                _jobCts.TryRemove(jobId, out _);
                _cancelled.TryRemove(jobId, out _);
                _terminalGates.TryRemove(jobId, out _);
            }
        }
    }

    private async Task SetTerminalStateAsync(
        AppDbContext db,
        BenchmarkJob job,
        JobStatus proposedStatus,
        string? proposedError,
        string? resultMarkdown = null)
    {
        var gate = GetTerminalGate(job.Id);
        await gate.WaitAsync();
        try
        {
            if (_cancelled.ContainsKey(job.Id))
            {
                job.Status = JobStatus.Cancelled;
                job.ErrorMessage = "Cancelled by admin.";
                job.ResultMarkdown = null;
            }
            else if (!IsActive(job.Status))
            {
                return;
            }
            else
            {
                job.Status = proposedStatus;
                job.ErrorMessage = proposedError;
                job.ResultMarkdown = resultMarkdown;
            }

            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Adds a log entry, swallowing failures (used from catch/cleanup paths).</summary>
    private async Task SafeAddLogAsync(AppDbContext db, Guid jobId, string message)
    {
        try
        {
            await AddLogAsync(db, jobId, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write log entry for job {JobId}", jobId);
        }
    }

    private async Task RecoverStaleJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var staleJobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Provisioning
                     || j.Status == JobStatus.Running
                     || (j.CloudProviderInstanceId != null && j.RentedCores > 0))
            .ToListAsync(ct);

        foreach (var job in staleJobs)
        {
            var wasActive = job.Status is JobStatus.Provisioning or JobStatus.Running;
            var retainedCores = job.RentedCores;
            logger.LogWarning(
                "Recovering job {JobId} (status={Status}, instance={InstanceId}, retained={Cores})",
                job.Id, job.Status, job.CloudProviderInstanceId, job.RentedCores);
            if (wasActive)
            {
                job.Status = JobStatus.Failed;
                job.ErrorMessage = "Service restarted — job was in-progress and has been marked as failed.";
                job.CompletedAt = DateTime.UtcNow;
            }

            // Attempt to deprovision if we have an instance ID
            if (job.CloudProviderInstanceId is not null)
            {
                try
                {
                    var provider = providerFactory.GetProvider(job.Platform);
                    await provider.DeprovisionAsync(job.CloudProviderInstanceId, ct);
                    job.CloudProviderInstanceId = null;
                    job.RentedCores = 0;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to deprovision stale instance for job {JobId}", job.Id);
                    job.RentedCores = retainedCores;
                    if (retainedCores > 0
                        && _retainedRents.TryAdd(
                            job.Id, (job.Platform, retainedCores)))
                    {
                        corePool.Restore(job.Platform, retainedCores);
                    }
                }
            }
            else if (retainedCores > 0)
            {
                try
                {
                    var provider = providerFactory.GetProvider(job.Platform);
                    var reconciled = await provider.TryDeprovisionByJobIdAsync(
                        job.Id.ToString(), ct);
                    if (reconciled)
                    {
                        job.RentedCores = 0;
                    }
                    else
                    {
                        RetainRecoveredRent(job, retainedCores);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to reconcile provisioning resource for job {JobId}",
                        job.Id);
                    RetainRecoveredRent(job, retainedCores);
                }
            }
        }

        if (staleJobs.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private void RetainRecoveredRent(BenchmarkJob job, int cores)
    {
        job.RentedCores = cores;
        if (_retainedRents.TryAdd(job.Id, (job.Platform, cores)))
            corePool.Restore(job.Platform, cores);
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
