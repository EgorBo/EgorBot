using EgorBot.Data;
using EgorBot.GitHub;
using EgorBot.Services;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Services.GitHub;

/// <summary>
/// Background service that polls GitHub for new @EgorBot mentions and dispatches jobs.
/// </summary>
public class GitHubMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitHubMonitorService> _logger;
    private readonly IConfiguration _config;
    private DateTimeOffset _lastChecked = DateTimeOffset.UtcNow;

    public GitHubMonitorService(IServiceScopeFactory scopeFactory, ILogger<GitHubMonitorService> logger, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(_config.GetValue("GitHub:PollIntervalSeconds", 30));
        var owner = _config["GitHub:Owner"] ?? "dotnet";
        var repo = _config["GitHub:Repo"] ?? "runtime";

        _logger.LogInformation("GitHub monitor started. Polling {Owner}/{Repo} every {Interval}s", owner, repo, pollInterval.TotalSeconds);

        // Small delay to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollForCommandsAsync(owner, repo, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling GitHub for commands");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    private async Task PollForCommandsAsync(string owner, string repo, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var github = scope.ServiceProvider.GetRequiredService<GitHubService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<JobOrchestrator>();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var since = _lastChecked;
        _lastChecked = DateTimeOffset.UtcNow;

        var comments = await github.GetRecentCommentsAsync(owner, repo, since, ct);
        if (comments.Count == 0)
            return;

        _logger.LogDebug("Fetched {Count} comments since {Since}", comments.Count, since);

        foreach (var comment in comments)
        {
            if (ct.IsCancellationRequested) break;

            // Skip comments from the bot itself
            if (comment.User.Login.Equals(github.BotLogin, StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip already-processed comments
            var alreadyProcessed = await db.Jobs.AnyAsync(j => j.GitHubCommentId == comment.Id, ct);
            if (alreadyProcessed)
                continue;

            // Extract issue/PR number from the comment URL: .../issues/NNNN#... or .../pull/NNNN#...
            var issueNumber = ExtractIssueNumber(comment.HtmlUrl);
            if (issueNumber is null)
                continue;

            var isPr = await github.IsPullRequestAsync(owner, repo, issueNumber.Value, ct);

            var command = CommandParser.TryParse(
                comment.Body, comment.User.Login, comment.Id,
                issueNumber.Value, isPr, owner, repo);

            if (command is null)
                continue;

            _logger.LogInformation("Found command from @{User} on {Owner}/{Repo}#{Number}: {Command}",
                command.Requester, owner, repo, issueNumber, command.Platforms.Count + " platform(s)");

            // Acknowledge with a rocket reaction
            await github.AddReactionAsync(owner, repo, comment.Id, Octokit.ReactionType.Rocket, ct);

            // Dispatch the job
            await orchestrator.CreateAndDispatchJobAsync(command, ct);
        }
    }

    private static int? ExtractIssueNumber(string url)
    {
        // URL format: https://github.com/{owner}/{repo}/issues/{number}#...
        // or: https://github.com/{owner}/{repo}/pull/{number}#...
        var segments = new Uri(url).AbsolutePath.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if ((segments[i] == "issues" || segments[i] == "pull") && int.TryParse(segments[i + 1], out var num))
                return num;
        }
        return null;
    }
}
