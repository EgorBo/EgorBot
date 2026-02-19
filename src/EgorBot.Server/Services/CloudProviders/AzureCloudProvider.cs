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
public sealed class AzureCloudProvider(IConfiguration config, ILogger<AzureCloudProvider> logger) : ICloudProvider
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
    /// Build the ARM template parameters for a Linux VM deployment.
    /// </summary>
    private BinaryData BuildLinuxParameters(string jobId, string vmSize, string cloudInitScript, int diskSizeGb, string platform)
    {
        var isArm64 = TargetCatalog.GetTarget(platform).Arch == VmArch.Arm64;
        var offer = DefaultOffer;
        var sku = isArm64 ? DefaultSkuArm64 : DefaultSkuX64;

        var password = config["Azure:AdminPassword"] ?? "EgorBot_Bench_2025!";

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

        var password = config["Azure:AdminPassword"] ?? "EgorBot_Bench_2025!";

        // Encode the PowerShell script as base64 for customData
        var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudInitScript));

        // Arm64 uses Windows 11 Desktop marketplace image; x64 uses Windows Server
        string publisher, offer, sku;
        object planInfo;

        if (isArm64)
        {
            publisher = WindowsArm64Publisher;
            offer     = WindowsArm64Offer;
            sku       = WindowsArm64Sku;
            planInfo  = new { name = WindowsArm64Sku, publisher = WindowsArm64Publisher, product = WindowsArm64Offer };
        }
        else
        {
            publisher = "MicrosoftWindowsServer";
            offer     = WindowsOffer;
            sku       = WindowsSkuX64;
            planInfo  = new { };
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
            },
            planInfo = new { value = planInfo }
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
            var coresMax = 16; // safety cap
            var cores = Math.Min(coresMax, request.Cores);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{JobId}] Azure provisioning failed: {Message}", request.JobId, ex.Message);

            // Clean up partially-created resource group on failure
            try
            {
                var rgName = $"egorbot-{request.JobId}";
                var armClient = CreateArmClient();
                var sub = await armClient.GetDefaultSubscriptionAsync(ct);
                var rgResponse = await sub.GetResourceGroups().GetIfExistsAsync(rgName, ct);
                if (rgResponse?.Value is not null)
                {
                    logger.LogInformation("[{JobId}] Cleaning up resource group '{RG}' after failure",
                        request.JobId, rgName);
                    await rgResponse.Value.DeleteAsync(WaitUntil.Started, cancellationToken: ct);
                }
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "[{JobId}] Failed to clean up resource group", request.JobId);
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
        }
    }
}
