namespace EgorBot.Github.Models;

/// <summary>
/// Parsed @EgorBot command extracted from a GitHub comment or issue/PR body.
/// </summary>
public sealed class BotCommand
{
    /// <summary>Target platforms (e.g. \"arm\", \"aws_genoa\"). Defaults to [\"helix_osx_arm64\"] if none specified.</summary>
    public List<string> Targets { get; init; } = [];

    /// <summary>Commits/PRs to compare, semicolon-separated (e.g. "PR_12345;main").</summary>
    public string CommitsAndPrs { get; init; } = "";

    /// <summary>BDN CLI arguments (everything that wasn't recognized as an EgorBot command).</summary>
    public string? BdnArguments { get; init; }

    /// <summary>Optional C# benchmark snippet from a markdown code block.</summary>
    public string? BenchmarkCode { get; init; }

    /// <summary>Enable perf profiler.</summary>
    public bool UseProfiler { get; init; }

    /// <summary>
    /// Optional comma-separated event list for `perf stat -e`
    /// (e.g. "l1d_cache,l1d_cache_refill,cycles"). Implies <see cref="UseProfiler"/>.
    /// </summary>
    public string? PerfStatEvents { get; init; }

    /// <summary>Number of times to run all benchmarks (default 1).</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>Show help text instead of running a job.</summary>
    public bool IsHelp { get; init; }

    /// <summary>Validation error that should be reported back to the user instead of running a job.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Captures where the @EgorBot mention came from on GitHub.
/// </summary>
public sealed class MentionSource
{
    /// <summary>GitHub owner (e.g. "dotnet").</summary>
    public required string Owner { get; init; }

    /// <summary>GitHub repository name (e.g. "runtime").</summary>
    public required string Repo { get; init; }

    /// <summary>Issue or PR number.</summary>
    public required int Number { get; init; }

    /// <summary>Whether the source is a PR (true) or an issue (false).</summary>
    public required bool IsPullRequest { get; init; }

    /// <summary>The GitHub comment ID (null if the mention is in the issue/PR body itself).</summary>
    public long? CommentId { get; init; }

    /// <summary>Login of the user who wrote the comment/description.</summary>
    public required string Author { get; init; }

    /// <summary>Direct URL to the comment or issue/PR.</summary>
    public required string HtmlUrl { get; init; }
}

/// <summary>
/// Tracks an in-flight benchmark job submitted to EgorBot.Server.
/// </summary>
public sealed class TrackedJob
{
    public required MentionSource Source { get; init; }
    public required BotCommand Command { get; init; }

    /// <summary>Job group ID returned by EgorBot.Server.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Individual job IDs per platform.</summary>
    public List<JobInfo> Jobs { get; set; } = [];

    /// <summary>Tracking issue number in the Benchmarks repo.</summary>
    public int? TrackingIssueNumber { get; set; }

    /// <summary>How many jobs have been completed (success or failure).</summary>
    public int CompletedCount { get; set; }
}

public sealed class JobInfo
{
    public required Guid Id { get; init; }
    public required string Platform { get; init; }
    public bool IsCompleted { get; set; }
    public string? LogsBlobUrl { get; set; }
    public string? ResultCommentUrl { get; set; }
    public bool Succeeded { get; set; }
}
