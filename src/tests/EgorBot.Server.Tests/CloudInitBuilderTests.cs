using EgorBot.Server.Models;
using EgorBot.Server.Services.CloudInit;
using EgorBot.Shared;
using Microsoft.Extensions.Configuration;

namespace EgorBot.Server.Tests;

public sealed class CloudInitBuilderTests
{
    [Fact]
    public void Orchard_CanEnablePerfAndGcProfilersTogether()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EgorBot:ServiceBaseUrl"] = "https://bot.example.test",
            })
            .Build();
        var builder = new CloudInitBuilder(configuration);
        var job = new BenchmarkJob
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Platform = "macos15_helix_arm64",
            Kind = BenchmarkKind.Orchard,
            CommitsAndPrs = "main;PR_123",
            UseProfiler = true,
            UseGcProfiler = true,
        };

        var script = builder.Build(job);

        Assert.Contains("--benchmark_kind orchard", script);
        Assert.Contains("--perf_enabled 1", script);
        Assert.Contains("--gc_profiler 1", script);
    }
}
