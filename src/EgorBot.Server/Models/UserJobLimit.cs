using System.ComponentModel.DataAnnotations;

namespace EgorBot.Server.Models;

/// <summary>
/// Persistent per-user override for the global rolling job limit.
/// </summary>
public sealed class UserJobLimit
{
    [Key]
    [MaxLength(128)]
    public string UserKey { get; set; } = "";

    public int MaxJobs { get; set; }

    public DateTime UpdatedAt { get; set; }
}
