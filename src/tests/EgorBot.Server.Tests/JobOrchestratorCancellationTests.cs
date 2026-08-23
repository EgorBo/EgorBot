using System.Collections.Concurrent;
using EgorBot.Server.Data;
using EgorBot.Server.Models;
using EgorBot.Server.Services;
using EgorBot.Server.Services.CloudInit;
using EgorBot.Server.Services.CloudProviders;
using EgorBot.Server.Services.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EgorBot.Server.Tests;

public sealed class JobOrchestratorCancellationTests
{
    private const string Platform = "ubuntu24_docker_x64";

    [Fact]
    public async Task CancelJob_CancelsOnlyRequestedJob_AndReturnsItsCores()
    {
        var connectionString =
            $"Data Source=cancel-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        await using var serviceProvider = services.BuildServiceProvider();

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EgorBot:ServiceBaseUrl"] = "https://example.test",
                ["EgorBot:DefaultCores"] = "8",
                ["EgorBot:MaxConcurrentJobs"] = "2",
            })
            .Build();

        var provider = new ControlledCloudProvider();
        var corePool = new CorePoolManager(NullLogger<CorePoolManager>.Instance);
        corePool.SetCapacity(Platform, 8, "test");

        var orchestrator = new JobOrchestrator(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new CloudProviderFactory([provider]),
            new CloudInitBuilder(config),
            new LogUploadService(config, NullLogger<LogUploadService>.Instance),
            corePool,
            Array.Empty<INotificationService>(),
            new RuntimeSettings(config),
            config,
            NullLogger<JobOrchestrator>.Instance);

        var first = new BenchmarkJob { Platform = Platform };
        var second = new BenchmarkJob { Platform = Platform };
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Jobs.AddRange(first, second);
            await db.SaveChangesAsync();
        }

        await orchestrator.StartAsync(CancellationToken.None);
        try
        {
            orchestrator.Enqueue(first.Id);
            await provider.WaitUntilProvisioningAsync(first.Id);
            await WaitUntilAsync(() =>
            {
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return db.Jobs.AsNoTracking().Single(j => j.Id == first.Id).Status == JobStatus.Running;
            });

            orchestrator.Enqueue(second.Id);
            await WaitUntilAsync(() => corePool.GetPoolState(Platform).Waiters == 1);

            provider.BlockDeprovision(first.Id);
            Assert.True(await orchestrator.CancelJobAsync(first.Id));
            await provider.WaitUntilDeprovisioningAsync(first.Id);

            // Cancellation must not release the lease while the VM still exists.
            Assert.False(provider.HasStartedProvisioning(second.Id));
            var blockedPool = corePool.GetPoolState(Platform);
            Assert.Equal(8, blockedPool.Used);
            Assert.Equal(1, blockedPool.Waiters);

            provider.CompleteDeprovision(first.Id);

            // Once cloud deletion completes, cleanup returns the rent and the queue advances.
            await provider.WaitUntilProvisioningAsync(second.Id);
            var pool = corePool.GetPoolState(Platform);
            Assert.Equal(8, pool.Used);
            Assert.Equal(0, pool.Waiters);

            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(JobStatus.Cancelled, (await db.Jobs.FindAsync(first.Id))!.Status);
            Assert.NotEqual(JobStatus.Cancelled, (await db.Jobs.FindAsync(second.Id))!.Status);
        }
        finally
        {
            await orchestrator.CancelJobAsync(second.Id);
            await WaitUntilAsync(() => corePool.GetPoolState(Platform).Used == 0);
            await orchestrator.StopAsync(CancellationToken.None);
            orchestrator.Dispose();
            corePool.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("Condition was not met.");
            await Task.Delay(20);
        }
    }

    private sealed class ControlledCloudProvider : ICloudProvider
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _provisioning = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _deprovisioning = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _deprovisionGates = new();

        public string Name => "Docker";

        public Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
        {
            var jobId = Guid.Parse(request.JobId);
            _provisioning.GetOrAdd(
                jobId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

            return Task.FromResult(new ProvisionResult(jobId.ToString()));
        }

        public async Task DeprovisionAsync(string instanceId, CancellationToken ct = default)
        {
            var jobId = Guid.Parse(instanceId);
            _deprovisioning.GetOrAdd(
                jobId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

            if (_deprovisionGates.TryGetValue(jobId, out var gate))
                await gate.Task.WaitAsync(ct);
        }

        public bool HasStartedProvisioning(Guid jobId) =>
            _provisioning.TryGetValue(jobId, out var started) && started.Task.IsCompleted;

        public void BlockDeprovision(Guid jobId) =>
            _deprovisionGates[jobId] =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteDeprovision(Guid jobId) =>
            _deprovisionGates[jobId].TrySetResult();

        public async Task WaitUntilProvisioningAsync(Guid jobId)
        {
            var started = _provisioning.GetOrAdd(
                jobId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task WaitUntilDeprovisioningAsync(Guid jobId)
        {
            var started = _deprovisioning.GetOrAdd(
                jobId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
