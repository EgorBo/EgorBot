namespace EgorBot.Cloud.Implementations;

public class LocalExecution(ILogger<LocalExecution> logger, IConfiguration config)
    : ICloudProvider
{
    public string Name => "Local";

    public bool SupportsSpec(CloudMachineSpec spec) => true;

    public async Task<string> ProvisionAsync(string subJobId, CloudMachineSpec spec, string script, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task DeallocateAsync(string cloudInstanceId, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
