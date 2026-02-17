using EgorBot.Server.Models;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Abstraction over cloud VM provisioning. Implementations exist for Azure, AWS, and local process execution.
/// </summary>
public interface ICloudProvider
{
    /// <summary>Human-readable name, e.g. "Azure", "AWS", "Local".</summary>
    string Name { get; }

    /// <summary>Provision a VM (or local process) and return its instance identifier.</summary>
    Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default);

    /// <summary>Tear down the VM / kill the process.</summary>
    Task DeprovisionAsync(string instanceId, CancellationToken ct = default);
}

public sealed record ProvisionRequest(
    string JobId,
    string CloudInitScript,
    string Platform,
    BenchmarkJob? Job = null,
    int Cores = 8,
    int DiskSizeGb = 64);

public sealed record ProvisionResult(
    string InstanceId,
    string? IpAddress = null);
