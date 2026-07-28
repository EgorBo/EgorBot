namespace EgorBot.Server.Models;

/// <summary>
/// Request DTO for the POST /api/jobs (StartJob) endpoint.
/// </summary>
public sealed class StartJobRequest
{
    /// <summary>Target platforms, e.g. ["linux_x64", "local_x64"].</summary>
    public required List<string> Platforms { get; init; }

    /// <summary>Semicolon-separated commits/PRs, e.g. "PR_12345;main".</summary>
    public required string CommitsAndPrs { get; init; }

    /// <summary>Optional BDN CLI arguments.</summary>
    public string? BdnArguments { get; init; }

    /// <summary>Optional C# benchmark code snippet.</summary>
    public string? BenchmarkCode { get; init; }

    /// <summary>Enable perf profiler on the agent.</summary>
    public bool UseProfiler { get; init; }

    /// <summary>
    /// Optional comma-separated event list for `perf stat -e`, e.g.
    /// "l1d_cache,l1d_cache_refill,cycles,instructions". Implies <see cref="UseProfiler"/>.
    /// </summary>
    public string? PerfStatEvents { get; init; }

    /// <summary>Number of times to run all benchmarks (default 1).</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>GitHub login (or display name) of the user who requested the job.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>URL of the original GitHub comment/issue that triggered the job.</summary>
    public string? SourceUrl { get; init; }
}
