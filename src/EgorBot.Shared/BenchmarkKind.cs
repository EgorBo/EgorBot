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

    /// <summary>
    /// Fixed ASP.NET Core minimal API throughput macro-benchmark.
    /// Linux/Windows x64/arm64 and macOS arm64, always comparing runtime builds.
    /// </summary>
    MinimalApi,
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

    /// <summary>Command tokens that select the ASP.NET Core minimal API benchmark.</summary>
    private static readonly HashSet<string> MinimalApiAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "minimalapi", "minimal-api",
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

        if (MinimalApiAliases.Contains(token.Trim().TrimStart('-')))
        {
            kind = BenchmarkKind.MinimalApi;
            return true;
        }

        return false;
    }

    /// <summary>Parse a wire value, falling back to <see cref="BenchmarkKind.Bdn"/>.</summary>
    public static BenchmarkKind Parse(string? value) =>
        Enum.TryParse<BenchmarkKind>(value, ignoreCase: true, out var kind) ? kind : BenchmarkKind.Bdn;

    /// <summary>Value passed to the agent's <c>--benchmark_kind</c> argument.</summary>
    public static string ToAgentArg(this BenchmarkKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>Whether the kind is a fixed macro-benchmark rather than a BDN run.</summary>
    public static bool IsFixedWorkload(this BenchmarkKind kind) => kind != BenchmarkKind.Bdn;

    /// <summary>Whether the kind can run on the given canonical target.</summary>
    public static bool SupportsTarget(this BenchmarkKind kind, string canonicalTarget)
    {
        if (kind == BenchmarkKind.Bdn)
            return true;

        if (!TargetCatalog.TryGetTarget(canonicalTarget, out var target) || target is null)
            return false;

        if (target.Arch is not (VmArch.X64 or VmArch.Arm64))
            return false;

        return kind switch
        {
            BenchmarkKind.Orchard =>
                target.OsFamily.Equals("linux", StringComparison.OrdinalIgnoreCase)
                || target.OsFamily.Equals("osx", StringComparison.OrdinalIgnoreCase),
            BenchmarkKind.MinimalApi =>
                target.OsFamily.Equals("linux", StringComparison.OrdinalIgnoreCase)
                || target.OsFamily.Equals("windows", StringComparison.OrdinalIgnoreCase)
                || (target.OsFamily.Equals("osx", StringComparison.OrdinalIgnoreCase)
                    && target.Arch == VmArch.Arm64),
            _ => false,
        };
    }

    /// <summary>Human-readable list of targets a kind can run on (for error messages).</summary>
    public static string SupportedTargetsDescription(this BenchmarkKind kind) =>
        kind switch
        {
            BenchmarkKind.Orchard =>
                "Linux and macOS x64/arm64 targets (e.g. `-arm`, `-amd`, `-intel`, `-azure_cobalt100`)",
            BenchmarkKind.MinimalApi =>
                "Linux and Windows x64/arm64 targets, and macOS arm64 targets "
                + "(e.g. `-arm`, `-amd`, `-windows_x64`, `-windows_arm64`)",
            _ => "any target",
        };
}
