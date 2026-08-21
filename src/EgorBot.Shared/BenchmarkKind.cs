using System.Text.Json.Serialization;

namespace EgorBot.Shared;

/// <summary>
/// What the agent actually runs on the provisioned machine.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BenchmarkKind>))]
public enum BenchmarkKind
{
    /// <summary>BenchmarkDotNet microbenchmarks (snippet or dotnet/performance).</summary>
    Bdn,

    /// <summary>
    /// OrchardCore CMS throughput (requests/sec) macro-benchmark.
    /// Linux/macOS x64/arm64 only, and always compares runtime builds (commits/PRs).
    /// </summary>
    Orchard,
}

/// <summary>
/// Helpers for <see cref="BenchmarkKind"/>: parsing user input and checking which
/// hardware targets a kind can actually run on.
/// </summary>
public static class BenchmarkKinds
{
    /// <summary>Command tokens that select the OrchardCore benchmark.</summary>
    private static readonly HashSet<string> OrchardAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "orchard", "orchardcms", "orchardcore",
    };

    /// <summary>Recognize a benchmark-kind token from an @EgorBot command line.</summary>
    public static bool TryParseToken(string token, out BenchmarkKind kind)
    {
        kind = BenchmarkKind.Bdn;
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (OrchardAliases.Contains(token.Trim().TrimStart('-')))
        {
            kind = BenchmarkKind.Orchard;
            return true;
        }

        return false;
    }

    /// <summary>Parse a wire value ("bdn"/"orchard"), falling back to <see cref="BenchmarkKind.Bdn"/>.</summary>
    public static BenchmarkKind Parse(string? value) =>
        Enum.TryParse<BenchmarkKind>(value, ignoreCase: true, out var kind) ? kind : BenchmarkKind.Bdn;

    /// <summary>Value passed to the agent's <c>--benchmark_kind</c> argument.</summary>
    public static string ToAgentArg(this BenchmarkKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>Whether the kind can run on the given canonical target.</summary>
    public static bool SupportsTarget(this BenchmarkKind kind, string canonicalTarget)
    {
        if (kind != BenchmarkKind.Orchard)
            return true;

        // The benchmark drives a real ASP.NET Core server with bombardier.
        // Windows and 32-bit targets are not supported.
        if (!TargetCatalog.TryGetTarget(canonicalTarget, out var target) || target is null)
            return false;

        return (target.OsFamily.Equals("linux", StringComparison.OrdinalIgnoreCase)
                || target.OsFamily.Equals("osx", StringComparison.OrdinalIgnoreCase))
               && target.Arch is VmArch.X64 or VmArch.Arm64;
    }

    /// <summary>Human-readable list of targets a kind can run on (for error messages).</summary>
    public static string SupportedTargetsDescription(this BenchmarkKind kind) =>
        kind == BenchmarkKind.Orchard
            ? "Linux and macOS x64/arm64 targets (e.g. `-arm`, `-amd`, `-intel`, `-azure_cobalt100`)"
            : "any target";
}
