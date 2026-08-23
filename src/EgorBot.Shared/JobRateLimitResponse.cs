using System.Text.Json.Serialization;

namespace EgorBot.Shared;

/// <summary>
/// Structured HTTP 429 response returned when a job request exceeds a per-request
/// or rolling-window limit.
/// Shared by the server and GitHub client so the wire contract cannot drift.
/// </summary>
public sealed class JobRateLimitResponse
{
    public const string RollingWindowCode = "job_limit_reached";
    public const string RequestLimitCode = "job_request_limit_reached";

    [JsonPropertyName("code")]
    public string Code { get; set; } = RollingWindowCode;

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("used")]
    public int Used { get; set; }

    [JsonPropertyName("requested")]
    public int Requested { get; set; }

    [JsonPropertyName("windowHours")]
    public int WindowHours { get; set; }

    [JsonPropertyName("retryAt")]
    public DateTime? RetryAt { get; set; }
}
