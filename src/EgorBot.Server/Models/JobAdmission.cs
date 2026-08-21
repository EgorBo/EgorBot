using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EgorBot.Server.Models;

/// <summary>
/// Durable rate-limit reservation for one accepted benchmark job.
/// </summary>
public sealed class JobAdmission
{
    [Key]
    [ForeignKey(nameof(Job))]
    public Guid JobId { get; set; }

    [MaxLength(128)]
    public string UserKey { get; set; } = "";

    public DateTime AdmittedAt { get; set; }

    public BenchmarkJob Job { get; set; } = null!;
}
