namespace EgorBot.Data;

public enum SubJobStatus
{
    Provisioning,
    Running,
    Completed,
    Failed,
    TimedOut,
    Deallocating,
}

public class SubJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    public Job Job { get; set; } = null!;
    public TargetOs TargetOs { get; set; }
    public TargetArch TargetArch { get; set; }
    public string HardwareProfile { get; set; } = "default";
    public string CloudProvider { get; set; } = "";
    public string? CloudInstanceId { get; set; }
    public SubJobStatus Status { get; set; } = SubJobStatus.Provisioning;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResultArtifactPath { get; set; }
}

public enum TargetOs
{
    Ubuntu2204,
    Ubuntu2404,
    AmazonLinux2023,
    Windows2022,
    Debian12,
    MacOsSequoia,
}

public enum TargetArch
{
    X64,
    X86,
    Arm32,
    Arm64,
}

/// <summary>
/// Specific CPU/hardware variant to provision.
/// Maps to concrete VM sizes in each cloud provider.
/// </summary>
public enum VmCpu
{
    // Azure
    AzureAmpere,
    AzureMilano,
    AzureCascadeLake,
    AzureCobalt100,
    AzureGenoa,
    AzureGenoaSMT1,

    // AWS Arm64
    AwsGraviton2,
    AwsGraviton3,
    AwsGraviton4,

    // AWS Arm64 Macs
    AwsM1,
    AwsM1Ultra,
    AwsM2,
    AwsM2Pro,

    // AWS x64
    AwsSapphireLake,
    AwsIceLake,
    AwsGenoa,
    AwsTurin,
    AwsMilano,

    // AWS x86 Macs
    AwsMacx86,
}

public static class VmCpuExtensions
{
    public static bool IsArm64(this VmCpu cpu) =>
        cpu is VmCpu.AwsGraviton2 or VmCpu.AwsGraviton3 or VmCpu.AwsGraviton4
            or VmCpu.AwsM1 or VmCpu.AwsM1Ultra or VmCpu.AwsM2 or VmCpu.AwsM2Pro
            or VmCpu.AzureAmpere or VmCpu.AzureCobalt100;

    public static bool IsAws(this VmCpu cpu) => cpu.ToString().StartsWith("Aws");
    public static bool IsAzure(this VmCpu cpu) => cpu.ToString().StartsWith("Azure");

    public static TargetArch ToArch(this VmCpu cpu) => cpu.IsArm64() ? TargetArch.Arm64 : TargetArch.X64;
}

public static class TargetOsExtensions
{
    public static bool IsWindows(this TargetOs os) => os == TargetOs.Windows2022;
    public static bool IsMac(this TargetOs os) => os == TargetOs.MacOsSequoia;
    public static bool IsLinux(this TargetOs os) => os is TargetOs.Ubuntu2204 or TargetOs.Ubuntu2404 or TargetOs.Debian12 or TargetOs.AmazonLinux2023;
}
