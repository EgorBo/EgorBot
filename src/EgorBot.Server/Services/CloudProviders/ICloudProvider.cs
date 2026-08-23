using EgorBot.Server.Models;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Abstraction over cloud VM provisioning. Implementations exist for Azure, AWS, and local process execution.
/// </summary>
public interface ICloudProvider
{
    /// <summary>Human-readable name, e.g. "Azure", "AWS", "Docker".</summary>
    string Name { get; }

    /// <summary>Provision a VM (or local process) and return its instance identifier.</summary>
    Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Tear down the VM / kill the process. Completes only after the resource no longer
    /// consumes provisioning quota, and throws when teardown cannot be confirmed.
    /// </summary>
    Task DeprovisionAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// Reconcile a provisioning attempt whose provider instance ID was not persisted
    /// before a service restart. Returns true only when absence or cleanup is confirmed.
    /// </summary>
    Task<bool> TryDeprovisionByJobIdAsync(string jobId, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// List the names/identifiers of all currently active VMs in this provider.
    /// Returns an empty list for providers that don't manage VMs (e.g. Docker, Helix).
    /// </summary>
    Task<IReadOnlyList<string>> ListActiveVmsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
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

/// <summary>
/// Provisioning failed after a resource was created, and the provider could not
/// confirm cleanup. The orchestrator must retry teardown and retain quota meanwhile.
/// </summary>
public sealed class ProvisioningCleanupException(
    string instanceId,
    Exception provisioningError,
    Exception cleanupError)
    : Exception(
        $"Provisioning failed and cleanup of resource '{instanceId}' could not be confirmed.",
        new AggregateException(provisioningError, cleanupError))
{
    public string InstanceId { get; } = instanceId;
}
