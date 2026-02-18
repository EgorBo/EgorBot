using System.Runtime.InteropServices;

namespace EgorBot.Shared;

public enum VmArch { X64, Arm64, Arm32 }

public enum VmCpuVendor { Amd, Intel, Arm }

/// <summary>
/// Describes a hardware target (OS + cloud + CPU).
/// Target names follow the convention {OsDistro}_{Cloud}_{Cpu}, e.g. "ubuntu24_azure_genoa".
/// OS family and cloud provider are inferred from the name.
/// </summary>
public sealed record TargetInfo(
    string Name,
    VmArch Arch,
    string? InstanceName,
    string? Region,
    VmCpuVendor CpuVendor,
    bool PreferredDefault)
{
    /// <summary>OS family derived from the target name: "linux", "windows", or "osx".</summary>
    public string OsFamily => TargetCatalog.InferOsFamily(Name);

    /// <summary>Cloud provider derived from the target name: "Azure", "AWS", "Helix", or "Local".</summary>
    public string CloudProvider => TargetCatalog.InferCloudProvider(Name);
}

/// <summary>
/// Single source of truth for all supported targets, with smart resolution from user input.
///
/// Target names: {OsDistro}_{Cloud}_{Cpu}
///   - OsDistro: ubuntu24, macos26, windows
///   - Cloud:    azure, aws, helix
///   - Cpu:      genoa, cascadelake, graviton4, arm64, x64, etc.
///
/// Resolution from user input:
///   1. Exact match
///   2. Parse segments, normalize OS (linux→ubuntu24, osx→macos26, etc.), fill defaults
///   3. Try full {os}_{cloud}_{cpu} match
///   4. CPU as vendor shorthand (amd, intel, arm, x64, arm64) → find preferred default
///   5. Search by CPU suffix across all targets
///   6. OS-only → find preferred default for that OS
/// </summary>
public static class TargetCatalog
{
    // ── Helix queue IDs (long container-image strings) ─────────────────

    private const string HelixQueueLinuxX64   = "(Ubuntu.2604.Amd64.Open)AzureLinux.3.Amd64.Open@mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-26.04-helix-amd64";
    private const string HelixQueueLinuxArm64 = "(Ubuntu.2404.Arm64.Open)Ubuntu.2204.Armarch.Open@mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-24.04-helix-arm64v8";
    private const string HelixQueueLinuxArm32 = "(Debian.12.Arm32.Open)Ubuntu.2204.ArmArch.Open@mcr.microsoft.com/dotnet-buildtools/prereqs:debian-12-helix-arm32v7";

    // ── Target catalog ───────────────────────────────────────────────────

    private static readonly Dictionary<string, TargetInfo> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        //                                                                    Arch          InstanceName                   Region        CpuVendor          Default
        // ── Azure ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        ["ubuntu24_azure_genoa"]       = new("ubuntu24_azure_genoa",          VmArch.X64,   "Standard_D{0}ads_v6",         "eastus",     VmCpuVendor.Amd,   true),
        ["ubuntu24_azure_milano"]      = new("ubuntu24_azure_milano",         VmArch.X64,   "Standard_D{0}ads_v5",         "westeurope", VmCpuVendor.Amd,   false),
        ["ubuntu24_azure_cascadelake"] = new("ubuntu24_azure_cascadelake",    VmArch.X64,   "Standard_D{0}ds_v5",          "westeurope", VmCpuVendor.Intel, true),
        ["ubuntu24_azure_cobalt100"]   = new("ubuntu24_azure_cobalt100",      VmArch.Arm64, "Standard_D{0}pds_v6",         "eastus",     VmCpuVendor.Arm,   true),
        ["ubuntu24_azure_ampere"]      = new("ubuntu24_azure_ampere",         VmArch.Arm64, "Standard_D{0}pds_v5",         "eastus",     VmCpuVendor.Arm,   false),
        ["windows_azure_cascadelake"]  = new("windows_azure_cascadelake",     VmArch.X64,   "Standard_D{0}ds_v5",          "westeurope", VmCpuVendor.Intel, true),
        ["windows_azure_genoa"]        = new("windows_azure_genoa",           VmArch.X64,   "Standard_D{0}ds_v5",          "westeurope", VmCpuVendor.Amd,   true),

