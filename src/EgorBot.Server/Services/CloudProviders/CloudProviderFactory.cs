namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Resolves the appropriate <see cref="ICloudProvider"/> for a given platform string.
/// The cloud provider is determined by the target definition (e.g. "azure_genoa" → Azure, "aws_graviton4" → AWS).
/// </summary>
public sealed class CloudProviderFactory(IEnumerable<ICloudProvider> providers)
{
    public ICloudProvider GetProvider(string platform)
    {
        var target = Models.Platform.Resolve(platform);
        var cloudName = target.CloudProvider; // "Azure", "AWS", "Local"

        return providers.FirstOrDefault(p =>
            p.Name.Equals(cloudName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No cloud provider registered for '{cloudName}' (target '{target.Name}').");
    }
}
