using EgorBot.Shared;

namespace EgorBot.Server.Models;

/// <summary>
/// Resolves target strings like "azure_genoa", "arm", "windows_aws_graviton4" into
/// concrete cloud-provider + VM-size + architecture info.
///
/// Delegates to <see cref="TargetCatalog"/> for target definitions, aliases, and OS prefixes.
///
/// Stored platform format: target name, optionally prefixed with OS when non-default.
///   "azure_genoa"                → Linux (default) on Azure Genoa
///   "windows_azure_cobalt100"    → Windows on Azure Cobalt 100
///   "local"                      → Local machine
/// </summary>
public static class Platform
{
    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Normalize a user-facing target string into the canonical stored form.
    /// Resolves aliases and optional OS prefix.
    /// Examples: "arm" → "azure_cobalt100", "windows_arm" → "windows_azure_cobalt100",
    ///           "-aws_graviton4" → "aws_graviton4"
    /// </summary>
    public static string Normalize(string input)
    {
        var trimmed = input.TrimStart('-').Trim();
        var (os, targetName) = Parse(trimmed);

        var target = TargetCatalog.GetTarget(targetName);

        if (os == "windows" && !target.SupportsWindows)
            throw new ArgumentException($"Target '{targetName}' does not support Windows.");

        // Only include OS prefix when it differs from the target's default
        return os != target.DefaultOs ? $"{os}_{targetName}" : targetName;
    }

    /// <summary>
    /// Check whether a raw input string (possibly with OS prefix / alias) is a valid target.
    /// </summary>
    public static bool IsValid(string input)
    {
        try
        {
            Normalize(input);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Get the <see cref="TargetInfo"/> for a normalized platform string.</summary>
    public static TargetInfo Resolve(string platform)
    {
        var (_, targetName) = Parse(platform);
        return TargetCatalog.GetTarget(targetName);
    }

    public static bool IsLocal(string platform) =>
        Parse(platform).TargetName.Equals("local", StringComparison.OrdinalIgnoreCase);

    public static bool IsWindows(string platform) =>
        GetOs(platform).Equals("windows", StringComparison.OrdinalIgnoreCase);

    public static bool IsLinux(string platform) =>
        GetOs(platform).Equals("linux", StringComparison.OrdinalIgnoreCase);

    public static string GetArch(string platform) =>
        Resolve(platform).Arch;

    public static string GetOs(string platform)
    {
        var (os, targetName) = Parse(platform);
        if (TargetCatalog.TryGetTarget(targetName, out var target))
            return os is "linux" or "windows" or "osx" ? os : target!.DefaultOs;
        return os;
    }

    /// <summary>All canonical target names (no aliases).</summary>
    public static IEnumerable<string> GetAllTargetNames() => TargetCatalog.GetAllTargetNames();

    /// <summary>All aliases.</summary>
    public static IReadOnlyDictionary<string, string> GetAliases() => TargetCatalog.GetAliases();

    // ── Internals ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a platform string into (os, targetName), handling optional OS prefix and aliases.
    /// </summary>
    private static (string Os, string TargetName) Parse(string platform)
    {
        string os;
        string rest;

        if (platform.StartsWith("windows_", StringComparison.OrdinalIgnoreCase))
        {
            os = "windows";
            rest = platform[8..];
        }
        else if (platform.StartsWith("linux_", StringComparison.OrdinalIgnoreCase))
        {
            os = "linux";
            rest = platform[6..];
        }
        else
        {
            os = "linux"; // default; overridden below for "local"
            rest = platform;
        }

        // Resolve alias
        rest = TargetCatalog.ResolveAlias(rest);

        // Special-case "local": use detected OS
        if (rest.Equals("local", StringComparison.OrdinalIgnoreCase) && os == "linux")
        {
            if (TargetCatalog.TryGetTarget("local", out var localTarget))
                os = localTarget!.DefaultOs;
        }

        return (os, rest);
    }
}
