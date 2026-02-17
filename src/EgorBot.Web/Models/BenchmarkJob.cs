using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EgorBot.Web.Models;

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

    /// <summary>Semicolon-separated commits/PRs, e.g. "PR_12345;main".</summary>
    public string CommitsAndPrs { get; set; } = "";

    /// <summary>Optional CLI args for BDN (e.g. --filter, --envvars).</summary>
    public string? BdnArguments { get; set; }

    /// <summary>Optional C# benchmark code snippet.</summary>
    public string? BenchmarkCode { get; set; }

    /// <summary>Whether to enable profiling (perf record).</summary>
    public bool UseProfiler { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>Final benchmark results as Markdown.</summary>
    public string? ResultMarkdown { get; set; }

    /// <summary>Error message if the job failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Provider-specific instance identifier for cleanup.</summary>
    [MaxLength(256)]
    public string? CloudProviderInstanceId { get; set; }

    [InverseProperty(nameof(JobLogEntry.Job))]
    public ICollection<JobLogEntry> LogEntries { get; set; } = [];
}
