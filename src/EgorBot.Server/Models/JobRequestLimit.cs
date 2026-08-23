using System.ComponentModel.DataAnnotations;

namespace EgorBot.Server.Models;

/// <summary>
/// Persistent global override for the maximum jobs created by one API request.
/// </summary>
public sealed class JobRequestLimit
{
    public const int SingletonId = 1;

    [Key]
    public int Id { get; set; } = SingletonId;

    public int MaxJobs { get; set; }

    public DateTime UpdatedAt { get; set; }
}
