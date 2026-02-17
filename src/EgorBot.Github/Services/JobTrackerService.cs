using System.Collections.Concurrent;
using EgorBot.Github.Models;
using Octokit;

namespace EgorBot.Github.Services;

/// <summary>
/// Manages the lifecycle of benchmark jobs:
///   1. Submit the job to EgorBot.Server
///   2. Create a tracking issue in the runtime-utils repo
///   3. Poll job status and post results as comments
///   4. Close the tracking issue when all jobs complete
/// </summary>
public sealed class JobTrackerService(
    EgorBotClient botClient,
    IConfiguration config,
    ILogger<JobTrackerService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, TrackedJob> _activeJobs = new();
    private Timer? _pollTimer;

    /// <summary>
    /// Handle a parsed @EgorBt command: submit to EgorBot.Server, create tracking issue, start monitoring.
    /// </summary>
    public async Task HandleCommandAsync(MentionSource source, BotCommand command)
    {
        if (command.IsHelp)
        {
            await PostHelpCommentAsync(source);
            return;
        }

        // 1. Submit job to EgorBot.Server
        var response = await botClient.StartJobAsync(command, source.Author);
        if (response is null)
        {
            logger.LogError("Failed to submit job for {Owner}/{Repo}#{Number}", source.Owner, source.Repo, source.Number);
            await PostCommentOnSourceAsync(source, "Failed to submit the benchmark job to EgorBot. Please try again later.");
            return;
        }

        var tracked = new TrackedJob
        {
            Source = source,
            Command = command,
            GroupId = response.GroupId,
            Jobs = response.Jobs.Select(j => new JobInfo { Id = j.Id, Platform = j.Platform }).ToList(),
        };

        // 2. Create tracking issue in runtime-utils repo
        await CreateTrackingIssueAsync(tracked);

        // 3. Register for monitoring
        _activeJobs[response.GroupId] = tracked;
        EnsurePollingStarted();

        logger.LogInformation("Job group {GroupId} registered for tracking ({Count} jobs)",
            response.GroupId, tracked.Jobs.Count);
    }

    // ── Tracking issue lifecycle ────────────────────────────────────────

    private async Task CreateTrackingIssueAsync(TrackedJob tracked)
    {
        try
        {
            var ghClient = CreateGitHubClient();
            var trackingOwner = config["Github:TrackingRepo:Owner"] ?? "EgorBot";
            var trackingRepo = config["Github:TrackingRepo:Name"] ?? "runtime-utils";

            var sourceType = tracked.Source.IsPullRequest ? "PR" : "issue";
            var sourceRef = $"{tracked.Source.Owner}/{tracked.Source.Repo}#{tracked.Source.Number}";

            var title = $"Benchmarks for {sourceRef} (for @{tracked.Source.Author})";

            var logsLinks = string.Join("\n", tracked.Jobs.Select(j =>
                $"- **{j.Platform}**: [logs]({botClient.GetLogsUrl(j.Id)})"));

            var body = $"""
                Processing benchmark request from [{sourceType} {sourceRef}]({tracked.Source.HtmlUrl}).

                **Targets:** {string.Join(", ", tracked.Command.Targets)}
                **Commits:** `{tracked.Command.CommitsAndPrs}`

                {logsLinks}

                Results will be posted here once they are ready.
                """;

            var newIssue = new NewIssue(title) { Body = body };
            var issue = await ghClient.Issue.Create(trackingOwner, trackingRepo, newIssue);

            tracked.TrackingIssueNumber = issue.Number;

            logger.LogInformation("Created tracking issue #{IssueNumber} in {Owner}/{Repo}",
                issue.Number, trackingOwner, trackingRepo);

            // Also post a comment on the original source pointing to the tracking issue
            var trackingUrl = issue.HtmlUrl;
            var reply = $"Benchmark job submitted. Tracking progress at {trackingUrl}";
            await PostCommentOnSourceAsync(tracked.Source, reply);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tracking issue for group {GroupId}", tracked.GroupId);
        }
    }

    // ── Polling for job completion ──────────────────────────────────────

    private void EnsurePollingStarted()
    {
        if (_pollTimer != null) return;
        _pollTimer = new Timer(PollCallback, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private async void PollCallback(object? state)
    {
        foreach (var (groupId, tracked) in _activeJobs)
        {
            try
            {
                await CheckJobGroupAsync(tracked);

                // If all jobs are done, remove from active tracking
                if (tracked.CompletedCount >= tracked.Jobs.Count)
                {
                    _activeJobs.TryRemove(groupId, out _);
                    logger.LogInformation("All jobs completed for group {GroupId}. Closing tracking issue.", groupId);
                    await CloseTrackingIssueAsync(tracked);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking job group {GroupId}", groupId);
            }
        }

        // Stop polling if no more active jobs
        if (_activeJobs.IsEmpty)
        {
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    private async Task CheckJobGroupAsync(TrackedJob tracked)
    {
        foreach (var job in tracked.Jobs.Where(j => !j.IsCompleted))
        {
            var status = await botClient.GetJobStatusAsync(job.Id);
            if (status is null) continue;

            var terminal = status.Status is "Completed" or "Failed" or "TimedOut" or "Cancelled";
            if (!terminal) continue;

            job.IsCompleted = true;
            tracked.CompletedCount++;

            if (status.Status == "Completed" && status.HasResult)
            {
                var markdown = await botClient.GetJobResultAsync(job.Id);
                await PostResultCommentAsync(tracked, job, markdown, success: true);
            }
            else
            {
                var error = status.ErrorMessage ?? $"Job {status.Status.ToLowerInvariant()}.";
                await PostResultCommentAsync(tracked, job, error, success: false);
            }

            logger.LogInformation("Job {JobId} ({Platform}) → {Status}",
                job.Id, job.Platform, status.Status);
        }
    }

    private async Task PostResultCommentAsync(TrackedJob tracked, JobInfo job, string? content, bool success)
    {
        if (tracked.TrackingIssueNumber is not { } issueNumber) return;

        try
        {
            var ghClient = CreateGitHubClient();
            var trackingOwner = config["Github:TrackingRepo:Owner"] ?? "EgorBot";
            var trackingRepo = config["Github:TrackingRepo:Name"] ?? "runtime-utils";

            string body;
            if (success)
            {
                body = $"""
                    ## Results for `{job.Platform}`

                    @{tracked.Source.Author}

                    {content ?? "_No results available._"}
                    """;
            }
            else
            {
                body = $"""
                    ## `{job.Platform}` — Failed

                    @{tracked.Source.Author}

                    {content}

                    [View logs]({botClient.GetLogsUrl(job.Id)})
                    """;
            }

            await ghClient.Issue.Comment.Create(trackingOwner, trackingRepo, issueNumber, body);
            logger.LogInformation("Posted result for {Platform} on tracking issue #{Issue}",
                job.Platform, issueNumber);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to post result comment for job {JobId}", job.Id);
        }
    }

    private async Task CloseTrackingIssueAsync(TrackedJob tracked)
    {
        if (tracked.TrackingIssueNumber is not { } issueNumber) return;

        try
        {
            var ghClient = CreateGitHubClient();
            var trackingOwner = config["Github:TrackingRepo:Owner"] ?? "EgorBot";
            var trackingRepo = config["Github:TrackingRepo:Name"] ?? "runtime-utils";

            // Post a closing comment
            var summary = $"""
                All benchmark jobs for this request have completed.

                cc @{tracked.Source.Author}
                """;

            await ghClient.Issue.Comment.Create(trackingOwner, trackingRepo, issueNumber, summary);
            await ghClient.Issue.Update(trackingOwner, trackingRepo, issueNumber,
                new IssueUpdate { State = ItemState.Closed });

            logger.LogInformation("Closed tracking issue #{Issue}", issueNumber);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to close tracking issue #{Issue}", issueNumber);
        }
    }

    // ── Comment on the original source ──────────────────────────────────

    private async Task PostCommentOnSourceAsync(MentionSource source, string body)
    {
        try
        {
            var ghClient = CreateGitHubClient();
            await ghClient.Issue.Comment.Create(source.Owner, source.Repo, source.Number, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post comment on {Owner}/{Repo}#{Number}",
                source.Owner, source.Repo, source.Number);
        }
    }

    private async Task PostHelpCommentAsync(MentionSource source)
    {
        var help = """
            ### EgorBot Usage

            ```
            @EgorBt [targets] [options] [BDN arguments]
            ```[cs]
            // optional benchmark code
            ```
            ```

            **Targets** (default: `-azure_genoa`):
            `-arm` `-intel` `-amd` `-x64`
            `-azure_genoa` `-azure_cobalt100` `-azure_cascadelake` `-azure_milano` `-azure_ampere`
            `-aws_graviton4` `-aws_graviton3` `-aws_sapphirelake` `-aws_icelake` `-aws_genoa` `-aws_turin`

            **Options:**
            `-profiler` — enable perf profiler
            `-pr <number>` — target a specific PR
            `-help` — show this help

            Targets can be prefixed with OS: `-windows_arm`, `-linux_intel`
            First unrecognized argument starts BDN arguments (e.g. `--filter "*MyBench*"`).
            """;

        await PostCommentOnSourceAsync(source, help);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private GitHubClient CreateGitHubClient()
    {
        var token = config["Github:Token"]
            ?? Environment.GetEnvironmentVariable("EGORBOT_GH_TOKEN")
            ?? throw new InvalidOperationException("GitHub token not configured.");

        var botName = config["Github:BotName"] ?? "EgorBot";
        return new GitHubClient(new ProductHeaderValue(botName))
        {
            Credentials = new Credentials(token),
        };
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
    }
}
