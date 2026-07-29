using EgorBot.Server.Services.CloudProviders;
using EgorBot.Shared;

namespace EgorBot.Server.Services;

/// <summary>
/// Sizes the core pools from the cloud provider's real vCPU quota instead of the
/// hand-maintained <see cref="TargetInfo.TotalCores"/> constants, which go stale silently
/// (a pool declaring 20 cores while Azure allows 64 just makes jobs queue for no reason,
/// and one declaring more than the quota fails at deployment time instead).
///
/// The catalog value stays as the fallback when a quota can't be read.
/// Disable with EgorBot:SyncQuotas=false.
/// </summary>
public sealed class QuotaSyncService(
    IEnumerable<ICloudProvider> providers,
    CorePoolManager corePool,
    IConfiguration config,
    ILogger<QuotaSyncService> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        config.GetValue("EgorBot:QuotaSyncMinutes", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("EgorBot:SyncQuotas", true))
        {
            logger.LogInformation("QuotaSyncService disabled (EgorBot:SyncQuotas=false)");
            return;
        }

        var quotaProviders = providers.OfType<ICoreQuotaProvider>().ToList();
        if (quotaProviders.Count == 0)
        {
            logger.LogInformation("QuotaSyncService: no provider reports quotas, using TargetCatalog values");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(quotaProviders, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "QuotaSyncService: sync failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncAsync(List<ICoreQuotaProvider> quotaProviders, CancellationToken ct)
    {
        foreach (var provider in quotaProviders)
        {
            // One target per pool is enough — targets sharing an instance family share a pool.
            var targets = TargetCatalog.GetAllTargetNames()
                .Select(TargetCatalog.GetTarget)
                .Where(t => t.CloudProvider.Equals(provider.Name, StringComparison.OrdinalIgnoreCase))
                .GroupBy(t => t.InstanceName ?? t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var quota = await provider.GetQuotaForPlatformAsync(target.Name, ct);
                    if (quota is null || quota.Limit <= 0)
                        continue;

                    corePool.SetCapacity(target.Name, quota.Limit,
                        $"{quota.Provider} quota '{quota.DisplayName}' in {quota.Region}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("QuotaSyncService: could not read quota for {Target}: {Reason}",
                        target.Name, ex.Message.Split('\n')[0].Trim());
                }
            }
        }
    }
}
