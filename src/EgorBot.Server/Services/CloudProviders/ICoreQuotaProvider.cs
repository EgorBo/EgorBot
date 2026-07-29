namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Live vCPU quota for one instance family in one region, as reported by the cloud provider.
/// </summary>
/// <param name="Provider">Cloud provider name, e.g. "Azure".</param>
/// <param name="Region">Region the quota applies to, e.g. "eastus".</param>
/// <param name="Family">Provider-specific quota family, e.g. "standardDADSv6Family".</param>
/// <param name="DisplayName">Human-readable family name, e.g. "Standard DADSv6 Family vCPUs".</param>
/// <param name="Used">vCPUs currently in use.</param>
/// <param name="Limit">vCPU limit.</param>
public sealed record CoreQuota(
    string Provider,
    string Region,
    string Family,
    string DisplayName,
    int Used,
    int Limit)
{
    public int Available => Math.Max(0, Limit - Used);
}

/// <summary>
/// Implemented by cloud providers that can report their real vCPU quotas, so the bot can size
/// its core pools from what the cloud actually allows instead of hand-maintained constants.
/// </summary>
public interface ICoreQuotaProvider
{
    /// <summary>Provider name (matches <see cref="ICloudProvider.Name"/>).</summary>
    string Name { get; }

    /// <summary>All vCPU quotas relevant to the targets this provider serves.</summary>
    Task<IReadOnlyList<CoreQuota>> GetQuotasAsync(CancellationToken ct = default);

    /// <summary>Quota backing a specific target platform, or null when it cannot be determined.</summary>
    Task<CoreQuota?> GetQuotaForPlatformAsync(string platform, CancellationToken ct = default);
}
