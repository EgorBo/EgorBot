using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EgorBot.Github.Models;
using Octokit;

namespace EgorBot.Github.Services;

/// <summary>
/// Manages the lifecycle of benchmark jobs:
///   1. Submit the job to EgorBot.Server
///   2. Create a tracking issue in the Benchmarks repo
///   3. Poll job status and post results as comments on the tracking issue
///   4. Close the tracking issue when all jobs complete
///   5. If the original requester is @Copilot, post a single summary comment
///      back on the source PR (using a separate token)
///
/// All communication happens in the tracking repo (EgorBot/Benchmarks).
/// No comments are posted in dotnet/runtime except the Copilot notification.
/// </summary>
public sealed class JobTrackerService(
    EgorBotClient botClient,
    IConfiguration config,
    ILogger<JobTrackerService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, TrackedJob> _activeJobs = new();
    private readonly Lock _timerLock = new();
    private Timer? _pollTimer;

    /// <summary>
    /// Handle a parsed @EgorBot command: submit to EgorBot.Server, create tracking issue, start monitoring.
    /// </summary>
    public async Task HandleCommandAsync(MentionSource source, BotCommand command)
    {
        if (command.IsHelp)
        {
            await PostHelpCommentAsync(source);
            return;
        }

        if (command.ErrorMessage is not null)
        {
            await PostErrorCommentAsync(source, $"⚠️ {command.ErrorMessage}");
            return;
        }

        // If the mention is in a tracking issue (in the tracking repo), try to infer
        // the source PR context from the issue title (e.g. "Benchmarks for dotnet/runtime#124445 ...")
        var effectiveCommand = await TryInferPrFromTrackingIssueAsync(source, command);

        // 1. Submit job to EgorBot.Server
        var response = await botClient.StartJobAsync(effectiveCommand, source.Author, source.HtmlUrl);
        if (response is null)
        {
            logger.LogError("Failed to submit job for {Owner}/{Repo}#{Number}", source.Owner, source.Repo, source.Number);
            // Report on the source issue/PR too: otherwise the user outside the tracking
            // repo just sees the 👀 reaction and never hears back.
            await PostErrorCommentAsync(source, "❌ Failed to submit the benchmark job to EgorBot. Please try again later.");
            return;
        }

        var tracked = new TrackedJob
        {
            Source = source,
            Command = effectiveCommand,
            GroupId = response.GroupId,
            Jobs = response.Jobs.Select(j => new JobInfo { Id = j.Id, Platform = j.Platform }).ToList(),
        };

        // 2. Create tracking issue in Benchmarks repo (unless already in one)
        if (IsTrackingRepo(source.Owner, source.Repo))
        {
            // The command was posted in a tracking issue — reuse it
            tracked.TrackingIssueNumber = source.Number;

            var logsLinks = string.Join("\n", tracked.Jobs.Select(j =>
                $"- **{j.Platform}**: [live logs]({botClient.GetLogsUrl(j.Id)})"));

            await PostCommentOnTrackingIssueAsync(tracked,
                $"Benchmark job submitted. The results will be posted here once they are ready.\n\n{logsLinks}");
        }
        else
        {
            await CreateTrackingIssueAsync(tracked);
        }

        // 3. Register for monitoring
        _activeJobs[response.GroupId] = tracked;
        EnsurePollingStarted();

        logger.LogInformation("Job group {GroupId} registered for tracking ({Count} jobs)",
            response.GroupId, tracked.Jobs.Count);
    }

    // ── Infer PR from tracking issue title ──────────────────────────────

    /// <summary>
    /// If the command came from the tracking repo and has no explicit commits,
    /// try to parse a PR number from the issue title.
    /// E.g. "Benchmarks for dotnet/runtime#124445 (for @EgorBo)" → PR_124445
    /// The title is the same shape for issues and PRs, so the referenced item is
    /// verified before it is treated as a PR.
    /// </summary>
    private async Task<BotCommand> TryInferPrFromTrackingIssueAsync(MentionSource source, BotCommand command)
    {
        if (!IsTrackingRepo(source.Owner, source.Repo))
            return command;

        // Only infer if no explicit commits were provided
        if (command.CommitsAndPrs is not ("main" or ""))
            return command;

        try
        {
            var ghClient = CreateGitHubClient();
            var issue = await ghClient.Issue.Get(source.Owner, source.Repo, source.Number);

            var match = Regex.Match(issue.Title, @"(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)#(?<number>\d+)");
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number))
                return command;

            var refOwner = match.Groups["owner"].Value;
            var refRepo = match.Groups["repo"].Value;

            // The agent always clones dotnet/runtime, so "PR_N" only means anything there.
            var primaryOwner = config["Github:PrimaryRepo:Owner"] ?? "dotnet";
            var primaryRepo = config["Github:PrimaryRepo:Name"] ?? "runtime";
            if (!refOwner.Equals(primaryOwner, StringComparison.OrdinalIgnoreCase)
                || !refRepo.Equals(primaryRepo, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Tracking issue #{Number} references {Owner}/{Repo}, which is not the benchmarked repo — skipping PR inference",
                    source.Number, refOwner, refRepo);
                return command;
            }

            // A tracking issue created from a plain issue has the very same title shape as
            // one created from a PR. Assuming "PR" here produced PR_<issue number>, and the
            // agent then failed on `git fetch origin pull/<issue number>/head`.
            var referenced = await ghClient.Issue.Get(refOwner, refRepo, number);
            if (referenced.PullRequest is null)
            {
                logger.LogInformation(
                    "Tracking issue #{Number} refers to {Owner}/{Repo}#{Ref}, which is an issue and not a PR — running without a PR build",
                    source.Number, refOwner, refRepo, number);
                return command;
            }

            logger.LogInformation("Inferred PR #{PrNumber} from tracking issue title: {Title}",
                number, issue.Title);
            return new BotCommand
            {
                Targets = command.Targets,
                CommitsAndPrs = $"main;PR_{number}",
                BdnArguments = command.BdnArguments,
                BenchmarkCode = command.BenchmarkCode,
                UseProfiler = command.UseProfiler,
                PerfStatEvents = command.PerfStatEvents,
                Attempts = command.Attempts,
                IsHelp = command.IsHelp,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to infer PR from tracking issue #{Number}", source.Number);
        }

        return command;
    }

    // ── Tracking issue lifecycle ────────────────────────────────────────

    private async Task CreateTrackingIssueAsync(TrackedJob tracked)
    {
        try
        {
            var ghClient = CreateGitHubClient();
            var (trackingOwner, trackingRepo) = GetTrackingRepo();

            var sourceType = tracked.Source.IsPullRequest ? "PR" : "issue";
            var sourceRef = $"{tracked.Source.Owner}/{tracked.Source.Repo}#{tracked.Source.Number}";

            var title = $"Benchmarks for {sourceRef} (for @{tracked.Source.Author})";

            var logsLinks = string.Join("\n", tracked.Jobs.Select(j =>
                $"- **{j.Platform}**: [live logs]({botClient.GetLogsUrl(j.Id)})"));

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

            // Notify the server so Telegram notifications can include the tracking issue link
            await botClient.SetTrackingIssueUrlAsync(tracked.GroupId, issue.HtmlUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tracking issue for group {GroupId}", tracked.GroupId);

            // Without a tracking issue every later result post is dropped, so the user
            // would never hear back at all. Tell them where the logs are instead.
            var logsLinks = string.Join("\n", tracked.Jobs.Select(j =>
                $"- **{j.Platform}**: [live logs]({botClient.GetLogsUrl(j.Id)})"));
            await PostErrorCommentAsync(tracked.Source,
                "⚠️ The benchmark job was submitted, but I could not create a tracking issue, " +
                $"so results won't be posted automatically. Follow the run here:\n\n{logsLinks}");
        }
    }

    // ── Polling for job completion ──────────────────────────────────────

    private void EnsurePollingStarted()
    {
        lock (_timerLock)
        {
            _pollTimer ??= new Timer(_ => PollCallback(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }
    }

    private int _polling;

    private async void PollCallback()
    {
        // A timer callback is `async void`: anything escaping it crashes the process.
        // It can also re-enter while the previous tick is still running (each tick does
        // several HTTP calls), which would post duplicate result comments.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            await PollOnceAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in job tracker poll");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task PollOnceAsync()
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
                    logger.LogInformation("All jobs completed for group {GroupId}.", groupId);

                    if (config.GetValue("Github:CloseTrackingIssues", false))
                        await CloseTrackingIssueAsync(tracked);
                    else
                        logger.LogInformation("Skipping closing tracking issue (Github:CloseTrackingIssues = false).");

                    // Copilot notification runs regardless of CloseTrackingIssues setting
                    await TryNotifyCopilotAsync(tracked);
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
            lock (_timerLock)
            {
                if (_activeJobs.IsEmpty)
                {
                    _pollTimer?.Dispose();
                    _pollTimer = null;
                }
            }
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
            job.LogsBlobUrl = status.LogsBlobUrl;
            tracked.CompletedCount++;

            if (status.Status == "Completed" && status.HasResult)
            {
                job.Succeeded = true;
                var markdown = await botClient.GetJobResultAsync(job.Id);
                await PostResultOnTrackingIssueAsync(tracked, job, markdown, success: true);
            }
            else
            {
                job.Succeeded = false;
                var error = status.ErrorMessage ?? $"Job {status.Status.ToLowerInvariant()}.";
                await PostResultOnTrackingIssueAsync(tracked, job, error, success: false);
            }

            logger.LogInformation("Job {JobId} ({Platform}) → {Status}",
                job.Id, job.Platform, status.Status);
        }
    }

    private async Task PostResultOnTrackingIssueAsync(TrackedJob tracked, JobInfo job, string? content, bool success)
    {
        if (tracked.TrackingIssueNumber is not { } issueNumber) return;

        try
        {
            var ghClient = CreateGitHubClient();
            var (trackingOwner, trackingRepo) = GetTrackingRepo();

            string body;
            if (success)
            {
                var logsLine = job.LogsBlobUrl is not null
                    ? $"\n\n[Full logs]({job.LogsBlobUrl})"
                    : "";

                body = $"""
                    ## Results for `{job.Platform}`

                    @{tracked.Source.Author}

                    {content ?? "_No results available._"}{logsLine}
                    """;
            }
            else
            {
                body = $"""
                    ## `{job.Platform}` — Failed

                    @{tracked.Source.Author}

                    {content}

                    [Job]({botClient.GetLogsUrl(job.Id)}){(job.LogsBlobUrl is not null ? $" | [Full logs]({job.LogsBlobUrl})" : "")}
                    """;
            }

            var resultComment = await ghClient.Issue.Comment.Create(trackingOwner, trackingRepo, issueNumber, body);
            job.ResultCommentUrl = resultComment.HtmlUrl;
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
            var (trackingOwner, trackingRepo) = GetTrackingRepo();

            // Post a closing comment on the tracking issue
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

    /// <summary>
    /// If the original requester's name starts with "copilot", post a notification
    /// on the source PR/issue. Runs independently of CloseTrackingIssues setting.
    /// </summary>
    private async Task TryNotifyCopilotAsync(TrackedJob tracked)
    {
        if (!tracked.Source.Author.StartsWith("copilot", StringComparison.OrdinalIgnoreCase))
            return;

        if (tracked.TrackingIssueNumber is not { } issueNumber)
        {
            logger.LogWarning("Copilot notification skipped — no tracking issue number.");
            return;
        }

        await NotifyCopilotOnSourceAsync(tracked, issueNumber);
    }

    /// <summary>
    /// Post a comment on the original dotnet/runtime PR/issue notifying the Copilot user
    /// that benchmarks are done. Uses a separate GitHub token (Github:CopilotNotifyToken)
    /// so it appears as a different identity.
    /// </summary>
    private async Task NotifyCopilotOnSourceAsync(TrackedJob tracked, int trackingIssueNumber)
    {
        try
        {
            var copilotToken = config["Github:CopilotNotifyToken"]
                ?? Environment.GetEnvironmentVariable("EGORBOT_GH_COPILOT_TOKEN");

            if (string.IsNullOrEmpty(copilotToken))
            {
                logger.LogWarning("Github:CopilotNotifyToken not configured — skipping Copilot notification.");
                return;
            }

            var (trackingOwner, trackingRepo) = GetTrackingRepo();
            var trackingUrl = $"https://github.com/{trackingOwner}/{trackingRepo}/issues/{trackingIssueNumber}";

            // Build per-platform result links
            var resultLines = string.Join("\n", tracked.Jobs.Select(j =>
            {
                var status = j.Succeeded ? "✅" : "❌";
                var link = j.ResultCommentUrl is not null
                    ? $"[{j.Platform}]({j.ResultCommentUrl})"
                    : j.Platform;
                return $"- {status} {link}";
            }));

            var comment = $"""
                @{tracked.Source.Author}, benchmark results are ready:

                {resultLines}

                Please analyze the results and act accordingly. </br> NOTE: some benchmarks may be flaky or bi-modal, so use your judgment when interpreting small differences.
                """;

            var ghClient = new GitHubClient(new ProductHeaderValue(config["Github:BotName"] ?? "EgorBot"))
            {
                Credentials = new Credentials(copilotToken),
            };

            await ghClient.Issue.Comment.Create(
                tracked.Source.Owner, tracked.Source.Repo, tracked.Source.Number, comment);

            logger.LogInformation("Posted Copilot notification on {Owner}/{Repo}#{Number}",
                tracked.Source.Owner, tracked.Source.Repo, tracked.Source.Number);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify Copilot on source issue");
        }
    }

    // ── Comment helpers (tracking repo only) ────────────────────────────

    private async Task PostCommentOnTrackingIssueAsync(TrackedJob tracked, string body)
    {
        if (tracked.TrackingIssueNumber is not { } issueNumber) return;

        try
        {
            var ghClient = CreateGitHubClient();
            var (trackingOwner, trackingRepo) = GetTrackingRepo();
            await ghClient.Issue.Comment.Create(trackingOwner, trackingRepo, issueNumber, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post comment on tracking issue #{Issue}", tracked.TrackingIssueNumber);
        }
    }

    /// <summary>
    /// Post a comment on the tracking repo. For commands that come from dotnet/runtime,
    /// we don't reply there — we only communicate through the tracking issue.
    /// If the command came from the tracking repo itself, reply directly on that issue.
    /// </summary>
    private async Task PostCommentOnTrackingRepoAsync(MentionSource source, string body)
    {
        if (IsTrackingRepo(source.Owner, source.Repo))
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
        else
        {
            // For dotnet/runtime — log only, no comment posted
            logger.LogInformation("Skipping reply on {Owner}/{Repo}#{Number} (not tracking repo): {Body}",
                source.Owner, source.Repo, source.Number, body);
        }
    }

    /// <summary>
    /// Post an error/warning message directly on the source issue/PR so the user
    /// always sees it — even when the source repo is not the tracking repo.
    /// </summary>
    private async Task PostErrorCommentAsync(MentionSource source, string body)
    {
        try
        {
            var ghClient = CreateGitHubClient();
            await ghClient.Issue.Comment.Create(source.Owner, source.Repo, source.Number, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post error comment on {Owner}/{Repo}#{Number}",
                source.Owner, source.Repo, source.Number);
        }
    }

    private async Task PostHelpCommentAsync(MentionSource source)
    {
        var help = """
            ### EgorBot Usage

            ```
            @EgorBot [targets] [options] [BDN arguments]
            ```[cs]
            // optional benchmark code
            ```
            ```

            **Targets** (default: `macos15_helix_arm64`):
            `-arm` `-intel` `-amd` `-x64`
            `-azure_genoa` `-azure_cobalt100` `-azure_cascadelake` `-azure_milano` `-azure_ampere` `-azure_turin` `-azure_emeraldrapids`
            `-aws_graviton5` `-aws_graviton4` `-aws_graviton3` `-aws_sapphirelake` `-aws_icelake` `-aws_genoa` `-aws_turin`

            **Options:**
            `-profiler` — enable perf profiler
            `-perf_events a,b,c` — custom `perf stat` events (implies `-profiler`),
            e.g. `-perf_events l1d_cache,l1d_cache_refill,l2d_cache_refill,cycles,instructions`.
            The events a machine supports are listed in the `perf_events.txt` artifact.
            `-pr <number>` — target a specific PR
            `-commits SHA1,SHA2,...` — specify commits to compare (`SHA~1` = its parent)
            `-attempts <n>` — repeat the run n times
            `-help` — show this help

            Targets can be prefixed with OS: `-windows_arm`, `-linux_intel`
            Anything on the `@EgorBot` line that isn't a target or an option is passed to
            BenchmarkDotNet (e.g. `--filter "*MyBench*"`).
            """;

        // Help is safe to post on the source repo since the user explicitly asked for it
        try
        {
            var ghClient = CreateGitHubClient();
            await ghClient.Issue.Comment.Create(source.Owner, source.Repo, source.Number, help);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post help on {Owner}/{Repo}#{Number}",
                source.Owner, source.Repo, source.Number);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private bool IsTrackingRepo(string owner, string repo)
    {
        var (trackingOwner, trackingRepo) = GetTrackingRepo();
        return owner.Equals(trackingOwner, StringComparison.OrdinalIgnoreCase) &&
               repo.Equals(trackingRepo, StringComparison.OrdinalIgnoreCase);
    }

    private (string Owner, string Repo) GetTrackingRepo() =>
        (config["Github:TrackingRepo:Owner"] ?? "EgorBot",
         config["Github:TrackingRepo:Name"] ?? "Benchmarks");

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
