using System.Net;
using System.Text;
using EgorBot.Github.Models;
using EgorBot.Github.Services;
using EgorBot.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EgorBot.Server.Tests;

public sealed class EgorBotClientTests
{
    [Fact]
    public async Task StartJobAsync_ReturnsStructuredRateLimitResponse()
    {
        const string responseJson = """
            {
              "code": "job_limit_reached",
              "error": "@jkotas has used 16 of 16 jobs.",
              "user": "jkotas",
              "limit": 16,
              "used": 16,
              "requested": 2,
              "windowHours": 24,
              "retryAt": "2026-08-22T10:15:00Z"
            }
            """;

        using var http = new HttpClient(new StubHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            }))
        {
            BaseAddress = new Uri("https://example.test"),
        };
        var configuration = new ConfigurationBuilder().Build();
        var client = new EgorBotClient(http, configuration, NullLogger<EgorBotClient>.Instance);

        var result = await client.StartJobAsync(
            new BotCommand
            {
                Targets = ["ubuntu24_azure_cobalt100", "ubuntu24_aws_graviton4"],
                CommitsAndPrs = "main",
            },
            requestedBy: "jkotas",
            sourceUrl: "https://github.com/dotnet/runtime/issues/1");

        Assert.Null(result.Response);
        var rateLimit = Assert.IsType<JobRateLimitResponse>(result.RateLimit);
        Assert.Equal("jkotas", rateLimit.User);
        Assert.Equal(16, rateLimit.Limit);
        Assert.Equal(16, rateLimit.Used);
        Assert.Equal(2, rateLimit.Requested);
        Assert.Equal(24, rateLimit.WindowHours);
        Assert.Equal(
            new DateTime(2026, 8, 22, 10, 15, 0, DateTimeKind.Utc),
            rateLimit.RetryAt);
    }

    [Fact]
    public void FormatRateLimitComment_RendersPlainGitHubCompatibleUtcTime()
    {
        var comment = JobTrackerService.FormatRateLimitComment(
            new MentionSource
            {
                Owner = "dotnet",
                Repo = "runtime",
                Number = 1,
                IsPullRequest = true,
                Author = "jkotas",
                HtmlUrl = "https://github.com/dotnet/runtime/pull/1",
            },
            new JobRateLimitResponse
            {
                User = "jkotas",
                Used = 16,
                Limit = 16,
                Requested = 1,
                WindowHours = 24,
                RetryAt = new DateTime(2026, 8, 22, 10, 15, 0, DateTimeKind.Utc),
            });

        Assert.Contains("2026-08-22 10:15 UTC", comment);
        Assert.DoesNotContain("<t:", comment);
        Assert.Contains("no jobs were started", comment);
    }

    [Fact]
    public async Task StartJobAsync_SendsGcProfilerIndependently()
    {
        var handler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"groupId":"11111111-1111-1111-1111-111111111111","jobs":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        var client = new EgorBotClient(
            http,
            new ConfigurationBuilder().Build(),
            NullLogger<EgorBotClient>.Instance);

        var result = await client.StartJobAsync(
            new BotCommand
            {
                Targets = ["macos15_helix_arm64"],
                Kind = BenchmarkKind.Orchard,
                CommitsAndPrs = "main",
                UseGcProfiler = true,
                UseProfiler = false,
            },
            requestedBy: "jkotas",
            sourceUrl: null);

        Assert.NotNull(result.Response);
        Assert.Contains("\"useGcProfiler\":true", handler.RequestBody);
        Assert.Contains("\"useProfiler\":false", handler.RequestBody);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
