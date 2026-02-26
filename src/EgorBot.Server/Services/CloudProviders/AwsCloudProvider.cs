using System.Text;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using EgorBot.Shared;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// AWS EC2 instance provisioning.
/// Creates on-demand instances, passes cloud-init via UserData, waits for a public IP,
/// and terminates instances on deprovisioning.
/// </summary>
public sealed class AwsCloudProvider(IConfiguration config, ILogger<AwsCloudProvider> logger) : ICloudProvider
{
    private readonly SemaphoreSlim _semaphore = new(3, 3);

    public string Name => "AWS";

    // ── AMI catalog (us-east-1) ──────────────────────────────────────────
    private const string Ubuntu2404X64      = "ami-0b6c6ebed2801a5cb";
    private const string Ubuntu2404Arm64    = "ami-096ea6a12ea24a797";
    private const string WindowsServer2025  = "ami-031283482dcfced88";

    // ── Helpers ──────────────────────────────────────────────────────────

    private AmazonEC2Client CreateEc2Client()
    {
        var accessKey = config["Aws:AccessKey"]
            ?? Environment.GetEnvironmentVariable("EGORBOT_AWS_K")
            ?? throw new InvalidOperationException("AWS access key not configured (Aws:AccessKey or EGORBOT_AWS_K).");
        var secretKey = config["Aws:SecretKey"]
            ?? Environment.GetEnvironmentVariable("EGORBOT_AWS_S")
            ?? throw new InvalidOperationException("AWS secret key not configured (Aws:SecretKey or EGORBOT_AWS_S).");

        var regionName = config["Aws:Region"] ?? "us-east-1";
        var region = RegionEndpoint.GetBySystemName(regionName);
        return new AmazonEC2Client(new BasicAWSCredentials(accessKey, secretKey), region);
    }

    /// <summary>
    /// Map (platform, cores) → EC2 instance type.
    /// Uses the instance family from the target definition (e.g. c7i, c8g, c7a, m8a).
    /// </summary>
    private static string ResolveInstanceType(string platform, int cores)
    {
        var target = TargetCatalog.GetTarget(platform);
        var family = target.InstanceName
            ?? throw new InvalidOperationException($"Target '{target.Name}' has no EC2 instance family (InstanceName) defined.");

        var suffix = cores switch
        {
            1  => ".medium",
            2  => ".large",
            4  => ".xlarge",
            8  => ".2xlarge",
            16 => ".4xlarge",
            32 => ".8xlarge",
            64 => ".16xlarge",
            _  => throw new ArgumentException($"Unsupported core count: {cores}. Must be 1/2/4/8/16/32/64.")
        };

        return family + suffix;
    }

    /// <summary>
    /// Select the right AMI for the given platform.
    /// </summary>
    private static string ResolveAmi(string platform)
    {
        var target = TargetCatalog.GetTarget(platform);
        if (target.OsFamily == "windows")
            return WindowsServer2025;
        return target.Arch == VmArch.Arm64
            ? Ubuntu2404Arm64
            : Ubuntu2404X64;
    }

    // ── ICloudProvider ──────────────────────────────────────────────────

    public async Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        if (_semaphore.CurrentCount == 0)
            logger.LogWarning("All AWS provisioning slots are busy — waiting...");

