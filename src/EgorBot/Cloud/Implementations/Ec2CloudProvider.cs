using System.Text;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using EgorBot.Data;

namespace EgorBot.Cloud.Implementations;

/// <summary>
/// AWS EC2 cloud provider — provisions EC2 instances with user-data scripts.
/// Ported from the original EgorBot implementation.
/// </summary>
public class Ec2CloudProvider(ILogger<Ec2CloudProvider> logger, IConfiguration config) : ICloudProvider
{
    // Networking (defaults from the old bot — override via config)
    private const string DefaultSecurityGroup = "sg-03a175852c486f0cf";
    private const string DefaultSubnetId = "subnet-075a806d626376615";
    private const string DefaultKeyPairName = "mackey";

    // AMI IDs (us-east-1)
    private const string Ubuntu2404X64   = "ami-0181ceca08e32d5dd";
    private const string Ubuntu2404Arm64 = "ami-096ea6a12ea24a797";
    private const string Ubuntu2204X64   = "ami-005fc0f236362e99f";
    private const string Ubuntu2204Arm64 = "ami-07ee04759daf109de";
    private const string AmazonLinux2023X64   = "ami-052064a798f08f0d3";
    private const string AmazonLinux2023Arm64 = "ami-089f6a79b0e02648a";
    private const string Debian12X64   = "ami-0779caf41f9ba54f0";
    private const string Debian12Arm64 = "ami-07c4edc673430f8b8";
    private const string Windows2022X64 = "ami-08073302a1e5b9b02";

    private static readonly SemaphoreSlim ProvisionSemaphore = new(3, 3);

    public string Name => "EC2";

    public bool SupportsSpec(CloudMachineSpec spec) =>
        spec.Os is TargetOs.Ubuntu2204 or TargetOs.Ubuntu2404 or TargetOs.AmazonLinux2023
                or TargetOs.Debian12 or TargetOs.Windows2022 or TargetOs.MacOsSequoia
        && spec.Arch is TargetArch.X64 or TargetArch.Arm64;

