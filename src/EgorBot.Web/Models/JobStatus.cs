namespace EgorBot.Web.Models;

/// <summary>
/// Lifecycle states for a benchmark job.
/// </summary>
public enum JobStatus
{
    Pending,
    Provisioning,
    Running,
    Completed,
    Failed,
    TimedOut,
    Cancelled
}
