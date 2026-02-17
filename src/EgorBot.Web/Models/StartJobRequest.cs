namespace EgorBot.Web.Models;

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
}