    public async Task<string> ProvisionAsync(string subJobId, CloudMachineSpec spec, string script, CancellationToken ct = default)
    {
        var client = CreateClient();
        var cpu = ResolveCpu(spec);
        int cores = config.GetValue("Aws:Cores", 8);
        int diskSize = config.GetValue("Aws:DiskSizeGb", 64);

        if (spec.Os.IsWindows())
        {
            diskSize = Math.Max(diskSize, 64);
            cores = Math.Max(cores, 16);
        }
        if (spec.Os.IsMac())
            diskSize = Math.Max(diskSize, 100);
        else
            diskSize = Math.Min(diskSize, 100);

        // Resolve instance type
        string instanceFamily = cpu switch
        {
            VmCpu.AwsGraviton2     => "c6g",
            VmCpu.AwsGraviton3     => "c7g",
            VmCpu.AwsGraviton4     => "c8g",
            VmCpu.AwsSapphireLake  => "c7i",
            VmCpu.AwsIceLake       => "c6i",
            VmCpu.AwsGenoa         => "c7a",
            VmCpu.AwsTurin         => "r8a",
            VmCpu.AwsMilano        => "c6a",
            VmCpu.AwsMacx86        => "mac1.metal",
            VmCpu.AwsM1            => "mac2.metal",
            VmCpu.AwsM1Ultra       => "mac2-m1ultra.metal",
            VmCpu.AwsM2            => "mac2-m2.metal",
            VmCpu.AwsM2Pro         => "mac2-m2pro.metal",
            _ => throw new ArgumentOutOfRangeException(nameof(cpu)),
        };

        string instanceType;
        if (instanceFamily.EndsWith(".metal"))
        {
            instanceType = instanceFamily;
        }
        else
        {
            var suffix = cores switch
            {
                1  => ".medium",
                2  => ".large",
                4  => ".xlarge",
                8  => ".2xlarge",
                16 => ".4xlarge",
                32 => ".8xlarge",
                64 => ".16xlarge",
                _  => ".2xlarge",
            };
            instanceType = instanceFamily + suffix;
        }

        // Resolve AMI
        bool isArm = cpu.IsArm64();
        string imageId = spec.Os switch
        {
            TargetOs.Ubuntu2404     => isArm ? Ubuntu2404Arm64 : Ubuntu2404X64,
            TargetOs.Ubuntu2204     => isArm ? Ubuntu2204Arm64 : Ubuntu2204X64,
            TargetOs.AmazonLinux2023 => isArm ? AmazonLinux2023Arm64 : AmazonLinux2023X64,
            TargetOs.Debian12       => isArm ? Debian12Arm64 : Debian12X64,
            TargetOs.Windows2022    => Windows2022X64,
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };

        string diskDeviceName = spec.Os == TargetOs.AmazonLinux2023 ? "/dev/xvda" : "/dev/sda1";
        var sg = config["Aws:SecurityGroupId"] ?? DefaultSecurityGroup;
        var subnet = config["Aws:SubnetId"] ?? DefaultSubnetId;
        var keyPair = config["Aws:KeyPairName"] ?? DefaultKeyPairName;

        await ProvisionSemaphore.WaitAsync(ct);
        try
        {
            var runRequest = new RunInstancesRequest
            {
                ImageId = imageId,
                InstanceType = instanceType,
                MinCount = 1,
                MaxCount = 1,
                KeyName = keyPair,
                UserData = Convert.ToBase64String(Encoding.UTF8.GetBytes(script)),
                SecurityGroupIds = [sg],
                SubnetId = subnet,
                BlockDeviceMappings =
                [
                    new BlockDeviceMapping
                    {
                        DeviceName = diskDeviceName,
                        Ebs = new EbsBlockDevice
                        {
                            VolumeSize = diskSize,
                            DeleteOnTermination = true,
                            Encrypted = false,
                            Iops = 8000,
                            VolumeType = VolumeType.Gp3,
                        }
                    }
                ]
            };

            var response = await client.RunInstancesAsync(runRequest, ct);
            var instance = response.Reservation.Instances[0];
            var instanceId = instance.InstanceId;

            // Wait for public IP
            string? publicIp = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var describeResponse = await client.DescribeInstancesAsync(
                        new DescribeInstancesRequest { InstanceIds = [instanceId] }, ct);
                    var inst = describeResponse.Reservations[0].Instances[0];
                    if (inst.State.Name == InstanceStateName.Running)
                    {
                        publicIp = inst.PublicIpAddress;
                        break;
                    }
                }
                catch { /* retry */ }
                await Task.Delay(2000, ct);
            }

            logger.LogInformation("EC2 instance {InstanceId} ({Type}) launched, IP: {Ip}",
                instanceId, instanceType, publicIp ?? "pending");

            return instanceId;
        }
        finally
        {
            ProvisionSemaphore.Release();
        }
    }

    public async Task DeallocateAsync(string cloudInstanceId, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            var response = await client.TerminateInstancesAsync(
                new TerminateInstancesRequest { InstanceIds = [cloudInstanceId] }, ct);

            foreach (var stateChange in response.TerminatingInstances)
                logger.LogInformation("EC2 instance {Id} → {State}", stateChange.InstanceId, stateChange.CurrentState.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to terminate EC2 instance {Id}", cloudInstanceId);
        }
    }

    private AmazonEC2Client CreateClient()
    {
        var region = RegionEndpoint.GetBySystemName(config["Aws:Region"] ?? "us-east-1");
        var accessKey = config["Aws:AccessKeyId"];
        var secretKey = config["Aws:SecretAccessKey"];

        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
            return new AmazonEC2Client(new BasicAWSCredentials(accessKey, secretKey), region);

        // Falls back to environment variables / instance profile
        return new AmazonEC2Client(region);
    }

    private static VmCpu ResolveCpu(CloudMachineSpec spec)
    {
        return spec.HardwareProfile.ToLowerInvariant() switch
        {
            "graviton2" => VmCpu.AwsGraviton2,
            "graviton3" => VmCpu.AwsGraviton3,
            "graviton4" or "graviton" => VmCpu.AwsGraviton4,
            "sapphirelake" => VmCpu.AwsSapphireLake,
            "icelake" => VmCpu.AwsIceLake,
            "genoa" or "amd" => VmCpu.AwsGenoa,
            "turin" => VmCpu.AwsTurin,
            "milano" => VmCpu.AwsMilano,
            _ => spec.Arch == TargetArch.Arm64 ? VmCpu.AwsGraviton4 : VmCpu.AwsGenoa,
        };
    }
}
