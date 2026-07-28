using EgorBot.Shared;

namespace EgorBot.Server.Services;

/// <summary>
/// Manages per-instance-type core quotas.
///
/// Each target in <see cref="TargetCatalog"/> has a <c>TotalCores</c> cap. Different
/// targets that share the same underlying instance family (InstanceName) share the same
/// pool.  For example, <c>ubuntu24_azure_turin</c> and <c>windows_azure_turin</c> both
/// use <c>Standard_D{0}ads_v7</c>, so they draw from the same 20-core budget.
///
/// Usage:
///   <code>
///   await pool.RentAsync("ubuntu24_azure_turin", 8, cancellationToken);
///   // … VM is running …
///   pool.Return("ubuntu24_azure_turin", 8);
///   </code>
///
/// <see cref="RentAsync"/> blocks (asynchronously) until the requested cores are available.
/// <see cref="Return"/> releases cores and wakes any blocked renters.
/// </summary>
public sealed class CorePoolManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PoolEntry> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CorePoolManager> _logger;
    private bool _disposed;

    /// <summary>Internal state for one pool (one instance family).</summary>
    private sealed class PoolEntry
    {
        public required int TotalCores { get; init; }
        public int UsedCores { get; set; }
        public int AvailableCores => TotalCores - UsedCores;

        /// <summary>
        /// Waiters are served FIFO.  Each waiter is a (requestedCores, TCS) pair.
        /// Using a linked list for efficient removal.
        /// </summary>
        public LinkedList<(int Cores, TaskCompletionSource<bool> Tcs)> Waiters { get; } = new();
    }

    public CorePoolManager(ILogger<CorePoolManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolve the pool key for a given target platform.
    /// Targets that share the same InstanceName share the same pool.
    /// Fallback: use the target name itself (e.g. Helix / Docker with no InstanceName).
    /// </summary>
    private static string GetPoolKey(TargetInfo target)
    {
        // For cloud providers that share instance families across OS variants
        // (e.g. ubuntu24_azure_turin and windows_azure_turin both use "Standard_D{0}ads_v7"),
        // group them under the same pool key.
        return target.InstanceName ?? target.Name;
    }

    private PoolEntry GetOrCreatePool(TargetInfo target)
    {
        var key = GetPoolKey(target);
        if (!_pools.TryGetValue(key, out var entry))
        {
            entry = new PoolEntry { TotalCores = target.TotalCores };
            _pools[key] = entry;
            _logger.LogInformation("CorePool: created pool '{Key}' with {Total} total cores",
                key, target.TotalCores);
        }
        return entry;
    }

    /// <summary>
    /// Rent <paramref name="cores"/> from the pool for <paramref name="platform"/>.
    /// Blocks asynchronously until enough cores are available (FIFO ordering).
    /// </summary>
    public async Task RentAsync(string platform, int cores, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = TargetCatalog.GetTarget(platform);
        TaskCompletionSource<bool> tcs;
        PoolEntry pool;
        LinkedListNode<(int Cores, TaskCompletionSource<bool> Tcs)> node;

        lock (_lock)
        {
            pool = GetOrCreatePool(target);

            // A request bigger than the whole pool can never be satisfied — waiting for it
            // would wedge this job (and every job queued behind it) forever.
            if (cores > pool.TotalCores)
            {
                throw new InvalidOperationException(
                    $"Requested {cores} cores for '{platform}', but pool '{GetPoolKey(target)}' " +
                    $"only has {pool.TotalCores}. Lower the default core count (e.g. `cores {pool.TotalCores}`).");
            }

            if (pool.Waiters.Count == 0 && pool.AvailableCores >= cores)
            {
                // Fast path: enough cores free and nobody else waiting
                pool.UsedCores += cores;
                _logger.LogInformation(
                    "CorePool: rented {Cores} cores for '{Platform}' (pool '{Key}'). Used={Used}/{Total}",
                    cores, platform, GetPoolKey(target), pool.UsedCores, pool.TotalCores);
                return;
            }

            // Slow path: must wait
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = pool.Waiters.AddLast((cores, tcs));
            _logger.LogInformation(
                "CorePool: queuing {Cores} cores for '{Platform}' (pool '{Key}'). Used={Used}/{Total}, waiters={Waiters}",
                cores, platform, GetPoolKey(target), pool.UsedCores, pool.TotalCores, pool.Waiters.Count);
        }

        // Wait outside the lock
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            await tcs.Task;
        }
        catch
        {
            // Drop the dead waiter immediately instead of leaving it queued
            // in front of everyone else until the next Return().
            lock (_lock)
            {
                if (node.List is not null)
                    pool.Waiters.Remove(node);
            }
            throw;
        }

        _logger.LogInformation(
            "CorePool: rented {Cores} cores for '{Platform}' (after wait)",
            cores, platform);
    }

    /// <summary>
    /// Return <paramref name="cores"/> to the pool for <paramref name="platform"/>.
    /// Wakes queued waiters (FIFO) if enough cores are now available.
    /// Never throws — returning cores happens on cleanup paths where an exception
    /// would leak the whole rent (and wedge every subsequent job).
    /// </summary>
    public void Return(string platform, int cores)
    {
        if (cores <= 0)
            return;

        TargetInfo target;
        try
        {
            target = TargetCatalog.GetTarget(platform);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CorePool: Return called with unknown platform '{Platform}' — cores leaked", platform);
            return;
        }

        lock (_lock)
        {
            var key = GetPoolKey(target);
            if (!_pools.TryGetValue(key, out var pool))
            {
                _logger.LogWarning("CorePool: Return called for unknown pool '{Key}' — ignoring", key);
                return;
            }

            pool.UsedCores = Math.Max(0, pool.UsedCores - cores);
            _logger.LogInformation(
                "CorePool: returned {Cores} cores for '{Platform}' (pool '{Key}'). Used={Used}/{Total}",
                cores, platform, key, pool.UsedCores, pool.TotalCores);

            // Wake FIFO waiters while we have capacity
            DrainWaiters(pool);
        }
    }

    /// <summary>
    /// Service waiters in FIFO order, granting cores to each waiter
    /// whose request fits in the remaining capacity.
    /// </summary>
    private void DrainWaiters(PoolEntry pool)
    {
        // Must be called under _lock.
        var node = pool.Waiters.First;
        while (node is not null)
        {
            var next = node.Next;
            var (requested, tcs) = node.Value;

            if (tcs.Task.IsCompleted)
            {
                // Waiter was cancelled (or already served) — discard it
                pool.Waiters.Remove(node);
                node = next;
                continue;
            }

            if (pool.AvailableCores >= requested)
            {
                pool.Waiters.Remove(node);

                // Only charge the pool if the waiter actually observes the grant.
                // TrySetResult loses the race against a concurrent cancellation, and
                // charging a waiter that never wakes up leaks the cores forever.
                if (tcs.TrySetResult(true))
                    pool.UsedCores += requested;

                // Continue — the next waiter might also fit
            }
            else
            {
                // FIFO: don't skip ahead (prevents starvation of large requests)
                break;
            }

            node = next;
        }
    }

    /// <summary>
    /// Get a snapshot of all pools for diagnostics / admin endpoints.
    /// </summary>
    public Dictionary<string, (int Used, int Total, int Waiters)> GetSnapshot()
    {
        lock (_lock)
        {
            var result = new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, pool) in _pools)
            {
                result[key] = (pool.UsedCores, pool.TotalCores, pool.Waiters.Count);
            }
            return result;
        }
    }

    /// <summary>
    /// Forget all accounting and wake every waiter. Only safe to call when no jobs
    /// are running (e.g. right after cancelling everything) — it exists so a leaked
    /// rent can be recovered without restarting the service.
    /// Returns the number of cores that were considered "in use".
    /// </summary>
    public int ResetAll()
    {
        lock (_lock)
        {
            var leaked = 0;
            foreach (var (key, pool) in _pools)
            {
                leaked += pool.UsedCores;
                _logger.LogWarning("CorePool: reset pool '{Key}' (was Used={Used}/{Total}, waiters={Waiters})",
                    key, pool.UsedCores, pool.TotalCores, pool.Waiters.Count);
                pool.UsedCores = 0;
                DrainWaiters(pool);
            }
            return leaked;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var (_, pool) in _pools)
            {
                foreach (var (_, tcs) in pool.Waiters)
                {
                    tcs.TrySetCanceled();
                }
                pool.Waiters.Clear();
            }
        }
    }
}