        // ── AWS ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        ["ubuntu24_aws_sapphirelake"]  = new("ubuntu24_aws_sapphirelake",     VmArch.X64,   "c7i",                         "us-east-1",  VmCpuVendor.Intel, false),
        ["ubuntu24_aws_icelake"]       = new("ubuntu24_aws_icelake",          VmArch.X64,   "c6i",                         "us-east-1",  VmCpuVendor.Intel, true),
        ["ubuntu24_aws_genoa"]         = new("ubuntu24_aws_genoa",            VmArch.X64,   "c7a",                         "us-east-1",  VmCpuVendor.Amd,   true),
        ["ubuntu24_aws_turin"]         = new("ubuntu24_aws_turin",            VmArch.X64,   "m8a",                         "us-east-1",  VmCpuVendor.Amd,   false),
        ["ubuntu24_aws_milano"]        = new("ubuntu24_aws_milano",           VmArch.X64,   "c6a",                         "us-east-1",  VmCpuVendor.Amd,   false),
        ["ubuntu24_aws_graviton2"]     = new("ubuntu24_aws_graviton2",        VmArch.Arm64, "c6g",                         "us-east-1",  VmCpuVendor.Arm,   false),
        ["ubuntu24_aws_graviton3"]     = new("ubuntu24_aws_graviton3",        VmArch.Arm64, "c7g",                         "us-east-1",  VmCpuVendor.Arm,   false),
        ["ubuntu24_aws_graviton4"]     = new("ubuntu24_aws_graviton4",        VmArch.Arm64, "c8g",                         "us-east-1",  VmCpuVendor.Arm,   true),

        // ── Helix ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        ["macos26_helix_arm64"]        = new("macos26_helix_arm64",           VmArch.Arm64, "osx.26.arm64.open",           null,         VmCpuVendor.Arm,   true),
        ["macos26_helix_x64"]          = new("macos26_helix_x64",             VmArch.X64,   "OSX.15.Amd64.Open",           null,         VmCpuVendor.Intel, true),
        ["ubuntu24_helix_x64"]         = new("ubuntu24_helix_x64",            VmArch.X64,   HelixQueueLinuxX64,            null,         VmCpuVendor.Amd,   false),
        ["ubuntu24_helix_arm64"]       = new("ubuntu24_helix_arm64",          VmArch.Arm64, HelixQueueLinuxArm64,          null,         VmCpuVendor.Arm,   false),
        ["ubuntu24_helix_arm32"]       = new("ubuntu24_helix_arm32",          VmArch.Arm32, HelixQueueLinuxArm32,          null,         VmCpuVendor.Arm,   true),
        ["windows_helix_x64"]          = new("windows_helix_x64",             VmArch.X64,   "windows.amd64.vs2022.pre.open", null,       VmCpuVendor.Intel, false),
        ["windows_helix_arm64"]        = new("windows_helix_arm64",           VmArch.Arm64, "Windows.11.Arm64.Open",       null,         VmCpuVendor.Arm,   false),

