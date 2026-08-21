using EgorBot.Server.Data;
using EgorBot.Server.Models;
using EgorBot.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EgorBot.Server.Tests;

public sealed class JobRateLimitServiceTests
{
    [Fact]
    public async Task MultiJobRequest_IsRejectedAtomically_WhenItWouldExceedLimit()
    {
        await using var store = await TestStore.CreateAsync(globalLimit: 16);

        var first = await store.Limiter.TryAdmitAsync("jkotas", CreateJobs(15));
        var rejected = await store.Limiter.TryAdmitAsync("jkotas", CreateJobs(2));

        Assert.True(first.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal(15, rejected.Used);
        Assert.Equal(16, rejected.Limit);
        Assert.Equal(2, rejected.Requested);
        Assert.Equal(store.Clock.GetUtcNow().UtcDateTime + JobRateLimitService.Window, rejected.RetryAtUtc);

        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(15, await db.Jobs.CountAsync());
        Assert.Equal(15, await db.JobAdmissions.CountAsync());
    }

    [Fact]
    public async Task ExpiredAdmissions_LeaveTheRollingWindow()
    {
        await using var store = await TestStore.CreateAsync(globalLimit: 2);

        Assert.True((await store.Limiter.TryAdmitAsync("jkotas", CreateJobs(2))).Accepted);
        store.Clock.Advance(TimeSpan.FromHours(23));
        Assert.False((await store.Limiter.TryAdmitAsync("jkotas", CreateJobs(1))).Accepted);

        store.Clock.Advance(TimeSpan.FromHours(1));
        var admitted = await store.Limiter.TryAdmitAsync("jkotas", CreateJobs(1));

        Assert.True(admitted.Accepted);
        Assert.Equal(0, admitted.Used);
    }

    [Fact]
    public async Task UserOverride_IsPersistentAndCaseInsensitive()
    {
        await using var store = await TestStore.CreateAsync(globalLimit: 16);

        await store.Limiter.SetUserLimitAsync("@JKotas", 32);
        Assert.True((await store.Limiter.TryAdmitAsync("jKoTaS", CreateJobs(32))).Accepted);

        var rejected = await store.Limiter.TryAdmitAsync("JKOTAS", CreateJobs(1));
        Assert.False(rejected.Accepted);
        Assert.Equal(32, rejected.Limit);

        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var savedOverride = await db.UserJobLimits.SingleAsync();
        Assert.Equal("jkotas", savedOverride.UserKey);
        Assert.Equal(32, savedOverride.MaxJobs);
    }

    [Fact]
    public async Task DifferentUsers_HaveIndependentWindows()
    {
        await using var store = await TestStore.CreateAsync(globalLimit: 1);

        Assert.True((await store.Limiter.TryAdmitAsync("jkotas", CreateJobs(1))).Accepted);
        Assert.False((await store.Limiter.TryAdmitAsync("jkotas", CreateJobs(1))).Accepted);
        Assert.True((await store.Limiter.TryAdmitAsync("EgorBo", CreateJobs(1))).Accepted);
    }

    [Fact]
    public async Task ConcurrentRequests_CannotExceedLimit()
    {
        await using var store = await TestStore.CreateAsync(globalLimit: 16);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => store.Limiter.TryAdmitAsync("jkotas", CreateJobs(1))));

        Assert.Equal(16, results.Count(r => r.Accepted));
        Assert.Equal(16, results.Count(r => !r.Accepted));

        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(16, await db.JobAdmissions.CountAsync());
    }

    [Fact]
    public async Task Initializer_MigratesExistingDatabaseAndBackfillsAdmissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var existingJob = new BenchmarkJob
        {
            RequestedBy = "@JKotas",
            CreatedAt = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
        };
        db.Jobs.Add(existingJob);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("""DROP TABLE "JobAdmissions";""");
        await db.Database.ExecuteSqlRawAsync("""DROP TABLE "UserJobLimits";""");
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Jobs" DROP COLUMN "UseGcProfiler";""");

        await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);

        var admission = await db.JobAdmissions.AsNoTracking().SingleAsync();
        Assert.Equal(existingJob.Id, admission.JobId);
        Assert.Equal("jkotas", admission.UserKey);
        Assert.Equal(existingJob.CreatedAt, admission.AdmittedAt);
        Assert.False(await db.UserJobLimits.AnyAsync());
        Assert.False((await db.Jobs.AsNoTracking().SingleAsync()).UseGcProfiler);
    }

    private static List<BenchmarkJob> CreateJobs(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => new BenchmarkJob { Platform = "ubuntu24_docker_x64" })
            .ToList();

    private sealed class TestStore : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestStore(
            SqliteConnection connection,
            ServiceProvider services,
            ManualTimeProvider clock)
        {
            _connection = connection;
            Services = services;
            Clock = clock;
            Limiter = services.GetRequiredService<JobRateLimitService>();
        }

        public ServiceProvider Services { get; }
        public ManualTimeProvider Clock { get; }
        public JobRateLimitService Limiter { get; }

        public static async Task<TestStore> CreateAsync(int globalLimit)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var clock = new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EgorBot:MaxJobsPerUser24Hours"] = globalLimit.ToString(),
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<TimeProvider>(clock);
            services.AddLogging();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<JobRateLimitService>();
            var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);
            }

            return new TestStore(connection, serviceProvider, clock);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    public sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
