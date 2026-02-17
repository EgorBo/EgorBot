using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EgorBot.Web.Models;

/// <summary>
/// A single log line captured from the agent and persisted for real-time viewing.
/// </summary>
public class JobLogEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public Guid JobId { get; set; }

    [ForeignKey(nameof(JobId))]
    public BenchmarkJob? Job { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Message { get; set; } = "";
}