        // ── Local (testing) ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        ["local"]                      = new("local",                         DetectLocalArch(), null,                      null,         DetectLocalCpuVendor(), false),
    };

    // ── OS distro → OS family ────────────────────────────────────────────

    private static readonly Dictionary<string, string> OsDistroToFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ubuntu24"] = "linux",
        ["macos26"]  = "osx",
        ["windows"]  = "windows",
    };

    // ── OS input normalization ───────────────────────────────────────────

    private static readonly Dictionary<string, string> OsNormalization = new(StringComparer.OrdinalIgnoreCase)
    {
        ["linux"]  = "ubuntu24",
        ["ubuntu"] = "ubuntu24",
        ["osx"]    = "macos26",
        ["macos"]  = "macos26",
        ["win"]    = "windows",
    };

    // ── Known cloud identifiers ──────────────────────────────────────────

    private static readonly HashSet<string> KnownClouds = new(StringComparer.OrdinalIgnoreCase)
    {
        "azure", "aws", "helix", "local"
    };

    // ── CPU → vendor shorthands ──────────────────────────────────────────

    private static readonly Dictionary<string, VmCpuVendor> VendorShorthands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amd"]   = VmCpuVendor.Amd,
        ["x64"]   = VmCpuVendor.Amd,
        ["intel"] = VmCpuVendor.Intel,
        ["arm"]   = VmCpuVendor.Arm,
        ["arm64"] = VmCpuVendor.Arm,
        ["arm32"] = VmCpuVendor.Arm,
    };

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>All canonical target names.</summary>
    public static IEnumerable<string> GetAllTargetNames() => Targets.Keys;

    /// <summary>Try to get a <see cref="TargetInfo"/> by exact canonical name.</summary>
    public static bool TryGetTarget(string name, out TargetInfo? info) =>
        Targets.TryGetValue(name, out info);

    /// <summary>Get the <see cref="TargetInfo"/> by exact canonical name. Throws if not found.</summary>
    public static TargetInfo GetTarget(string name)
    {
        if (Targets.TryGetValue(name, out var info)) return info;
        throw new ArgumentException(
            $"Unknown target: '{name}'. Valid targets: {string.Join(", ", GetAllTargetNames())}");
    }

    /// <summary>
    /// Resolve user input (e.g. "arm", "genoa", "aws_graviton4", "windows_cascadelake")
    /// to a canonical target name.
    /// </summary>
    public static string Resolve(string input) =>
        TryResolve(input, out var name)
            ? name!
            : throw new ArgumentException(
                $"Cannot resolve target: '{input}'. Valid targets: {string.Join(", ", GetAllTargetNames())}");

    /// <summary>
    /// Try to resolve user input to a canonical target name.
    /// </summary>
    public static bool TryResolve(string input, out string? canonicalName)
    {
        canonicalName = null;
        var clean = input.ToLowerInvariant().TrimStart('-').Trim();
        if (string.IsNullOrEmpty(clean)) return false;

        // 1. Exact match (handles full canonical names like "ubuntu24_azure_genoa")
        if (Targets.TryGetValue(clean, out var exact))
        {
            canonicalName = exact.Name;
            return true;
        }

        // 2. Parse into (os?, cloud?, cpu?) with normalization
        var (userOs, userCloud, cpu) = ParseSegments(clean);

        // 3. Apply defaults: OS → ubuntu24, Cloud → azure (macos26 → helix)
        var os = userOs ?? "ubuntu24";
        var cloud = userCloud ?? (os == "macos26" ? "helix" : "azure");

        // 4. Try full {os}_{cloud}_{cpu} match
        if (cpu != null)
        {
            var fullName = $"{os}_{cloud}_{cpu}";
            if (Targets.TryGetValue(fullName, out var t))
            {
                canonicalName = t.Name;
                return true;
            }
        }

        // 5. CPU as vendor shorthand → find preferred default
        if (cpu != null && VendorShorthands.TryGetValue(cpu, out var vendor))
        {
            var osFamily = OsDistroToFamily.GetValueOrDefault(os, "linux");
            var match = FindPreferredByVendor(vendor, osFamily, userCloud);
            if (match != null)
            {
                canonicalName = match.Name;
                return true;
            }
        }

        // 6. Search by CPU suffix across all targets (e.g. "graviton4" → ubuntu24_aws_graviton4)
        if (cpu != null)
        {
            var suffix = "_" + cpu;
            var matches = Targets.Values
                .Where(t => t.Name != "local" && t.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
            {
                var osFamily = OsDistroToFamily.GetValueOrDefault(os, "linux");
                var best = matches.FirstOrDefault(t => t.OsFamily.Equals(osFamily, StringComparison.OrdinalIgnoreCase))
                           ?? matches[0];
                canonicalName = best.Name;
                return true;
            }
        }

        // 7. OS-only (no CPU specified) → find preferred default for that OS
        if (cpu == null)
        {
            var osFamily = OsDistroToFamily.GetValueOrDefault(os, "linux");
            var candidates = Targets.Values
                .Where(t => t.Name != "local")
                .Where(t => t.OsFamily.Equals(osFamily, StringComparison.OrdinalIgnoreCase))
                .Where(t => t.PreferredDefault);

            if (userCloud != null)
            {
                var withCloud = candidates
                    .Where(t => ExtractCloudSegment(t.Name).Equals(userCloud, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (withCloud.Count > 0)
                {
                    canonicalName = withCloud[0].Name;
                    return true;
                }
            }

            var any = candidates.ToList();
            if (any.Count > 0)
            {
                canonicalName = any[0].Name;
                return true;
            }
        }

        return false;
    }

    // ── Inference helpers (used by TargetInfo computed properties) ────────

    internal static string InferOsFamily(string targetName)
    {
        if (targetName.Equals("local", StringComparison.OrdinalIgnoreCase))
            return DetectLocalOs();
        var firstSeg = targetName.Split('_')[0];
        return OsDistroToFamily.GetValueOrDefault(firstSeg, "linux");
    }

    internal static string InferCloudProvider(string targetName)
    {
        if (targetName.Equals("local", StringComparison.OrdinalIgnoreCase))
            return "Local";
        var cloudSeg = ExtractCloudSegment(targetName);
        return cloudSeg.ToLowerInvariant() switch
        {
            "azure" => "Azure",
            "aws"   => "AWS",
            "helix" => "Helix",
            "local" => "Local",
            _ => throw new InvalidOperationException($"Unknown cloud in target name: '{targetName}'")
        };
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static string ExtractCloudSegment(string targetName)
    {
        var parts = targetName.Split('_');
        return parts.Length >= 2 ? parts[1] : targetName;
    }

    private static (string? Os, string? Cloud, string? Cpu) ParseSegments(string input)
    {
        var parts = input.Split('_');
        int idx = 0;
        string? os = null, cloud = null, cpu = null;

        if (idx < parts.Length && IsOsToken(parts[idx]))
        {
            os = NormalizeOs(parts[idx]);
            idx++;
        }

        if (idx < parts.Length && KnownClouds.Contains(parts[idx]))
        {
            cloud = parts[idx];
            idx++;
        }

        if (idx < parts.Length)
        {
            cpu = string.Join("_", parts[idx..]);
        }

        return (os, cloud, cpu);
    }

    private static bool IsOsToken(string s) =>
        OsDistroToFamily.ContainsKey(s) || OsNormalization.ContainsKey(s);

    private static string NormalizeOs(string s) =>
        OsNormalization.TryGetValue(s, out var n) ? n : s;

    private static TargetInfo? FindPreferredByVendor(VmCpuVendor vendor, string osFamily, string? cloudHint)
    {
        var candidates = Targets.Values
            .Where(t => t.Name != "local")
            .Where(t => t.CpuVendor == vendor)
            .Where(t => t.OsFamily.Equals(osFamily, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.PreferredDefault)
            .ToList();

        if (candidates.Count == 0)
        {
            // Relax OS constraint
            candidates = Targets.Values
                .Where(t => t.Name != "local")
                .Where(t => t.CpuVendor == vendor)
                .Where(t => t.PreferredDefault)
                .ToList();
        }

        if (candidates.Count == 0) return null;

        if (cloudHint != null)
        {
            var withCloud = candidates.FirstOrDefault(t =>
                ExtractCloudSegment(t.Name).Equals(cloudHint, StringComparison.OrdinalIgnoreCase));
            if (withCloud != null) return withCloud;
        }

        return candidates[0];
    }

    private static string DetectLocalOs() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    private static VmArch DetectLocalArch() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? VmArch.Arm64 : VmArch.X64;

    private static VmCpuVendor DetectLocalCpuVendor() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? VmCpuVendor.Arm : VmCpuVendor.Amd;
}
