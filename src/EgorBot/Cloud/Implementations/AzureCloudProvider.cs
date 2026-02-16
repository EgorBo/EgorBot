using System.Text;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using EgorBot.Data;

namespace EgorBot.Cloud.Implementations;

/// <summary>
/// Azure VM cloud provider — provisions Azure VMs with cloud-init (ARM template deployment).
/// Ported from the original EgorBot implementation.
/// </summary>
public class AzureCloudProvider(ILogger<AzureCloudProvider> logger, IConfiguration config) : ICloudProvider
{
    // ARM templates hosted as gists
    private const string LinuxTemplate = "https://gist.githubusercontent.com/EgorBo/ce5ca672bf1f4d502cb19f50db4c7b92/raw";
    private const string WindowsTemplate = "https://gist.githubusercontent.com/EgorBo/c4be1aa2f285ec8c54659bd6df36aab4/raw";

    // Pre-built Windows images
    private const string Arm64ImageId = "/subscriptions/7cf9e899-0a10-4abe-9fe6-9b9fcc94523e/resourceGroups/Arm64TemplateRG/providers/Microsoft.Compute/galleries/WinArmGal2/images/WinArmDef2/versions/0.0.5";
    private const string X64ImageId = "/subscriptions/7cf9e899-0a10-4abe-9fe6-9b9fcc94523e/resourceGroups/Winx64TemplateRG/providers/Microsoft.Compute/galleries/WinX64TemplateGal/images/Winx64ImageDef";

    // Maps VmCpu → max core counts
    private static readonly Dictionary<VmCpu, int> MaxCores = new()
    {
        [VmCpu.AzureAmpere]      = 8,
        [VmCpu.AzureCobalt100]   = 64,
        [VmCpu.AzureMilano]      = 8,
        [VmCpu.AzureGenoa]       = 16,
        [VmCpu.AzureGenoaSMT1]   = 16,
        [VmCpu.AzureCascadeLake] = 16,
    };

    public string Name => "Azure";

    public bool SupportsSpec(CloudMachineSpec spec) =>
        spec.Os is TargetOs.Ubuntu2204 or TargetOs.Ubuntu2404 or TargetOs.Windows2022 or TargetOs.Debian12
        && spec.Arch is TargetArch.X64 or TargetArch.Arm64;

    public async Task<string> ProvisionAsync(string subJobId, CloudMachineSpec spec, string script, CancellationToken ct = default)
    {
        var credential = GetCredential();
        var cpu = ResolveCpu(spec);
        int cores = Math.Min(MaxCores.GetValueOrDefault(cpu, 8), config.GetValue("Azure:MaxCoresPerInstance", 8));

        var location = cpu switch
        {
            VmCpu.AzureMilano      => AzureLocation.WestUS,
            VmCpu.AzureCobalt100   => AzureLocation.EastUS,
            VmCpu.AzureAmpere      => AzureLocation.EastUS,
            VmCpu.AzureGenoa       => AzureLocation.EastUS,
            VmCpu.AzureGenoaSMT1   => AzureLocation.EastUS,
            VmCpu.AzureCascadeLake => AzureLocation.WestEurope,
            _ => AzureLocation.EastUS,
        };

        if (spec.Os.IsWindows())
            location = AzureLocation.WestEurope;

        // Resolve Ubuntu image offer/sku
        string offer = "0001-com-ubuntu-server-jammy";
        string sku = cpu.IsArm64() ? "22_04-lts-arm64" : "22_04-lts-gen2";
        if (spec.Os == TargetOs.Ubuntu2404)
        {
            offer = "ubuntu-24_04-lts";
            sku = cpu.IsArm64() ? "server-arm64" : "server";
        }

        // Resolve VM size string
        string vmSize = cpu switch
        {
            VmCpu.AzureAmpere      => $"Standard_D{cores}pds_v5",
            VmCpu.AzureCobalt100   => $"Standard_D{cores}pds_v6",
            VmCpu.AzureMilano      => $"Standard_D{cores}ads_v5",
            VmCpu.AzureGenoa       => $"Standard_D{cores}ads_v6",
            VmCpu.AzureGenoaSMT1   => $"Standard_F{cores}ams_v6",
            VmCpu.AzureCascadeLake => $"Standard_D{cores}ds_v5",
            _ => throw new ArgumentOutOfRangeException(nameof(cpu)),
        };

        var password = config["Azure:VmPassword"] ?? Guid.NewGuid().ToString("N") + "!Aa";

        // Fetch the ARM template
        using var http = new HttpClient();
        var template = await http.GetStringAsync(spec.Os.IsWindows() ? WindowsTemplate : LinuxTemplate, ct);

        // Build template parameters
        BinaryData parameters;
        if (spec.Os.IsWindows())
        {
            parameters = BinaryData.FromObjectAsJson(new
            {
                runnerId = new { value = subJobId },
                osDiskSizeGiB = new { value = 64 },
                virtualMachineSize = new { value = vmSize },
                ImageId = new { value = cpu.IsArm64() ? Arm64ImageId : X64ImageId },
            });
        }
        else
        {
            parameters = BinaryData.FromObjectAsJson(new
            {
                runnerId = new { value = subJobId },
                osDiskSizeGiB = new { value = 128 },
                virtualMachineSize = new { value = vmSize },
                adminPassword = new { value = password },
                customData = new { value = Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) },
                imageReference = new
                {
                    value = new
                    {
                        publisher = "canonical",
                        offer,
                        sku,
                        version = "latest",
                    }
                }
            });
        }

