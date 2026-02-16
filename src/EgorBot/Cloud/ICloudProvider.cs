using EgorBot.Data;

namespace EgorBot.Cloud;

/// <summary>
/// Specification for a cloud machine to provision.
/// </summary>
public record CloudMachineSpec(
    TargetOs Os,
    TargetArch Arch,
    string HardwareProfile = "default",
    string? PreferredProvider = null
);

/// <summary>
/// Abstraction for a cloud provider that can provision machines, run scripts, and deallocate.
/// Implementations: Azure VMs, AWS EC2, Local Docker, Helix (future).
/// </summary>
public interface ICloudProvider
{
    string Name { get; }

    bool SupportsSpec(CloudMachineSpec spec);

    /// <summary>
    /// Provisions a machine with the given spec, injects the script as cloud-init (or equivalent),
    /// and returns a cloud-specific instance identifier.
    /// </summary>
    Task<string> ProvisionAsync(string subJobId, CloudMachineSpec spec, string script, CancellationToken ct = default);

    /// <summary>
    /// Deallocates/destroys the cloud instance.
    /// </summary>
    Task DeallocateAsync(string cloudInstanceId, CancellationToken ct = default);
}
