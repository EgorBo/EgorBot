using System.Collections.Concurrent;
using EgorBot.Github.Models;
using Octokit;

namespace EgorBot.Github.Services;

/// <summary>
/// Background service that polls GitHub repositories for @EgorBt mentions
/// in issue/PR comments and descriptions every 30 seconds.
///
/// Monitors:
///   - dotnet/runtime
///   - EgorBot/runtime-utils (configurable)
///
/// Once a mention is detected and processed, its unique key is stored so it
/// is never processed again — even if the comment/body is edited later.
/// </summary>
public sealed class GitHubPollingService(
    IConfiguration config,
    ILogger<GitHubPollingService> logger,
    IServiceProvider services) : BackgroundService
{
    /// <summary>
    /// Unique keys of entities we've already seen.
    /// Format: "{owner}/{repo}/issue/{number}" for body mentions,
    ///         "{owner}/{repo}/comment/{commentId}" for comment mentions.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _processed = new();

    private record RepoConfig(string Owner, string Name);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait a moment for the app to finish starting up
        await Task.Delay(2000, ct);

        var repos = GetRepoConfigs();
        var client = CreateGitHubClient();
        var pollInterval = TimeSpan.FromSeconds(config.GetValue("Github:PollIntervalSeconds", 30));

        logger.LogInformation("GitHub polling started. Repos: [{Repos}], interval: {Interval}s",
            string.Join(", ", repos.Select(r => $"{r.Owner}/{r.Name}")), pollInterval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var since = DateTimeOffset.UtcNow - pollInterval - TimeSpan.FromSeconds(5); // small overlap

                foreach (var repo in repos)
                {
                    await PollIssueCommentsAsync(client, repo, since, ct);
                    await PollIssuesAndPrsAsync(client, repo, since, ct);
                }
            }
            catch (RateLimitExceededException ex)
            {
                logger.LogWarning("GitHub rate limit exceeded. Resets at {Reset}. Waiting...", ex.Reset);
                var waitTime = ex.Reset - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                if (waitTime > TimeSpan.Zero)
                    await Task.Delay(waitTime, ct);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during GitHub polling cycle");
            }

            await Task.Delay(pollInterval, ct);
        }
    }

    // ── Poll issue/PR comments ──────────────────────────────────────────

    private async Task PollIssueCommentsAsync(GitHubClient client, RepoConfig repo, DateTimeOffset since, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var request = new IssueCommentRequest
        {
            Since = since,
            Sort = IssueCommentSort.Updated,
            Direction = SortDirection.Descending,
        };

        var comments = await client.Issue.Comment.GetAllForRepository(repo.Owner, repo.Name, request,
            new ApiOptions { PageSize = 100, PageCount = 1 });

        foreach (var comment in comments)
        {
            ct.ThrowIfCancellationRequested();

            var key = $"{repo.Owner}/{repo.Name}/comment/{comment.Id}";
            if (_processed.ContainsKey(key)) continue;

            if (!CommandParser.ContainsMention(comment.Body)) continue;

            // Mark as processed BEFORE handling (to avoid re-processing on edits)
            _processed[key] = true;

            logger.LogInformation("Detected @EgorBt mention in comment {CommentId} on {Owner}/{Repo}#{IssueUrl}",
                comment.Id, repo.Owner, repo.Name, comment.HtmlUrl);

            // Determine the issue/PR number from the URL
            // comment.HtmlUrl looks like: https://github.com/dotnet/runtime/issues/12345#issuecomment-...
            // or https://github.com/dotnet/runtime/pull/12345#issuecomment-...
            var (issueNumber, isPr) = ParseIssueNumberFromUrl(comment.HtmlUrl);
            if (issueNumber <= 0) continue;

            var source = new MentionSource
            {
                Owner = repo.Owner,
                Repo = repo.Name,
                Number = issueNumber,
                IsPullRequest = isPr,
                CommentId = comment.Id,
                Author = comment.User.Login,
                HtmlUrl = comment.HtmlUrl,
            };

            var command = CommandParser.Parse(comment.Body, isPr ? issueNumber : null);
            if (command is null) continue;

            await DispatchCommandAsync(source, command);
        }
    }

    // ── Poll issue/PR body descriptions ─────────────────────────────────

    private async Task PollIssuesAndPrsAsync(GitHubClient client, RepoConfig repo, DateTimeOffset since, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var request = new RepositoryIssueRequest
        {
            Since = since,
            SortProperty = IssueSort.Updated,
            SortDirection = SortDirection.Descending,
            State = ItemStateFilter.All,
        };

        var issues = await client.Issue.GetAllForRepository(repo.Owner, repo.Name, request,
            new ApiOptions { PageSize = 50, PageCount = 1 });

        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();

            var key = $"{repo.Owner}/{repo.Name}/issue/{issue.Number}";
            if (_processed.ContainsKey(key)) continue;

            if (!CommandParser.ContainsMention(issue.Body)) continue;

            // Mark as processed
            _processed[key] = true;

            var isPr = issue.PullRequest != null;

            logger.LogInformation("Detected @EgorBt mention in {Type} #{Number} body on {Owner}/{Repo}",
                isPr ? "PR" : "issue", issue.Number, repo.Owner, repo.Name);

            var source = new MentionSource
            {
                Owner = repo.Owner,
                Repo = repo.Name,
                Number = issue.Number,
                IsPullRequest = isPr,
                CommentId = null,
                Author = issue.User.Login,
                HtmlUrl = issue.HtmlUrl,
            };

            var command = CommandParser.Parse(issue.Body, isPr ? issue.Number : null);
            if (command is null) continue;

            await DispatchCommandAsync(source, command);
        }
    }

    // ── Dispatch to job tracker ─────────────────────────────────────────

    private async Task DispatchCommandAsync(MentionSource source, BotCommand command)
    {
        logger.LogInformation(
            "Dispatching command from @{Author}: targets=[{Targets}], commits={Commits}, hasCode={HasCode}",
            source.Author,
            string.Join(",", command.Targets),
            command.CommitsAndPrs,
            command.BenchmarkCode is not null);

        var tracker = services.GetRequiredService<JobTrackerService>();
        await tracker.HandleCommandAsync(source, command);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private GitHubClient CreateGitHubClient()
    {
        var token = config["Github:Token"]
            ?? Environment.GetEnvironmentVariable("EGORBOT_GH_TOKEN")
            ?? throw new InvalidOperationException("GitHub token not configured (Github:Token or EGORBOT_GH_TOKEN).");

        var botName = config["Github:BotName"] ?? "EgorBot";

        var client = new GitHubClient(new ProductHeaderValue(botName))
        {
            Credentials = new Credentials(token),
        };
        return client;
    }

    private List<RepoConfig> GetRepoConfigs()
    {
        var repos = new List<RepoConfig>();

        // dotnet/runtime
        repos.Add(new RepoConfig(
            config["Github:PrimaryRepo:Owner"] ?? "dotnet",
            config["Github:PrimaryRepo:Name"] ?? "runtime"));

        // EgorBot/runtime-utils (tracking repo)
        repos.Add(new RepoConfig(
            config["Github:TrackingRepo:Owner"] ?? "EgorBot",
            config["Github:TrackingRepo:Name"] ?? "runtime-utils"));

        return repos;
    }

    private static (int Number, bool IsPr) ParseIssueNumberFromUrl(string url)
    {
        // https://github.com/owner/repo/issues/12345#...
        // https://github.com/owner/repo/pull/12345#...
        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // segments: ["owner", "repo", "issues"|"pull", "12345"]
            if (segments.Length >= 4 && int.TryParse(segments[3], out var number))
            {
                var isPr = segments[2].Equals("pull", StringComparison.OrdinalIgnoreCase);
                return (number, isPr);
            }
        }
        catch { }
        return (0, false);
    }
}
