using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EgorBot.Github.Services;

/// <summary>
/// HTTP client for the EgorBot.BenchmarkValidator service.
/// Validates benchmark snippets before spawning VMs.
/// </summary>
public sealed class BenchmarkValidatorClient(HttpClient http, ILogger<BenchmarkValidatorClient> logger)
{
    // ── DTOs ─────────────────────────────────────────────────────────────

    private sealed class ValidateRequest
    {
        [JsonPropertyName("benchmarkCode")]
        public string? BenchmarkCode { get; init; }

        [JsonPropertyName("bdnArguments")]
        public string? BdnArguments { get; init; }
    }

    public sealed class ValidateResponse
    {
        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("benchmarkCount")]
        public int BenchmarkCount { get; set; }
    }

    // ── API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Validate the benchmark snippet. Returns null on communication failure.
    /// </summary>
    public async Task<ValidateResponse?> ValidateAsync(string? benchmarkCode, string? bdnArguments)
    {
        var request = new ValidateRequest
        {
            BenchmarkCode = benchmarkCode,
            BdnArguments = bdnArguments,
        };

        try
        {
            logger.LogInformation("Validating benchmark (hasCode={HasCode})", benchmarkCode is not null);
            var response = await http.PostAsJsonAsync("/api/validate", request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                logger.LogError("Validator returned {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ValidateResponse>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to call BenchmarkValidator service");
            return null;
        }
    }
}