        await _semaphore.WaitAsync(ct);
        try
        {
            using var ec2 = CreateEc2Client();

            var securityGroupId = config["Aws:SecurityGroupId"]
                ?? throw new InvalidOperationException("Aws:SecurityGroupId not configured.");
            var subnetId = config["Aws:SubnetId"]
                ?? throw new InvalidOperationException("Aws:SubnetId not configured.");
            var keyPairName = config["Aws:KeyPairName"] ?? "mackey";

            var instanceType = ResolveInstanceType(request.Platform, request.Cores);
            var imageId = ResolveAmi(request.Platform);
            var diskSize = Math.Max(request.DiskSizeGb, 64);

            logger.LogInformation(
                "[{JobId}] Creating EC2 instance: type={Type}, ami={Ami}, disk={Disk}GB, sg={SG}, subnet={Subnet}",
                request.JobId, instanceType, imageId, diskSize, securityGroupId, subnetId);

            // AWS EC2Launch requires PowerShell UserData wrapped in <powershell> tags
            var isWindows = TargetCatalog.GetTarget(request.Platform).OsFamily == "windows";
            var userData = isWindows
                ? $"<powershell>\n{request.CloudInitScript}\n</powershell>"
                : request.CloudInitScript;

            var runRequest = new RunInstancesRequest
            {
                ImageId = imageId,
                InstanceType = instanceType,
                MinCount = 1,
                MaxCount = 1,
                KeyName = keyPairName,
                UserData = Convert.ToBase64String(Encoding.UTF8.GetBytes(userData)),
                SecurityGroupIds = [securityGroupId],
                SubnetId = subnetId,
                BlockDeviceMappings =
                [
                    new BlockDeviceMapping
                    {
                        DeviceName = isWindows ? "/dev/sda1" : "/dev/sda1",
                        Ebs = new EbsBlockDevice
                        {
                            VolumeSize = diskSize,
                            DeleteOnTermination = true,
                            Encrypted = false,
                            Iops = 8000,
                            VolumeType = VolumeType.Gp3
                        }
                    }
                ]
            };

            var runResponse = await ec2.RunInstancesAsync(runRequest, ct);
            var instance = runResponse.Reservation.Instances[0];
            var instanceId = instance.InstanceId;

            logger.LogInformation("[{JobId}] EC2 instance launched: {InstanceId}", request.JobId, instanceId);

            // Poll until the instance is running and has a public IP
            string? publicIp = null;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(2000, ct);
                try
                {
                    var describeResponse = await ec2.DescribeInstancesAsync(
                        new DescribeInstancesRequest { InstanceIds = [instanceId] }, ct);
                    var inst = describeResponse.Reservations[0].Instances[0];
                    if (inst.State.Name == InstanceStateName.Running &&
                        !string.IsNullOrEmpty(inst.PublicIpAddress))
                    {
                        publicIp = inst.PublicIpAddress;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[{JobId}] DescribeInstances attempt {Attempt} failed", request.JobId, attempt);
                }
            }

            if (!string.IsNullOrEmpty(publicIp))
            {
                var sshUser = isWindows ? "Administrator" : "ubuntu";
                var pemPath = config["Aws:SshKeyPath"] ?? "<ssh-key-path-not-specified>";
                logger.LogInformation(
                    "[{JobId}] Instance {InstanceId}\n\nssh {User}@{IP} -i {Pem}\n",
                    request.JobId, instanceId, sshUser, publicIp, pemPath);
            }
            else
            {
                logger.LogWarning("[{JobId}] EC2 instance {Id} running but no public IP assigned",
                    request.JobId, instanceId);
            }

            return new ProvisionResult(instanceId, publicIp);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeprovisionAsync(string instanceId, CancellationToken ct = default)
    {
        try
        {
            using var ec2 = CreateEc2Client();
            var response = await ec2.TerminateInstancesAsync(
                new TerminateInstancesRequest { InstanceIds = [instanceId] }, ct);

            foreach (var change in response.TerminatingInstances)
            {
                logger.LogInformation("EC2 instance {Id} → {State}",
                    change.InstanceId, change.CurrentState.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to terminate EC2 instance {Id}", instanceId);
        }
    }

    public async Task<IReadOnlyList<string>> ListActiveVmsAsync(CancellationToken ct = default)
    {
        try
        {
            using var ec2 = CreateEc2Client();
            var request = new DescribeInstancesRequest
            {
                Filters =
                [
                    new Filter("instance-state-name", ["pending", "running", "stopping"])
                ]
            };

            var names = new List<string>();
            DescribeInstancesResponse response;
            do
            {
                response = await ec2.DescribeInstancesAsync(request, ct);
                foreach (var reservation in response.Reservations)
                {
                    foreach (var instance in reservation.Instances)
                    {
                        var nameTag = instance.Tags?.FirstOrDefault(t => t.Key == "Name")?.Value;
                        var display = !string.IsNullOrEmpty(nameTag)
                            ? $"{nameTag} ({instance.InstanceId})"
                            : instance.InstanceId;
                        names.Add(display);
                    }
                }
                request.NextToken = response.NextToken;
            } while (!string.IsNullOrEmpty(response.NextToken));

            return names;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AWS: failed to list active instances");
            return [];
        }
    }
}