        var deploymentContent = new ArmDeploymentContent(
            new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(template),
                Parameters = parameters,
            });

        var armClient = new ArmClient(credential);
        var subscription = await armClient.GetDefaultSubscriptionAsync(ct);

        var resourceGroupName = $"runtime-runner-{subJobId}";
        var resourceGroupData = new ResourceGroupData(location);
        var resourceGroup = (await subscription.GetResourceGroups()
            .CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, resourceGroupData, ct)).Value;

        var deploymentName = $"runner-deployment-{subJobId}";
        await resourceGroup.GetArmDeployments()
            .CreateOrUpdateAsync(WaitUntil.Completed, deploymentName, deploymentContent, ct);

        var vmName = $"runner-vm-{subJobId}";
        var ipAddress = GetVmIpAddress(armClient, subscription, resourceGroupName, vmName);

        logger.LogInformation("Azure VM provisioned: {VmSize} in {Location}, IP: {Ip}", vmSize, location, ipAddress);

        // Return the resource group name as the instance ID (used for deallocation)
        return resourceGroupName;
    }

    public async Task DeallocateAsync(string cloudInstanceId, CancellationToken ct = default)
    {
        try
        {
            var credential = GetCredential();
            var armClient = new ArmClient(credential);
            var subscription = await armClient.GetDefaultSubscriptionAsync(ct);
            var resourceGroup = (await subscription.GetResourceGroupAsync(cloudInstanceId, ct)).Value;
            await resourceGroup.DeleteAsync(WaitUntil.Completed, cancellationToken: ct);
            logger.LogInformation("Azure resource group {Rg} deleted", cloudInstanceId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            logger.LogWarning("Azure resource group {Rg} not found (already deleted?)", cloudInstanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete Azure resource group {Rg}", cloudInstanceId);
        }
    }

    private TokenCredential GetCredential()
    {
        // Uses DefaultAzureCredential which works with:
        // - Environment variables (AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_CLIENT_SECRET)
        // - Managed Identity (when running on Azure)
        // - Azure CLI (for local dev)
        return new Azure.Identity.DefaultAzureCredential();
    }

    private static VmCpu ResolveCpu(CloudMachineSpec spec)
    {
        // Map hardware profile string → VmCpu enum
        return spec.HardwareProfile.ToLowerInvariant() switch
        {
            "ampere" => VmCpu.AzureAmpere,
            "cobalt" or "cobalt100" => VmCpu.AzureCobalt100,
            "milano" => VmCpu.AzureMilano,
            "genoa" => VmCpu.AzureGenoa,
            "genoa_smt1" => VmCpu.AzureGenoaSMT1,
            "cascadelake" or "intel" => VmCpu.AzureCascadeLake,
            // Default based on arch
            _ => spec.Arch == TargetArch.Arm64 ? VmCpu.AzureAmpere : VmCpu.AzureCascadeLake,
        };
    }

    private static string GetVmIpAddress(ArmClient armClient, SubscriptionResource subscription, string resourceGroupName, string vmName)
    {
        try
        {
            var rg = subscription.GetResourceGroup(resourceGroupName);
            var vm = rg.Value.GetVirtualMachine(vmName);
            var networkInterfaceRef = vm.Value.Data.NetworkProfile.NetworkInterfaces.First();
            var nic = armClient.GetNetworkInterfaceResource(new ResourceIdentifier(networkInterfaceRef.Id!));
            var ipConfig = nic.GetNetworkInterfaceIPConfigurations().FirstOrDefault();
            if (!ipConfig!.HasData)
                ipConfig = ipConfig.Get().Value;
            var publicIp = armClient.GetPublicIPAddressResource(new ResourceIdentifier(ipConfig.Data.PublicIPAddress.Id!));
            if (!publicIp.HasData)
                publicIp = publicIp.Get().Value;
            return publicIp.Data.IPAddress;
        }
        catch (Exception)
        {
            return "";
        }
    }
}
