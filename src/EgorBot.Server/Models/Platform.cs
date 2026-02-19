using EgorBot.Shared;

namespace EgorBot.Server.Models;

/// <summary>
/// Resolves target strings into concrete cloud-provider + VM-size + architecture info.
/// Delegates to <see cref="TargetCatalog"/> for target definitions and resolution.
///
/// Stored platform format: canonical target name, e.g.
///   "ubuntu24_azure_genoa"       → Linux on Azure Genoa
///   "windows_azure_cascadelake"  → Windows on Azure Cascade Lake
///   "macos26_helix_arm64"        → macOS on Helix ARM64
///   "local"                      → Local machine
/// </summary>
public static class Platform
{
    /// <summary>
    /// Normalize a user-facing target string into the canonical stored form.
    /// Resolves shorthands like "arm", "genoa", "aws_graviton4", "windows_cascadelake".
    /// </summary>
    public static string Normalize(string input) => TargetCatalog.Resolve(input);

    /// <summary>
    /// Check whether a raw input string is a valid / resolvable target.
    /// </summary>
    public static bool IsValid(string input) => TargetCatalog.TryResolve(input, out _);

    /// <summary>Get the <see cref="TargetInfo"/> for a stored platform string.</summary>
    public static TargetInfo Resolve(string platform) => TargetCatalog.GetTarget(platform);

    public static bool IsLocal(string platform) =>
        platform.StartsWith("local", StringComparison.OrdinalIgnoreCase);

    public static bool IsWindows(string platform) =>
        TargetCatalog.GetTarget(platform).OsFamily == "windows";

    public static bool IsLinux(string platform) =>
        TargetCatalog.GetTarget(platform).OsFamily == "linux";

    public static string GetArch(string platform) => TargetCatalog.GetTarget(platform).Arch switch
    {
        VmArch.X64 => "x64",
        VmArch.Arm64 => "arm64",
        VmArch.Arm32 => "arm32",
        _ => "x64"
    };

    public static string GetOs(string platform) =>
        TargetCatalog.GetTarget(platform).OsFamily;

    /// <summary>All canonical target names.</summary>
    public static IEnumerable<string> GetAllTargetNames() => TargetCatalog.GetAllTargetNames();
}
