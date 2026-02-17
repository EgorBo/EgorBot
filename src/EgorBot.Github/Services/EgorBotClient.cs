using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EgorBot.Github.Models;

namespace EgorBot.Github.Services;

/// <summary>
/// HTTP client for communicating with the EgorBot.Web service.
/// Submits benchmark jobs and polls for their status/results.
/// </summary>
public sealed class EgorBotClient(HttpClient http, ILogger<EgorBotClient> logger)
{
    // ── DTOs matching EgorBot.Web's API ──────────────────────────────────

    private sealed class StartJobRequest
    {
        [JsonPropertyName("platforms")]
        public required List<string> Platforms { get; init; }

        [JsonPropertyName("commitsAndPrs")]
        public required string CommitsAndPrs { get; init; }

        [JsonPropertyName("bdnArguments")]
        public string? BdnArguments { get; init; }

        [JsonPropertyName("benchmarkCode")]
        public string? BenchmarkCode { get; init; }

        [JsonPropertyName("useProfiler")]
        public bool UseProfiler { get; init; }
    }

    public sealed class StartJobResponse
    {
        [JsonPropertyName("groupId")]
        public Guid GroupId { get; set; }

        [JsonPropertyName("jobs")]
        public List<JobEntry> Jobs { get; set; } = [];
    }

    public sealed class JobEntry
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";
    }

    public sealed class JobStatusResponse
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("hasResult")]
        public bool HasResult { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    // ── API calls ────────────────────────────────────────────────────────

    /// <summary>Submit a benchmark job to EgorBot.Web. Returns the response or null on failure.</summary>
    public async Task<StartJobResponse?> StartJobAsync(BotCommand command)
    {
        var request = new StartJobRequest
        {
            Platforms = command.Targets,
            CommitsAndPrs = command.CommitsAndPrs,
            BdnArguments = command.BdnArguments,
            BenchmarkCode = command.BenchmarkCode,
            UseProfiler = command.UseProfiler,
        };

        try
        {
            logger.LogInformation("Submitting job to EgorBot.Web: targets=[{Targets}], commits={Commits}",
                string.Join(",", command.Targets), command.CommitsAndPrs);

            var response = await http.PostAsJsonAsync("/api/jobs", request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("EgorBot.Web returned {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var result = JsonSerializer.Deserialize<StartJobResponse>(body);
            logger.LogInformation("Job submitted: groupId={GroupId}, {Count} job(s)",
                result?.GroupId, result?.Jobs.Count);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit job to EgorBot.Web");
            return null;
        }
    }

    /// <summary>Get job status from EgorBot.Web.</summary>
    public async Task<JobStatusResponse?> GetJobStatusAsync(Guid jobId)
    {
        try
        {
            var response = await http.GetAsync($"/api/jobs/{jobId}/status");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<JobStatusResponse>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get job status for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>Get job result markdown from EgorBot.Web.</summary>
    public async Task<string?> GetJobResultAsync(Guid jobId)
    {
        try
        {
            var response = await http.GetAsync($"/api/jobs/{jobId}/result");
            if (!response.IsSuccessStatusCode) return null;

            // Check content type — if text/markdown, return directly
            if (response.Content.Headers.ContentType?.MediaType == "text/markdown")
                return await response.Content.ReadAsStringAsync();

            // Otherwise it's JSON with an error field
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var err)
                ? $"Error: {err.GetString()}"
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get job result for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>Get the logs page URL for a job.</summary>
    public string GetLogsUrl(Guid jobId) => $"{http.BaseAddress?.ToString().TrimEnd('/')}/jobs/{jobId}";
}
