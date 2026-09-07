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

public sealed record CancelAllResult(int CancelledJobs, int ActiveJobs, int ReservedCores, int PendingCleanup);

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
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _processing = new();
    private readonly ConcurrentDictionary<Guid, Task<ProvisionResult>> _unfinishedProvisions = new();

    /// <summary>Per-job cancellation sources, so admin cancellation can unblock jobs
    /// that are waiting for cores or for a VM (they are not waiting on the completion TCS).</summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCts = new();
    private readonly SemaphoreSlim _jobStateGate = new(1, 1);
    private readonly SemaphoreSlim _rentLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private readonly SemaphoreSlim _cancelAllGate = new(1, 1);

    /// <summary>Admin cancellations that must win terminal-state races with a processing loop.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelled = new();

    /// <summary>Number of jobs currently holding cores from the pool.</summary>
    private int _activeRents;
    private int _pendingRents;
    private readonly DateTime _startupAt = DateTime.UtcNow;
    private int _startupRecoveryComplete;

    public bool StartupRecoveryComplete => Volatile.Read(ref _startupRecoveryComplete) != 0;

    /// <summary>
    /// Core rents deliberately kept after cloud teardown failed. Releasing these would
    /// let the queue exceed the real cloud quota while the orphaned VM still exists.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (string Platform, int Cores)> _retainedRents = new();

    private readonly int _maxConcurrentJobs = config.GetValue("EgorBot:MaxConcurrentJobs", 4);
    private readonly TimeSpan _jobTimeout = TimeSpan.FromMinutes(config.GetValue("EgorBot:JobTimeoutMinutes", 60));
    private readonly TimeSpan _helixJobTimeout = TimeSpan.FromMinutes(config.GetValue("EgorBot:HelixJobTimeoutMinutes", 150));
    private readonly TimeSpan _queueTimeout = TimeSpan.FromMinutes(
        Math.Max(0.01, config.GetValue("EgorBot:QueueTimeoutMinutes", 120.0)));
    private readonly TimeSpan _provisioningTimeout = TimeSpan.FromMinutes(
        Math.Max(0.01, config.GetValue("EgorBot:ProvisioningTimeoutMinutes", 30.0)));
    private readonly TimeSpan _notificationTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(
        Math.Max(1, config.GetValue("EgorBot:CleanupTimeoutSeconds", 300)));
    private readonly TimeSpan _cleanupRetryInterval = TimeSpan.FromSeconds(
        Math.Max(1, config.GetValue("EgorBot:CleanupRetrySeconds", 60)));

    /// <summary>Enqueue a job ID for processing.</summary>
    public void Enqueue(Guid jobId)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_processing.TryAdd(jobId, completion))
        {
            logger.LogWarning("Job {JobId} is already queued or processing", jobId);
            return;
        }

        logger.LogInformation("Enqueuing job {JobId}", jobId);
        if (!_jobQueue.Writer.TryWrite(jobId))
        {
            _processing.TryRemove(jobId, out _);
            completion.TrySetResult();
            logger.LogWarning("Job {JobId} could not be queued during shutdown; it will be recovered on restart", jobId);
        }
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
    /// Waits for bounded cleanup and reports reservations whose deletion is still unconfirmed.
    /// </summary>
    public async Task<CancelAllResult> CancelAllJobsAsync(CancellationToken ct = default)
    {
        await _cancelAllGate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var activeJobs = await db.Jobs
                .Where(j => j.Status == JobStatus.Pending
                         || j.Status == JobStatus.Provisioning
                         || j.Status == JobStatus.Running)
                .ToListAsync(ct);

            var processing = _processing.Values.Select(completion => completion.Task).ToArray();

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

            using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cleanupCts.CancelAfter(_cleanupTimeout + _notificationTimeout);
            try
            {
                await Task.WhenAll(
                    Task.WhenAll(processing),
                    ReconcileRetainedRentsAsync(cleanupCts.Token)).WaitAsync(cleanupCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && cleanupCts.IsCancellationRequested)
            {
                logger.LogWarning("CancelAllJobs: cleanup deadline reached; unconfirmed leases remain reserved");
            }

            // If nothing holds cores anymore, whatever the pool still counts as "used" was
            // leaked by an earlier job. Reclaim it — this is the escape hatch for jobs stuck
            // on "Waiting for N cores from pool" while no VM exists. When jobs are still
            // winding down we leave the pool alone; they return their own cores.
            var activeCount = await db.Jobs.CountAsync(j =>
                j.Status == JobStatus.Pending || j.Status == JobStatus.Provisioning || j.Status == JobStatus.Running, ct);
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

                return new CancelAllResult(
                    cancelledCount, activeCount,
                    corePool.GetSnapshot().Values.Sum(p => p.Used),
                    _retainedRents.Count + inFlight);
            }
            finally
            {
                _rentLifecycleGate.Release();
            }
        }
        finally
        {
            _cancelAllGate.Release();
        }
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
                || Volatile.Read(ref _pendingRents) > 0
                || !_unfinishedProvisions.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Cannot reset the core pool while jobs are renting, holding cores, or still provisioning. Cancel them and wait for provisioning to stop first.");
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
        var gate = _jobStateGate;
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

            processOwnsCleanup = _jobCts.TryGetValue(job.Id, out cts);
            if (!processOwnsCleanup && job.Status == JobStatus.Pending && job.CloudProviderInstanceId is null)
                job.RentedCores = 0;
            job.Status = JobStatus.Cancelled;
            job.ErrorMessage = "Cancelled by admin.";
            job.CompletedAt = DateTime.UtcNow;
            if (processOwnsCleanup)
                _cancelled[job.Id] = 0;
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
                ObserveLateOperation(cts.CancelAsync(), $"Cancellation callbacks for job '{job.Id}'");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Operation}: failed to cancel token for {JobId}", operation, job.Id);
            }
        }

        // Normally ProcessJobAsync owns deprovisioning. Handle an active database row
        // left without a processing loop as a fallback, without racing a double teardown.
        if (!processOwnsCleanup && (job.CloudProviderInstanceId is not null || job.RentedCores > 0))
        {
            await _rentLifecycleGate.WaitAsync();
            try
            {
                await db.Entry(job).ReloadAsync();
                RetainRecoveredRent(job, job.RentedCores);
            }
            finally
            {
                _rentLifecycleGate.Release();
            }
        }

        return true;
    }

    private static bool IsActive(JobStatus status) =>
        status is JobStatus.Pending or JobStatus.Provisioning or JobStatus.Running;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("JobOrchestrator started. MaxConcurrent={Max}, Timeout={Timeout}",
            _maxConcurrentJobs, _jobTimeout);

        // Startup recovery: clean up stale jobs
        await RecoverStaleJobsAsync(stoppingToken);
        using (var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            cleanupCts.CancelAfter(_cleanupTimeout);
            try
            {
                await ReconcileRetainedRentsAsync(cleanupCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && cleanupCts.IsCancellationRequested)
            {
                logger.LogWarning("Startup cleanup deadline reached; unconfirmed leases remain reserved for retry");
            }
        }
        Volatile.Write(ref _startupRecoveryComplete, 1);

        using var reconciliationCts =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var reconciliationTask =
            ReconcileRetainedRentsLoopAsync(reconciliationCts.Token);
        using var semaphore = new SemaphoreSlim(_maxConcurrentJobs, _maxConcurrentJobs);

        try
        {
            await foreach (var jobId in _jobQueue.Reader.ReadAllAsync(stoppingToken))
            {
                // Core waiters must not occupy execution slots for unrelated pools.
                _ = RunQueuedJobAsync(jobId, semaphore, stoppingToken);
            }
        }
        finally
        {
            _jobQueue.Writer.TryComplete();
            while (_jobQueue.Reader.TryRead(out var queuedId))
            {
                if (_processing.TryRemove(queuedId, out var completion))
                    completion.TrySetResult();
            }
            await reconciliationCts.CancelAsync();
            await reconciliationTask;
            await Task.WhenAll(_processing.Values.Select(c => c.Task));
        }
    }

    private async Task RunQueuedJobAsync(Guid jobId, SemaphoreSlim semaphore, CancellationToken ct)
    {
        try
        {
            await ProcessJobAsync(jobId, semaphore, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing job {JobId}", jobId);
        }
        finally
        {
            if (_processing.TryRemove(jobId, out var completion))
                completion.TrySetResult();
        }
    }

    private async Task ProcessJobAsync(Guid jobId, SemaphoreSlim semaphore, CancellationToken ct)
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
        var executionSlot = false;
        var ownsJob = false;
        Task<ProvisionResult>? provisioningTask = null;

        var tcs = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var jobToken = jobCts.Token;
        using var queueCts = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        using var provisioningCts = CancellationTokenSource.CreateLinkedTokenSource(jobToken);

        try
        {
            await _jobStateGate.WaitAsync(jobToken);
            try
            {
                await db.Entry(job).ReloadAsync(jobToken);
                if (job.Status != JobStatus.Pending)
                {
                    logger.LogInformation("[{JobId}] Skipping job with status {Status}", jobId, job.Status);
                    return;
                }
                _jobCts[jobId] = jobCts;
                ownsJob = true;
                _completions[jobId] = tcs;
                _heartbeats[jobId] = DateTime.UtcNow;
            }
            finally
            {
                _jobStateGate.Release();
            }

            var remainingQueueTime = _queueTimeout - (DateTime.UtcNow - job.CreatedAt);
            queueCts.CancelAfter(remainingQueueTime > TimeSpan.Zero ? remainingQueueTime : TimeSpan.Zero);
            queueCts.Token.ThrowIfCancellationRequested();

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
                $"Waiting for {coresToRent} cores from pool ({Math.Max(0, poolState.Total - poolState.Used)}/{poolState.Total} available, " +
                $"{poolState.Used} reserved, {poolState.Waiters} job(s) already queued)...");
            await _rentLifecycleGate.WaitAsync(queueCts.Token);
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
                await corePool.RentAsync(job.Platform, coresToRent, queueCts.Token);
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

            await AddLogAsync(db, jobId, "Waiting for an execution slot...");
            await semaphore.WaitAsync(queueCts.Token);
            executionSlot = true;
            queueCts.Token.ThrowIfCancellationRequested();
            queueCts.CancelAfter(Timeout.InfiniteTimeSpan);
            await SetPhaseAsync(db, job, JobStatus.Provisioning, jobToken);
            await AddLogAsync(db, jobId, $"Job started. Platform={job.Platform}, Commits={job.CommitsAndPrs}");
            await NotifyAsync(job, n => n.OnJobStartedAsync(job));

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

            provisioningCts.CancelAfter(_provisioningTimeout);
            var provisioningToken = provisioningCts.Token;
            provisioningToken.ThrowIfCancellationRequested();
            provisioningTask = Task.Run(
                () => provider.ProvisionAsync(request, provisioningToken), CancellationToken.None);
            var result = await provisioningTask.WaitAsync(provisioningToken);
            provisioningCts.CancelAfter(Timeout.InfiniteTimeSpan);
            instanceId = result.InstanceId;
            job.CloudProviderInstanceId = instanceId;
            await SetPhaseAsync(db, job, JobStatus.Running, jobToken);
            logger.LogInformation("[{JobId}] Provisioned. InstanceId={InstanceId}, IP={IP}",
                jobId, instanceId, result.IpAddress ?? "N/A");
            await AddLogAsync(db, jobId, $"Provisioned. InstanceId={instanceId}, IP={result.IpAddress ?? "N/A"}");

            // Notify: VM provisioned with SSH info
            await NotifyAsync(job, n => n.OnVmProvisionedAsync(job, provider.Name, result.IpAddress));

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
                    await NotifyAsync(job, n => n.OnJobCompletedAsync(job));
                }
                else
                {
                    var prefix = job.Status == JobStatus.Cancelled ? "Job cancelled" : "Job failed";
                    await SafeAddLogAsync(db, jobId, $"{prefix}: {job.ErrorMessage}");
                    await NotifyAsync(job, n => n.OnJobFailedAsync(job, job.ErrorMessage ?? prefix));
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
                await NotifyAsync(job, n => n.OnJobFailedAsync(job, job.ErrorMessage!));
            }
        }
        catch (OperationCanceledException) when (_cancelled.ContainsKey(jobId))
        {
            logger.LogWarning("[{JobId}] Job cancelled by admin", jobId);
            await SetTerminalStateAsync(
                db, job, JobStatus.Cancelled, "Cancelled by admin.");
            await SafeAddLogAsync(db, jobId, "Job cancelled by admin.");
            await NotifyAsync(job, n => n.OnJobFailedAsync(job, job.ErrorMessage!));
        }
        catch (OperationCanceledException) when (!jobToken.IsCancellationRequested
            && (queueCts.IsCancellationRequested || provisioningCts.IsCancellationRequested))
        {
            var error = queueCts.IsCancellationRequested
                ? $"Job timed out waiting for cores or an execution slot after {_queueTimeout.TotalMinutes} minutes from submission."
                : $"VM provisioning timed out after {_provisioningTimeout.TotalMinutes} minutes.";
            await SetTerminalStateAsync(db, job, JobStatus.TimedOut, error);
            await SafeAddLogAsync(db, jobId, error);
            await NotifyAsync(job, n => n.OnJobFailedAsync(job, job.ErrorMessage!));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SetTerminalStateAsync(db, job, JobStatus.Failed, "Service stopped before the job completed.");
            await SafeAddLogAsync(db, jobId, job.ErrorMessage!);
        }
        catch (ProvisioningCleanupException ex)
        {
            instanceId = ex.InstanceId;
            job.CloudProviderInstanceId = instanceId;
            logger.LogError(ex, "Provisioning cleanup failed for job {JobId}; teardown will be retried", jobId);

            await SetTerminalStateAsync(
                db, job, JobStatus.Failed, ex.Message);
            await SafeAddLogAsync(db, jobId, job.ErrorMessage!);
            await NotifyAsync(job, n => n.OnJobFailedAsync(job, job.ErrorMessage!));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing job {JobId}", jobId);
            await SetTerminalStateAsync(db, job, JobStatus.Failed, ex.Message);
            await SafeAddLogAsync(db, jobId, $"Internal error: {ex.Message}");
            await NotifyAsync(job, n => n.OnJobFailedAsync(job, job.ErrorMessage ?? ex.Message));
        }
        finally
        {
            try
            {
                if (ownsJob)
                    await CleanupJobAsync(db, job, provider, instanceId, rentedCores, provisioningTask);
            }
            finally
            {
                if (executionSlot)
                    semaphore.Release();
                if (ownsJob)
                {
                    _completions.TryRemove(jobId, out _);
                    _heartbeats.TryRemove(jobId, out _);
                    _jobCts.TryRemove(jobId, out _);
                    _cancelled.TryRemove(jobId, out _);
                }
            }
        }
    }

    private async Task CleanupJobAsync(
        AppDbContext db, BenchmarkJob job, ICloudProvider? provider, string? instanceId,
        int rentedCores, Task<ProvisionResult>? provisioningTask)
    {
        var release = false;
        try
        {
            if (provisioningTask is { IsCompleted: false })
            {
                // A timed-out SDK call can still create a VM later. Do not reconcile
                // by absence until it settles, or we could release quota before creation.
                _unfinishedProvisions[job.Id] = provisioningTask;
                ObserveLateOperation(provisioningTask, $"Late provisioning for job '{job.Id}'");
                throw new InvalidOperationException("Provisioning is still unwinding; cleanup will retry once it finishes.");
            }

            if (provisioningTask is not null && instanceId is null)
                instanceId = await GetProvisionedInstanceIdAsync(job.Id, provisioningTask);

            if (instanceId is not null)
            {
                job.CloudProviderInstanceId = instanceId;
                // Keep late-returned IDs durable even if teardown subsequently fails.
                await db.SaveChangesAsync(CancellationToken.None);
                await DeprovisionWithTimeoutAsync(
                    provider ?? throw new InvalidOperationException("Cloud provider is unavailable."),
                    instanceId, CancellationToken.None);
            }
            else if (provisioningTask is not null)
            {
                var confirmed = await TryDeprovisionByJobIdWithTimeoutAsync(
                    provider ?? throw new InvalidOperationException("Cloud provider is unavailable."),
                    job.Id.ToString(), CancellationToken.None);
                if (!confirmed)
                    throw new InvalidOperationException("The provider could not confirm cleanup by job ID.");
            }

            job.CloudProviderInstanceId = null;
            job.RentedCores = 0;
            job.LogsBlobUrl = await logUploadService.UploadJobLogsAsync(job.Id);
            await db.SaveChangesAsync(CancellationToken.None);
            release = true;
            if (provisioningTask is not null)
                await SafeAddLogAsync(db, job.Id, "Instance cleanup confirmed.");
        }
        catch (Exception ex)
        {
            job.CloudProviderInstanceId = instanceId;
            job.RentedCores = rentedCores;
            logger.LogError(ex, "[{JobId}] Cleanup unconfirmed; retaining {Cores} cores for retry", job.Id, rentedCores);
            await SafeAddLogAsync(db, job.Id,
                $"Cleanup unconfirmed; retaining {rentedCores} pool cores and retrying automatically. {ex.Message}");
        }
        finally
        {
            if (rentedCores > 0)
            {
                await _rentLifecycleGate.WaitAsync();
                try
                {
                    if (release)
                        corePool.Return(job.Platform, rentedCores);
                    else
                        _retainedRents[job.Id] = (job.Platform, rentedCores);
                    Interlocked.Decrement(ref _activeRents);
                }
                finally
                {
                    _rentLifecycleGate.Release();
                }
            }
        }
    }

    private async Task<string?> GetProvisionedInstanceIdAsync(Guid jobId, Task<ProvisionResult> task)
    {
        try
        {
            return (await task).InstanceId;
        }
        catch (ProvisioningCleanupException ex)
        {
            logger.LogWarning(ex, "[{JobId}] Provisioning left a resource requiring cleanup", jobId);
            return ex.InstanceId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{JobId}] Provisioning failed; cleanup must be confirmed by job ID", jobId);
            return null;
        }
    }

    private async Task SetPhaseAsync(AppDbContext db, BenchmarkJob job, JobStatus status, CancellationToken ct)
    {
        await _jobStateGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_cancelled.ContainsKey(job.Id))
                throw new OperationCanceledException("Cancelled by admin.", ct);
            job.Status = status;
            if (status == JobStatus.Provisioning)
                job.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _jobStateGate.Release();
        }
    }

    private async Task NotifyAsync(BenchmarkJob job, Func<INotificationService, Task> notify)
    {
        foreach (var notifier in notifiers)
        {
            Task? notification = null;
            try
            {
                notification = Task.Run(() => notify(notifier), CancellationToken.None);
                await notification.WaitAsync(_notificationTimeout);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{JobId}] {Notifier} notification failed", job.Id, notifier.GetType().Name);
                if (notification is not null)
                    ObserveLateOperation(notification, $"Notification for job '{job.Id}'");
            }
        }
    }

    private async Task ReconcileRetainedRentsLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_cleanupRetryInterval, ct);

                try
                {
                    await ReconcileRetainedRentsAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Retained-rent reconciliation failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileRetainedRentsAsync(CancellationToken ct)
    {
        await _reconciliationGate.WaitAsync(ct);
        try
        {
            await Parallel.ForEachAsync(
                _retainedRents.ToArray(),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(_maxConcurrentJobs, 4),
                    CancellationToken = ct,
                },
                async (entry, token) => await ReconcileRetainedRentAsync(entry.Key, entry.Value, token));
        }
        finally
        {
            _reconciliationGate.Release();
        }
    }

    private async Task ReconcileRetainedRentAsync(
        Guid jobId, (string Platform, int Cores) retained, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var job = await db.Jobs.FindAsync([jobId], ct);
            var provider = providerFactory.GetProvider(retained.Platform);

            if (_unfinishedProvisions.TryGetValue(jobId, out var provisioningTask))
            {
                if (!provisioningTask.IsCompleted)
                {
                    logger.LogWarning("[{JobId}] Provisioning has not stopped; keeping its cores reserved", jobId);
                    return;
                }

                var lateInstanceId = await GetProvisionedInstanceIdAsync(jobId, provisioningTask);
                if (job is null)
                    throw new InvalidOperationException($"Job {jobId} disappeared before late provisioning could be reconciled.");
                await _rentLifecycleGate.WaitAsync(ct);
                try
                {
                    if (lateInstanceId is not null)
                    {
                        job.CloudProviderInstanceId = lateInstanceId;
                        await db.SaveChangesAsync(ct);
                    }
                    _unfinishedProvisions.TryRemove(jobId, out _);
                }
                finally
                {
                    _rentLifecycleGate.Release();
                }
            }

            bool cleanupConfirmed;
            if (job?.CloudProviderInstanceId is { Length: > 0 } instanceId)
            {
                logger.LogWarning(
                    "[{JobId}] Retrying retained cleanup of {InstanceId}",
                    jobId, instanceId);
                await DeprovisionWithTimeoutAsync(provider, instanceId, ct);
                cleanupConfirmed = true;
            }
            else
            {
                logger.LogWarning(
                    "[{JobId}] Retrying retained cleanup by job ID",
                    jobId);
                cleanupConfirmed = await TryDeprovisionByJobIdWithTimeoutAsync(
                    provider, jobId.ToString(), ct);
            }

            if (!cleanupConfirmed)
            {
                logger.LogWarning(
                    "[{JobId}] Provider could not confirm retained cleanup",
                    jobId);
                await SafeAddLogAsync(db, jobId,
                    $"Provider could not confirm cleanup; keeping {retained.Cores} cores reserved.");
                return;
            }

            // Persist first. If the process dies before the in-memory return, the
            // rebuilt pool starts empty and the database no longer restores this rent.
            await _rentLifecycleGate.WaitAsync(ct);
            try
            {
                if (!_retainedRents.ContainsKey(jobId))
                    return;
                if (job is not null)
                {
                    job.CloudProviderInstanceId = null;
                    job.RentedCores = 0;
                    await AddLogAsync(db, jobId,
                        $"Retained instance cleanup confirmed; releasing {retained.Cores} pool cores.");
                }
                // An admin pool reset may have won while cloud cleanup was running.
                // Return only when this retry still owns the retained entry.
                if (_retainedRents.TryRemove(jobId, out var lease))
                {
                    corePool.Return(lease.Platform, lease.Cores);
                    logger.LogWarning(
                        "[{JobId}] Retained cleanup succeeded; returned {Cores} cores",
                        jobId, lease.Cores);
                }
            }
            finally
            {
                _rentLifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[{JobId}] Retained cleanup retry failed; keeping {Cores} cores reserved",
                jobId, retained.Cores);
            await SafeAddLogAsync(db, jobId,
                $"Cleanup retry failed; keeping {retained.Cores} cores reserved. {ex.Message.Split('\n')[0].Trim()}");
        }
    }

    private Task DeprovisionWithTimeoutAsync(
        ICloudProvider provider,
        string instanceId,
        CancellationToken ct) =>
        RunCleanupWithTimeoutAsync(
            token => provider.DeprovisionAsync(instanceId, token),
            $"{provider.Name} cleanup of '{instanceId}'",
            ct);

    private Task<bool> TryDeprovisionByJobIdWithTimeoutAsync(
        ICloudProvider provider,
        string jobId,
        CancellationToken ct) =>
        RunCleanupWithTimeoutAsync(
            token => provider.TryDeprovisionByJobIdAsync(jobId, token),
            $"{provider.Name} cleanup for job '{jobId}'",
            ct);

    private async Task RunCleanupWithTimeoutAsync(
        Func<CancellationToken, Task> cleanup,
        string operation,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_cleanupTimeout);
        var cleanupToken = timeoutCts.Token;
        var cleanupTask = Task.Run(() => cleanup(cleanupToken), CancellationToken.None);

        try
        {
            await cleanupTask.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            ObserveLateOperation(cleanupTask, operation);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{operation} did not finish within {_cleanupTimeout.TotalMinutes:F1} minutes.",
                ex);
        }
    }

    private async Task<T> RunCleanupWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> cleanup,
        string operation,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_cleanupTimeout);
        var cleanupToken = timeoutCts.Token;
        var cleanupTask = Task.Run(() => cleanup(cleanupToken), CancellationToken.None);

        try
        {
            return await cleanupTask.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            ObserveLateOperation(cleanupTask, operation);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{operation} did not finish within {_cleanupTimeout.TotalMinutes:F1} minutes.",
                ex);
        }
    }

    private void ObserveLateOperation(Task cleanupTask, string operation)
    {
        _ = cleanupTask.ContinueWith(
            completed => logger.LogError(
                completed.Exception,
                "{Operation} faulted",
                operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task SetTerminalStateAsync(
        AppDbContext db,
        BenchmarkJob job,
        JobStatus proposedStatus,
        string? proposedError,
        string? resultMarkdown = null)
    {
        var gate = _jobStateGate;
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
            job.LogsBlobUrl = await logUploadService.UploadJobLogsAsync(job.Id);
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
        await _jobStateGate.WaitAsync(ct);
        try
        {
            await RecoverJobLeasesAsync(ct);
        }
        finally
        {
            _jobStateGate.Release();
        }
    }

    private async Task RecoverJobLeasesAsync(CancellationToken ct)
    {
        await _rentLifecycleGate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var staleJobs = await db.Jobs
                .Where(j => ((j.Status == JobStatus.Pending
                         || j.Status == JobStatus.Provisioning
                         || j.Status == JobStatus.Running) && j.CreatedAt < _startupAt)
                         || j.RentedCores > 0)
                .OrderBy(j => j.CreatedAt)
                .ToListAsync(ct);

            foreach (var job in staleJobs)
            {
                if (job.Status == JobStatus.Pending && job.CloudProviderInstanceId is null)
                {
                    // Provisioning cannot start before the phase change is durable.
                    // Any Pending reservation was only waiting for an execution slot.
                    job.RentedCores = 0;
                }

                var wasActive = IsActive(job.Status);
                var retainedCores = job.RentedCores;
                logger.LogWarning(
                    "Recovering job {JobId} (status={Status}, instance={InstanceId}, retained={Cores})",
                    job.Id, job.Status, job.CloudProviderInstanceId, job.RentedCores);
                if (wasActive)
                {
                    job.Status = JobStatus.Cancelled;
                    job.ErrorMessage = "Cancelled on service startup; previous jobs are not resumed.";
                    job.CompletedAt = DateTime.UtcNow;
                }

                // Restore accounting before accepting work, but let the bounded retry
                // loop do cloud I/O so a dead provider cannot stall service startup.
                // Terminal legacy rows can retain instance IDs after successful cleanup;
                // only their persisted nonzero lease is evidence that cleanup is pending.
                if (job.CloudProviderInstanceId is not null || retainedCores > 0)
                    RetainRecoveredRent(job, retainedCores);
            }

            if (staleJobs.Count > 0)
                await db.SaveChangesAsync(ct);
        }
        finally
        {
            _rentLifecycleGate.Release();
        }
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
