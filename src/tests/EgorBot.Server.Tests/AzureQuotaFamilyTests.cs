using EgorBot.Server.Services.CloudProviders;
using EgorBot.Shared;

namespace EgorBot.Server.Tests;

/// <summary>
/// The VM size → ARM usage family mapping is what makes the quota lookup work; get it wrong
/// and the bot silently stops checking quotas (or checks the wrong family).
/// </summary>
public class AzureQuotaFamilyTests
{
    [Theory]
    [InlineData("Standard_D{0}ads_v6", "standardDADSv6Family")]   // AMD Genoa
    [InlineData("Standard_D{0}pds_v6", "standardDPDSv6Family")]   // Cobalt 100
    [InlineData("Standard_D{0}ds_v6", "standardDDSv6Family")]     // Emerald Rapids
    [InlineData("Standard_D{0}ads_v7", "standardDADSv7Family")]   // Turin
    [InlineData("Standard_D{0}ads_v5", "standardDADSv5Family")]   // Milano
    [InlineData("Standard_D{0}pds_v5", "standardDPDSv5Family")]   // Ampere
    [InlineData("Standard_D{0}ds_v5", "standardDDSv5Family")]     // Cascade Lake
    public void MapsVmSizeTemplateToUsageFamily(string template, string expected)
    {
        Assert.Equal(expected, AzureCloudProvider.ResolveQuotaFamily(template));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("c8g")]                      // AWS family
    [InlineData("osx.15.arm64.open")]        // Helix queue
    public void ReturnsNullForNonAzureSizes(string? template)
    {
        Assert.Null(AzureCloudProvider.ResolveQuotaFamily(template));
    }

    [Fact]
    public void EveryAzureTargetMapsToAFamily()
    {
        var unmapped = TargetCatalog.GetAllTargetNames()
            .Select(TargetCatalog.GetTarget)
            .Where(t => t.CloudProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
            .Where(t => AzureCloudProvider.ResolveQuotaFamily(t.InstanceName) is null)
            .Select(t => $"{t.Name} ({t.InstanceName})")
            .ToList();

        Assert.True(unmapped.Count == 0, "Unmapped Azure targets: " + string.Join(", ", unmapped));
    }
}
