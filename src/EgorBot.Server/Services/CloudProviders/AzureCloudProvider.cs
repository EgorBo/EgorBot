using System.Text;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using EgorBot.Shared;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Azure VM provisioning via ARM template deployment.
/// Creates a dedicated resource group per job, deploys a VM using an ARM template from a gist URL,
/// passes cloud-init as base64-encoded customData, then discovers the public IP.
/// Deprovisioning deletes the entire resource group.
/// </summary>
public sealed class AzureCloudProvider(IConfiguration config, ILogger<AzureCloudProvider> logger)
    : ICloudProvider, ICoreQuotaProvider
{
    private readonly SemaphoreSlim _semaphore = new(3, 3);

    public string Name => "Azure";

    // ── Default Ubuntu image ─────────────────────────────────────────────
    private const string DefaultOffer = "ubuntu-24_04-lts";
    private const string DefaultSkuX64  = "server";
    private const string DefaultSkuArm64 = "server-arm64";

    // ── Default Windows Server image ─────────────────────────────────────
    private const string WindowsOffer = "WindowsServer";
    private const string WindowsSkuX64 = "2025-datacenter-g2";

    // ── Windows 11 Arm64 (marketplace desktop image) ─────────────────────
    private const string WindowsArm64Publisher = "microsoftwindowsdesktop";
    private const string WindowsArm64Offer     = "windows11preview-arm64";
    private const string WindowsArm64Sku       = "win11-25h2-ent";

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Create an ArmClient using DefaultAzureCredential.
    /// Works with managed identity in production and with az login / VS creds locally.
    /// </summary>
    private static ArmClient CreateArmClient()
    {
        TokenCredential credential = new DefaultAzureCredential();
        return new ArmClient(credential);
    }

    // ── Quotas ───────────────────────────────────────────────────────────

    /// <summary>Cached (region → family → quota) so we don't hit ARM on every provision.</summary>
    private readonly Dictionary<string, (DateTime FetchedAt, List<CoreQuota> Quotas)> _quotaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _quotaLock = new(1, 1);
    private static readonly TimeSpan QuotaCacheTtl = TimeSpan.FromMinutes(10);
    /// <summary>Retry a failing region sooner than a successful one, but not on every call.</summary>
    private static readonly TimeSpan QuotaFailureTtl = TimeSpan.FromMinutes(5);
    private ILogger QuotaLogger => logger;

    /// <summary>Regions used by Azure targets in the catalog.</summary>
    private static IEnumerable<string> AzureRegions() =>
        TargetCatalog.GetAllTargetNames()
            .Select(TargetCatalog.GetTarget)
            .Where(t => t.CloudProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase) && t.Region is not null)
            .Select(t => t.Region!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CoreQuota>> GetQuotasAsync(CancellationToken ct = default)
    {
        var all = new List<CoreQuota>();
        foreach (var region in AzureRegions())
        {
            try
            {
                all.AddRange(await GetRegionQuotasAsync(region, ct));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Azure: failed to read quotas for region {Region}", region);
            }
        }
        return all;
    }

    public async Task<CoreQuota?> GetQuotaForPlatformAsync(string platform, CancellationToken ct = default)
    {
        var target = TargetCatalog.GetTarget(platform);
        if (!target.CloudProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
            return null;

        var region = ResolveLocation(platform).ToString();
        var family = ResolveQuotaFamily(target.InstanceName);
        if (family is null) return null;

        var quotas = await GetRegionQuotasAsync(region, ct);
        return quotas.FirstOrDefault(q => q.Family.Equals(family, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Map a VM size template ("Standard_D{0}ads_v6") to the ARM usage family name
    /// ("standardDADSv6Family"), which is what the quota API is keyed on.
    /// </summary>
    internal static string? ResolveQuotaFamily(string? vmSizeTemplate)
    {
        if (string.IsNullOrEmpty(vmSizeTemplate)) return null;

        // "Standard_D{0}ads_v6" → letters before "_v" (minus the {0}) plus the version
        var size = vmSizeTemplate.Replace("{0}", "", StringComparison.Ordinal);
        var match = System.Text.RegularExpressions.Regex.Match(
            size, @"^Standard_(?<prefix>[A-Za-z]+)_?v(?<version>\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        return $"standard{match.Groups["prefix"].Value.ToUpperInvariant()}v{match.Groups["version"].Value}Family";
    }

    private async Task<List<CoreQuota>> GetRegionQuotasAsync(string region, CancellationToken ct)
    {
        await _quotaLock.WaitAsync(ct);
        try
        {
            if (_quotaCache.TryGetValue(region, out var cached)
                && DateTime.UtcNow - cached.FetchedAt < (cached.Quotas.Count == 0 ? QuotaFailureTtl : QuotaCacheTtl))
            {
                return cached.Quotas;
            }

            var quotas = new List<CoreQuota>();
            try
            {
                var armClient = CreateArmClient();
                var subscription = await armClient.GetDefaultSubscriptionAsync(ct);

                // Disambiguate: Network also has a GetUsagesAsync extension on SubscriptionResource.
                await foreach (var usage in ComputeExtensions.GetUsagesAsync(subscription, new AzureLocation(region), ct))
                {
                    var name = usage.Name?.Value;
                    if (string.IsNullOrEmpty(name) || !name.EndsWith("Family", StringComparison.OrdinalIgnoreCase))
                        continue;

                    quotas.Add(new CoreQuota(
                        Provider: Name,
                        Region: region,
                        Family: name,
                        DisplayName: usage.Name?.LocalizedValue ?? name,
                        Used: (int)usage.CurrentValue,
                        Limit: (int)usage.Limit));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One concise line per region — a credential failure otherwise dumps a
                // multi-hundred-line DefaultAzureCredential stack trace for every pool.
                var reason = ex.Message.Split('\n')[0].Trim();
                QuotaLogger.LogWarning("Azure: cannot read quotas for {Region} ({Type}: {Reason}) — " +
                                       "falling back to the TargetCatalog core limits",
                    region, ex.GetType().Name, reason);
            }

            _quotaCache[region] = (DateTime.UtcNow, quotas);
            return quotas;
        }
        finally
        {
            _quotaLock.Release();
        }
    }

    /// <summary>
    /// Best-effort pre-flight: fail with a readable message instead of an opaque ARM
    /// "QuotaExceeded" deployment error minutes later.
    /// </summary>
    private async Task EnsureQuotaAsync(string platform, int cores, string jobId, CancellationToken ct)
    {
        CoreQuota? quota;
        try
        {
            quota = await GetQuotaForPlatformAsync(platform, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{JobId}] Azure: quota pre-flight check failed, continuing anyway", jobId);
            return;
        }

        if (quota is null) return;

        logger.LogInformation("[{JobId}] Azure quota for {Family} in {Region}: {Used}/{Limit} vCPUs used",
            jobId, quota.DisplayName, quota.Region, quota.Used, quota.Limit);

        if (cores > quota.Limit)
        {
            throw new InvalidOperationException(
                $"{cores} vCPUs requested but the '{quota.DisplayName}' quota in {quota.Region} is only {quota.Limit}. " +
                $"Raise the quota in the Azure portal or lower the core count.");
        }

        if (cores > quota.Available)
        {
            throw new InvalidOperationException(
                $"{cores} vCPUs requested but only {quota.Available} of the '{quota.DisplayName}' quota " +
                $"in {quota.Region} are free ({quota.Used}/{quota.Limit} in use). " +
                $"Wait for other VMs of that family to finish, or raise the quota.");
        }
    }

    /// <summary>
    /// Resolve the Azure VM size string from the platform string and core count.
    /// Uses the VM size template from the target definition, with config override support.
    /// </summary>
    private string ResolveVmSize(string platform, int cores)
    {
        var target = TargetCatalog.GetTarget(platform);

        // Allow config override: Azure:VmSizeOverride:azure_genoa → "Standard_D{0}ads_v6"
        var template = config[$"Azure:VmSizeOverride:{target.Name}"];

        if (string.IsNullOrEmpty(template))
        {
            template = target.InstanceName
                ?? throw new InvalidOperationException($"Target '{target.Name}' has no Azure VM size (InstanceName) defined.");
        }

        return string.Format(template, cores);
    }

    /// <summary>
    /// Resolve Azure region from target definition or config override.
    /// </summary>
    private AzureLocation ResolveLocation(string platform)
    {
        var target = TargetCatalog.GetTarget(platform);

        // Allow config override: Azure:LocationOverride:azure_genoa → "westeurope"
        var locationStr = config[$"Azure:LocationOverride:{target.Name}"];
        if (!string.IsNullOrEmpty(locationStr))
            return new AzureLocation(locationStr);

        if (!string.IsNullOrEmpty(target.Region))
            return new AzureLocation(target.Region);

        // Final fallback
        return target.OsFamily == "windows" ? AzureLocation.WestEurope : AzureLocation.EastUS;
    }

    /// <summary>
    /// Load the ARM template JSON from an embedded resource.
    /// </summary>
    private static string LoadArmTemplate(bool isWindows)
    {
        var resourceName = isWindows ? "azure-arm-windows.json" : "azure-arm-linux.json";
        using var stream = typeof(AzureCloudProvider).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded ARM template '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Admin password for provisioned VMs. Configure <c>Azure:AdminPassword</c>; otherwise a
    /// random one is generated per process — never fall back to a value that is public in git.
    /// </summary>
    private string GetAdminPassword()
    {
        var configured = config["Azure:AdminPassword"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return _generatedPassword.Value;
    }

    private readonly Lazy<string> _generatedPassword = new(() =>
    {
        // Azure requires upper/lower/digit/special and 12-72 chars.
        var random = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .Replace('+', 'x').Replace('/', 'y').TrimEnd('=');
        return $"Eb1!{random}";
    });

    /// <summary>
    /// Build the ARM template parameters for a Linux VM deployment.
    /// </summary>
    private BinaryData BuildLinuxParameters(string jobId, string vmSize, string cloudInitScript, int diskSizeGb, string platform)
    {
        var isArm64 = TargetCatalog.GetTarget(platform).Arch == VmArch.Arm64;
        var offer = DefaultOffer;
        var sku = isArm64 ? DefaultSkuArm64 : DefaultSkuX64;

        var password = GetAdminPassword();

        return BinaryData.FromObjectAsJson(new
        {
            runnerId = new { value = jobId },
            osDiskSizeGiB = new { value = Math.Max(diskSizeGb, 64) },
            virtualMachineSize = new { value = vmSize },
            adminPassword = new { value = password },
            customData = new { value = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudInitScript)) },
            imageReference = new
            {
                value = new
                {
                    publisher = "canonical",
                    offer,
                    sku,
                    version = "latest"
                }
            }
        });
    }

    /// <summary>
    /// Build the ARM template parameters for a Windows VM deployment.
    /// The PowerShell bootstrap script is passed as base64-encoded customData,
    /// and executed via the Custom Script Extension in the ARM template.
    /// </summary>
    private BinaryData BuildWindowsParameters(string jobId, string vmSize, string cloudInitScript, int diskSizeGb, string platform)
    {
        var isArm64 = TargetCatalog.GetTarget(platform).Arch == VmArch.Arm64;

        var password = GetAdminPassword();

        // Encode the PowerShell script as base64 for customData
        var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudInitScript));

        // Arm64 uses Windows 11 Desktop marketplace image; x64 uses Windows Server
        string publisher, offer, sku;

        if (isArm64)
        {
            publisher = WindowsArm64Publisher;
            offer     = WindowsArm64Offer;
            sku       = WindowsArm64Sku;
        }
        else
        {
            publisher = "MicrosoftWindowsServer";
            offer     = WindowsOffer;
            sku       = WindowsSkuX64;
        }

        return BinaryData.FromObjectAsJson(new
        {
            runnerId = new { value = jobId },
            osDiskSizeGiB = new { value = Math.Max(diskSizeGb, 128) },
            virtualMachineSize = new { value = vmSize },
            adminPassword = new { value = password },
            customData = new { value = scriptBase64 },
            imageReference = new
            {
                value = new
                {
                    publisher,
                    offer,
                    sku,
                    version = "latest"
                }
            }
        });
    }

    /// <summary>
    /// After the ARM deployment completes, discover the VM's public IP address.
    /// </summary>
    private static string? GetVmPublicIp(ArmClient armClient, SubscriptionResource subscription,
        string resourceGroupName, string vmName)
    {
        try
        {
            var rg = subscription.GetResourceGroup(resourceGroupName).Value;
            var vm = rg.GetVirtualMachine(vmName).Value;

            var nicRef = vm.Data.NetworkProfile.NetworkInterfaces.FirstOrDefault();
            if (nicRef is null) return null;

            var nic = armClient.GetNetworkInterfaceResource(new ResourceIdentifier(nicRef.Id!));
            var ipConfig = nic.GetNetworkInterfaceIPConfigurations().FirstOrDefault();
            if (ipConfig is null) return null;

            if (!ipConfig.HasData)
                ipConfig = ipConfig.Get().Value;

            if (ipConfig.Data.PublicIPAddress is null) return null;

            var publicIp = armClient.GetPublicIPAddressResource(
                new ResourceIdentifier(ipConfig.Data.PublicIPAddress.Id!));
            if (!publicIp.HasData)
                publicIp = publicIp.Get().Value;

            return publicIp.Data.IPAddress;
        }
        catch
        {
            return null;
        }
    }

    // ── ICloudProvider ──────────────────────────────────────────────────

    public async Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        if (_semaphore.CurrentCount == 0)
            logger.LogWarning("All Azure provisioning slots are busy — waiting...");

        await _semaphore.WaitAsync(ct);
        try
        {
            var armClient = CreateArmClient();
            var subscription = await armClient.GetDefaultSubscriptionAsync(ct);

            var location = ResolveLocation(request.Platform);

            // Historically hard-coded to 16, which silently produced a 16-core VM for a job
            // that had rented (and reported) far more. Configurable, and loud when it clamps.
            var coresMax = config.GetValue("Azure:MaxCoresPerVm", 64);
            var cores = Math.Min(coresMax, request.Cores);
            if (cores != request.Cores)
            {
                logger.LogWarning(
                    "[{JobId}] Azure: clamping {Requested} cores to {Cores} (Azure:MaxCoresPerVm)",
                    request.JobId, request.Cores, cores);
            }

            await EnsureQuotaAsync(request.Platform, cores, request.JobId, ct);

            var vmSize = ResolveVmSize(request.Platform, cores);
            var diskSize = Math.Max(request.DiskSizeGb, 64);

            logger.LogInformation(
                "[{JobId}] Azure: creating VM. Size={VmSize}, Location={Location}, Disk={Disk}GB",
                request.JobId, vmSize, location, diskSize);

            // 1. Create a dedicated resource group
            var resourceGroupName = $"egorbot-{request.JobId}";
            var rgData = new ResourceGroupData(location);
            var resourceGroup = (await subscription.GetResourceGroups()
                .CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, rgData, ct)).Value;

            logger.LogInformation("[{JobId}] Resource group '{RG}' created in {Location}",
                request.JobId, resourceGroupName, location);

            // 2. Load ARM template (separate templates for Linux vs Windows)
            var isWindows = TargetCatalog.GetTarget(request.Platform).OsFamily == "windows";
            var template = LoadArmTemplate(isWindows);

            // 3. Build deployment parameters
            var parameters = isWindows
                ? BuildWindowsParameters(request.JobId, vmSize, request.CloudInitScript, diskSize, request.Platform)
                : BuildLinuxParameters(request.JobId, vmSize, request.CloudInitScript, diskSize, request.Platform);

            var deploymentContent = new ArmDeploymentContent(
                new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
                {
                    Template = BinaryData.FromString(template),
                    Parameters = parameters
                });

            // 4. Deploy
            var deploymentName = $"egorbot-deploy-{request.JobId}";
            var deployment = (await resourceGroup.GetArmDeployments()
                .CreateOrUpdateAsync(WaitUntil.Completed, deploymentName, deploymentContent, ct)).Value;

            logger.LogInformation("[{JobId}] ARM deployment '{DeploymentName}' completed",
                request.JobId, deploymentName);

            // 5. Discover public IP
            var vmName = $"runner-vm-{request.JobId}";
            var publicIp = GetVmPublicIp(armClient, subscription, resourceGroupName, vmName);

            if (!string.IsNullOrEmpty(publicIp))
            {
                logger.LogInformation(
                    "[{JobId}] Azure VM ready.\n\ncode --folder-uri \"vscode-remote://ssh-remote+runner@{IpAddress}/home\"\nssh runner@{IpAddress2}\n",
                    request.JobId, publicIp, publicIp);
            }
            else
            {
                logger.LogWarning("[{JobId}] Azure VM '{VmName}' is running but no public IP found",
                    request.JobId, vmName);
            }

            // InstanceId = resource group name (used for deprovisioning)
            return new ProvisionResult(resourceGroupName, publicIp);
        }
        catch (Exception provisioningError)
        {
            logger.LogError(
                provisioningError,
                "[{JobId}] Azure provisioning failed: {Message}",
                request.JobId,
                provisioningError.Message);

            try
            {
                await DeprovisionAsync(
                    $"egorbot-{request.JobId}", CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                throw new ProvisioningCleanupException(
                    $"egorbot-{request.JobId}", provisioningError, cleanupError);
            }

            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeprovisionAsync(string instanceId, CancellationToken ct = default)
    {
        // instanceId is the resource group name
        try
        {
            logger.LogInformation("Azure: deleting resource group '{RG}'", instanceId);

            var armClient = CreateArmClient();
            var subscription = await armClient.GetDefaultSubscriptionAsync(ct);
            var rgResponse = await subscription.GetResourceGroups().GetIfExistsAsync(instanceId, ct);

            if (rgResponse?.Value is null)
            {
                logger.LogWarning("Azure: resource group '{RG}' not found — already deleted?", instanceId);
                return;
            }

            await rgResponse.Value.DeleteAsync(WaitUntil.Completed, cancellationToken: ct);
            logger.LogInformation("Azure: resource group '{RG}' deleted", instanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Azure: failed to delete resource group '{RG}'", instanceId);
            throw;
        }
    }

    public async Task<bool> TryDeprovisionByJobIdAsync(
        string jobId,
        CancellationToken ct = default)
    {
        await DeprovisionAsync($"egorbot-{jobId}", ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> ListActiveVmsAsync(CancellationToken ct = default)
    {
        try
        {
            var armClient = CreateArmClient();
            var subscription = await armClient.GetDefaultSubscriptionAsync(ct);
            var names = new List<string>();

            await foreach (var rg in subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct))
            {
                ct.ThrowIfCancellationRequested();
                await foreach (var vm in rg.GetVirtualMachines().GetAllAsync(cancellationToken: ct))
                {
                    names.Add($"{rg.Data.Name}/{vm.Data.Name}");
                }
            }

            return names;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure: failed to list active VMs");
            return [];
        }
    }
}
