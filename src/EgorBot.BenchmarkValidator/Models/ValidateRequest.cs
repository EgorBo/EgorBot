namespace EgorBot.BenchmarkValidator.Models;

public sealed class ValidateRequest
{
    /// <summary>C# benchmark snippet. Null means dotnet/performance (skip for now).</summary>
    public string? BenchmarkCode { get; init; }

    /// <summary>Optional BDN CLI arguments that the user specified.</summary>
    public string? BdnArguments { get; init; }
}

public sealed class ValidateResponse
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public int BenchmarkCount { get; init; }
}
