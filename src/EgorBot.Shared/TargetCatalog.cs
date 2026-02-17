using System.Runtime.InteropServices;

namespace EgorBot.Shared;

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
/// Single source of truth for all supported target names, aliases, and OS prefixes.
/// Referenced by both EgorBot.Server (cloud provisioning) and EgorBot.Github (command parsing).
/// </summary>
public static class TargetCatalog
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

        // Helix — managed infrastructure, VmSizeTemplate stores the Helix queue ID
        // macOS
        ["helix_osx_arm64"]     = new("helix_osx_arm64",     "Helix", "arm64", "osx",     false, "osx.26.arm64.open",           null, null, "Apple Silicon (macOS 15)"),
        ["helix_osx_x64"]       = new("helix_osx_x64",       "Helix", "x64",   "osx",     false, "OSX.15.Amd64.Open",           null, null, "Intel Mac (macOS 15)"),
        // Linux
        ["helix_linux_x64"]     = new("helix_linux_x64",     "Helix", "x64",   "linux",   false, "(Ubuntu.2604.Amd64.Open)AzureLinux.3.Amd64.Open@mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-26.04-helix-amd64",     null, null, "Azure Linux 3 x64"),
        ["helix_linux_arm64"]   = new("helix_linux_arm64",    "Helix", "arm64", "linux",   false, "(Ubuntu.2404.Arm64.Open)Ubuntu.2204.Armarch.Open@mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-24.04-helix-arm64v8", null, null, "Ubuntu 24.04 ARM64 (container)"),
        ["helix_linux_arm32"]   = new("helix_linux_arm32",    "Helix", "arm32", "linux",   false, "(Debian.12.Arm32.Open)Ubuntu.2204.ArmArch.Open@mcr.microsoft.com/dotnet-buildtools/prereqs:debian-12-helix-arm32v7",       null, null, "Debian 12 ARM32 (container)"),
        // Windows
        ["helix_windows_x64"]   = new("helix_windows_x64",    "Helix", "x64",   "windows", true, "windows.amd64.vs2022.pre.open",       null, null, "Windows 11 x64"),
        ["helix_windows_arm64"] = new("helix_windows_arm64",  "Helix", "arm64", "windows", true,  "Windows.11.Arm64.Open",       null, null, "Windows 11 ARM64"),

        // Local (testing) — supports any OS
        ["local"]             = new("local",             "Local", DetectLocalArch(), DetectLocalOs(), true,  null, null, null, "Local machine"),
    };

    // ── Aliases ──────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Short-hand → canonical target name (default to AWS linux)
        ["arm"]       = "aws_graviton4",
        ["arm64"]     = "aws_graviton4",
        ["intel"]     = "aws_sapphirelake",
        ["x64"]       = "aws_sapphirelake",
        ["amd"]       = "aws_genoa",

        // CPU-specific shortcuts
        ["cobalt"]      = "azure_cobalt100",
        ["cobalt100"]   = "azure_cobalt100",
        ["ampere"]      = "azure_ampere",
        ["cascadelake"] = "azure_cascadelake",
        ["genoa"]       = "azure_genoa",
        ["milano"]      = "azure_milano",
        ["graviton2"]   = "aws_graviton2",
        ["graviton3"]   = "aws_graviton3",
        ["graviton4"]   = "aws_graviton4",
        ["sapphirelake"] = "aws_sapphirelake",
        ["icelake"]     = "aws_icelake",
        ["turin"]       = "aws_turin",

        // Cloud-vendor shortcuts

        // AWS shortcuts
        ["aws_arm"]   = "aws_graviton4",
        ["aws_arm64"] = "aws_graviton4",
        ["aws_x64"]   = "aws_sapphirelake",
        ["aws_amd"]   = "aws_genoa",
        ["aws_intel"] = "aws_sapphirelake",

        // Azure shortcuts
        ["azure_arm"] = "azure_cobalt100",
        ["azure_x64"] = "azure_genoa",
        ["azure_intel"] = "azure_cascadelake",
        ["azure_amd"] = "azure_genoa",

        // Local shortcuts
        ["local_x64"]   = "local",
        ["local_arm64"] = "local",

        // OSX shortcuts (Helix)
        ["osx"]         = "helix_osx_arm64",
        ["osx_arm64"]   = "helix_osx_arm64",
        ["osx_x64"]     = "helix_osx_x64",

        // Windows shortcuts (Helix)
        ["windows_x64"]   = "helix_windows_x64",
        ["windows_arm64"] = "helix_windows_arm64",
        ["windows_arm"]   = "helix_windows_arm64",
        ["windows_intel"] = "helix_windows_x64",
        ["windows_amd"]   = "helix_windows_x64",

        // Helix shortcuts
        ["helix_arm64"] = "helix_linux_arm64",
        ["helix_arm"]   = "helix_linux_arm64",
        ["helix_x64"]   = "helix_linux_x64",
        ["helix_arm32"] = "helix_linux_arm32",
        ["helix_win_x64"]   = "helix_windows_x64",
        ["helix_win_arm64"] = "helix_windows_arm64",
    };

    // ── OS prefixes ─────────────────────────────────────────────────────

    private static readonly HashSet<string> OsPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "linux", "windows", "osx",
    };

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>All canonical target names (no aliases).</summary>
    public static IEnumerable<string> GetAllTargetNames() => Targets.Keys;

    /// <summary>All aliases.</summary>
    public static IReadOnlyDictionary<string, string> GetAliases() => Aliases;

    /// <summary>All recognized OS prefixes.</summary>
    public static IReadOnlySet<string> GetOsPrefixes() => OsPrefixes;

    /// <summary>Try to get a <see cref="TargetInfo"/> by canonical name.</summary>
    public static bool TryGetTarget(string canonicalName, out TargetInfo? info) =>
        Targets.TryGetValue(canonicalName, out info);

    /// <summary>Get the <see cref="TargetInfo"/> for a canonical target name. Throws if not found.</summary>
    public static TargetInfo GetTarget(string canonicalName)
    {
        if (Targets.TryGetValue(canonicalName, out var info))
            return info;
        throw new ArgumentException(
            $"Unknown target: '{canonicalName}'. Valid targets: {string.Join(", ", GetAllTargetNames())}");
    }

    /// <summary>
    /// Resolve an alias (e.g. "arm", "x64", "graviton3") to its canonical target name.
    /// Returns the input unchanged if it's already canonical or unrecognized.
    /// </summary>
    public static string ResolveAlias(string name) =>
        Aliases.TryGetValue(name, out var canonical) ? canonical : name;

    /// <summary>
    /// Check whether <paramref name="name"/> is a known target — either canonical or alias,
    /// with or without an OS prefix (e.g. "windows_arm").
    /// </summary>
    public static bool IsKnownTarget(string name)
    {
        var stripped = StripOsPrefix(name);
        var resolved = ResolveAlias(stripped);
        return Targets.ContainsKey(resolved);
    }

    /// <summary>
    /// Strip a leading OS prefix (e.g. "linux_arm" → "arm", "windows_intel" → "intel").
    /// Returns the input unchanged if there is no recognized OS prefix.
    /// </summary>
    public static string StripOsPrefix(string name)
    {
        var underscoreIdx = name.IndexOf('_');
        if (underscoreIdx < 0) return name;

        var prefix = name[..underscoreIdx];
        if (OsPrefixes.Contains(prefix))
            return name[(underscoreIdx + 1)..];
        return name;
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private static string DetectLocalOs() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    private static string DetectLocalArch() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
}
