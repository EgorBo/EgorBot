namespace EgorBot.Data;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    TimedOut,
}

public class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Requester { get; set; } = "";
    public string Repository { get; set; } = "";
    public int? PrNumber { get; set; }
    public string? Commits { get; set; }
    public string? BenchmarkSnippetUrl { get; set; }
    public string? RawCommand { get; set; }
    public bool EnablePerf { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long? GitHubCommentId { get; set; }
    public int? GitHubIssueOrPrNumber { get; set; }
    public string? ResultMarkdown { get; set; }

    public List<SubJob> SubJobs { get; set; } = [];
}
