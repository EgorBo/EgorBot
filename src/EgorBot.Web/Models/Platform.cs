using System.Runtime.InteropServices;

namespace EgorBot.Web.Models;

/// <summary>
/// Describes a hardware target (cloud + CPU + architecture).
/// </summary>
public sealed record TargetInfo(
    string Name,
    string CloudProvider,
    string Arch,
    string DefaultOs,
    bool SupportsWindows,
    string? VmSizeTemplate,
    string? InstanceFamily,
    string? DefaultLocation,
    string? CpuName);

/// <summary>
/// Resolves target strings like "azure_genoa", "arm", "windows_aws_graviton4" into
/// concrete cloud-provider + VM-size + architecture info.
///
/// Stored platform format: target name, optionally prefixed with OS when non-default.
///   "azure_genoa"                → Linux (default) on Azure Genoa
///   "windows_azure_cobalt100"    → Windows on Azure Cobalt 100
///   "local"                      → Local machine
/// </summary>
public static class Platform
{
    // ── Target catalog ───────────────────────────────────────────────────

    private static readonly Dictionary<string, TargetInfo> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        // Azure x64
        ["azure_genoa"]       = new("azure_genoa",       "Azure", "x64",   "linux", false, "Standard_D{0}ads_v6", null, "eastus",      "AMD EPYC 9V74"),
        ["azure_genoasmt1"]   = new("azure_genoasmt1",   "Azure", "x64",   "linux", false, "Standard_F{0}ams_v6", null, "eastus",      "AMD EPYC 9V74 SMT1"),
        ["azure_milano"]      = new("azure_milano",      "Azure", "x64",   "linux", true,  "Standard_D{0}ads_v5", null, "westeurope",  "AMD EPYC 7763"),
        ["azure_cascadelake"] = new("azure_cascadelake",  "Azure", "x64",   "linux", true,  "Standard_D{0}ds_v5",  null, "westeurope",  "Intel Cascade Lake"),

        // Azure arm64
        ["azure_cobalt100"]   = new("azure_cobalt100",   "Azure", "arm64", "linux", true,  "Standard_D{0}pds_v6", null, "eastus",      "Cobalt 100 (Neoverse-N2)"),
        ["azure_ampere"]      = new("azure_ampere",      "Azure", "arm64", "linux", true,  "Standard_D{0}pds_v5", null, "eastus",      "Neoverse-N1"),

        // AWS x64
        ["aws_sapphirelake"]  = new("aws_sapphirelake",  "AWS",   "x64",   "linux", false, null, "c7i", null, "Intel Sapphire Lake"),
        ["aws_icelake"]       = new("aws_icelake",       "AWS",   "x64",   "linux", false, null, "c6i", null, "Intel Ice Lake"),
        ["aws_genoa"]         = new("aws_genoa",         "AWS",   "x64",   "linux", false, null, "c7a", null, "AMD EPYC 9R14"),
        ["aws_turin"]         = new("aws_turin",         "AWS",   "x64",   "linux", false, null, "m8a", null, "AMD EPYC 9R45"),
        ["aws_milano"]        = new("aws_milano",        "AWS",   "x64",   "linux", false, null, "c6a", null, "AMD EPYC Milan"),

        // AWS arm64
        ["aws_graviton2"]     = new("aws_graviton2",     "AWS",   "arm64", "linux", false, null, "c6g", null, "Graviton2 (Neoverse-N1)"),
        ["aws_graviton3"]     = new("aws_graviton3",     "AWS",   "arm64", "linux", false, null, "c7g", null, "Graviton3 (Neoverse-V1)"),
        ["aws_graviton4"]     = new("aws_graviton4",     "AWS",   "arm64", "linux", false, null, "c8g", null, "Graviton4 (Neoverse-V2)"),

        // Local (testing)
        ["local"]             = new("local",             "Local", DetectLocalArch(), DetectLocalOs(), false, null, null, null, "Local machine"),
    };

    // ── Aliases ──────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arm"]       = "azure_cobalt100",
        ["intel"]     = "azure_cascadelake",
        ["x64"]       = "azure_genoa",
        ["amd"]       = "azure_genoa",
        ["aws_arm"]   = "aws_graviton4",
        ["aws_amd"]   = "aws_genoa",
        ["aws_intel"] = "aws_sapphirelake",
        ["azure_arm"] = "azure_cobalt100",
        ["azure_x64"] = "azure_genoa",
    };

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

        if (!Targets.TryGetValue(targetName, out var target))
            throw new ArgumentException(
                $"Unknown target: '{targetName}'. Valid targets: {string.Join(", ", GetAllTargetNames())}");

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
        if (Targets.TryGetValue(targetName, out var info))
            return info;
        throw new ArgumentException($"Unknown target: '{targetName}'.");
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
        if (Targets.TryGetValue(targetName, out var target))
            return os == "linux" || os == "windows" || os == "osx" ? os : target.DefaultOs;
        return os;
    }

    /// <summary>All canonical target names (no aliases).</summary>
    public static IEnumerable<string> GetAllTargetNames() => Targets.Keys;

    /// <summary>All aliases.</summary>
    public static IReadOnlyDictionary<string, string> GetAliases() => Aliases;

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
        if (Aliases.TryGetValue(rest, out var resolved))
            rest = resolved;

        // Special-case "local": use detected OS
        if (rest.Equals("local", StringComparison.OrdinalIgnoreCase) && os == "linux")
            os = DetectLocalOs();

        return (os, rest);
    }

    private static string DetectLocalOs() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    private static string DetectLocalArch() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
}
