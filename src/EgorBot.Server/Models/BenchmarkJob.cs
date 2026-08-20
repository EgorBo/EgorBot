using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using EgorBot.Shared;

namespace EgorBot.Server.Models;

/// <summary>
/// Persisted entity representing a single benchmark job targeting one platform.
/// A multi-platform StartJob request fans out into N BenchmarkJob rows sharing the same GroupId.
/// </summary>
public class BenchmarkJob
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Groups jobs originating from the same StartJob request.</summary>
    public Guid GroupId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>Target platform, e.g. "linux_x64", "local_x64".</summary>
    [MaxLength(32)]
    public string Platform { get; set; } = "";

    /// <summary>What the agent runs: BDN microbenchmarks (default) or a macro-benchmark like OrchardCore.</summary>
    public BenchmarkKind Kind { get; set; } = BenchmarkKind.Bdn;

    /// <summary>Semicolon-separated commits/PRs, e.g. "PR_12345;main".</summary>
    public string CommitsAndPrs { get; set; } = "";

    /// <summary>Optional CLI args for BDN (e.g. --filter, --envvars).</summary>
    public string? BdnArguments { get; set; }

    /// <summary>Optional C# benchmark code snippet.</summary>
    public string? BenchmarkCode { get; set; }

    /// <summary>Whether to enable profiling (perf record).</summary>
    public bool UseProfiler { get; set; }

    /// <summary>
    /// Optional comma-separated event list for `perf stat -e` (e.g. "l1d_cache,l1d_cache_refill").
    /// Empty = the agent's portable default set.
    /// </summary>
    [MaxLength(512)]
    public string? PerfStatEvents { get; set; }

    /// <summary>Number of times to run all benchmarks (default 1).</summary>
    public int Attempts { get; set; } = 1;

    /// <summary>GitHub login (or display name) of the user who requested the job.</summary>
    [MaxLength(128)]
    public string? RequestedBy { get; set; }

    /// <summary>URL of the original GitHub comment/issue that triggered the job.</summary>
    [MaxLength(512)]
    public string? SourceUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>Final benchmark results as Markdown.</summary>
    public string? ResultMarkdown { get; set; }

    /// <summary>Error message if the job failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>URL of the tracking issue in the Benchmarks repo.</summary>
    [MaxLength(512)]
    public string? TrackingIssueUrl { get; set; }

    /// <summary>URL of the uploaded full log in Azure Blob Storage.</summary>
    [MaxLength(512)]
    public string? LogsBlobUrl { get; set; }

    /// <summary>Provider-specific instance identifier for cleanup.</summary>
    [MaxLength(256)]
    public string? CloudProviderInstanceId { get; set; }

    [InverseProperty(nameof(JobLogEntry.Job))]
    public ICollection<JobLogEntry> LogEntries { get; set; } = [];
}
