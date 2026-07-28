using EgorBot.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EgorBot.Server.Tests;

/// <summary>
/// Unit tests for the core quota accounting. A leaked rent wedges every later job
/// on "Waiting for N cores from pool" even when no VM exists, so the accounting
/// must stay exact under cancellation and mismatched return amounts.
/// </summary>
public class CorePoolManagerTests
{
    // 32 total cores in the catalog
    private const string Platform = "ubuntu24_aws_graviton4";

    private static CorePoolManager NewPool() => new(NullLogger<CorePoolManager>.Instance);

    private static (int Used, int Total, int Waiters) Snapshot(CorePoolManager pool) =>
        pool.GetSnapshot().Single().Value;

    [Fact]
    public async Task RentAndReturn_KeepsAccountingExact()
    {
        var pool = NewPool();

        await pool.RentAsync(Platform, 16);
        Assert.Equal(16, Snapshot(pool).Used);

        pool.Return(Platform, 16);
        Assert.Equal(0, Snapshot(pool).Used);
    }

    [Fact]
    public async Task Rent_MoreThanCapacity_FailsFastInsteadOfHanging()
    {
        var pool = NewPool();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.RentAsync(Platform, 64));
        Assert.Contains("only has 32", ex.Message);
    }

    [Fact]
    public async Task AzurePool_CapacityIsIndependentOfWhichTargetRunsFirst()
    {
        // ubuntu24_azure_cobalt100 (20) and windows_azure_cobalt100 (60) share the
        // "Standard_D{0}pds_v6" family, so the pool size must not depend on ordering.
        var linuxFirst = NewPool();
        await linuxFirst.RentAsync("ubuntu24_azure_cobalt100", 1);
        var linuxTotal = linuxFirst.GetPoolState("windows_azure_cobalt100").Total;

        var windowsFirst = NewPool();
        await windowsFirst.RentAsync("windows_azure_cobalt100", 1);
        var windowsTotal = windowsFirst.GetPoolState("ubuntu24_azure_cobalt100").Total;

        Assert.Equal(linuxTotal, windowsTotal);
    }

    [Fact]
    public async Task Rent_AboveAzurePoolCapacity_FailsFast()
    {
        // "cores 32" on an Azure D-series target can never be satisfied (20-core pool)
        var pool = NewPool();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.RentAsync("ubuntu24_azure_cobalt100", 32));
        Assert.Contains("Lower the default core count", ex.Message);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotConsumeCores()
    {
        var pool = NewPool();
        await pool.RentAsync(Platform, 32); // pool is now full

        using var cts = new CancellationTokenSource();
        var waiter = pool.RentAsync(Platform, 32, cts.Token);
        Assert.Equal(1, Snapshot(pool).Waiters);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        // The dead waiter must be gone immediately, not left blocking the queue
        Assert.Equal(0, Snapshot(pool).Waiters);

        pool.Return(Platform, 32);

        // The cancelled waiter must not have been charged for the cores it never got
        Assert.Equal(0, Snapshot(pool).Used);
    }

    [Fact]
    public async Task Return_WakesQueuedWaiter()
    {
        var pool = NewPool();
        await pool.RentAsync(Platform, 32);

        var waiter = pool.RentAsync(Platform, 32);
        Assert.False(waiter.IsCompleted);

        pool.Return(Platform, 32);
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(32, Snapshot(pool).Used);
    }

    [Fact]
    public async Task ResetAll_RecoversLeakedCores()
    {
        var pool = NewPool();
        await pool.RentAsync(Platform, 32);

        // Simulate a leak: the job never returned its cores
        var leaked = pool.ResetAll();

        Assert.Equal(32, leaked);
        Assert.Equal(0, Snapshot(pool).Used);

        // The pool is usable again
        await pool.RentAsync(Platform, 32).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Return_UnknownPlatform_DoesNotThrow()
    {
        var pool = NewPool();

        // Called from cleanup paths — throwing here would skip the rest of the cleanup
        pool.Return("no_such_target", 8);
    }
}
