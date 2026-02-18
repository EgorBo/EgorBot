using System.Diagnostics;
using EgorBot.Server.Models;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Runs the agent inside a Docker container for local sandboxed execution.
/// Uses the same cloud-init bash script that Azure/AWS would run, executed
/// inside a container with no host volume mounts for security isolation.
/// </summary>
public sealed class DockerCloudProvider(IConfiguration config, ILogger<DockerCloudProvider> logger) : ICloudProvider
{
    public string Name => "Docker";

    private string DockerImage => config["Docker:Image"] ?? "ubuntu:24.04";
    private int MemoryLimitMb => config.GetValue("Docker:MemoryLimitMb", 16384);
    private int CpuLimit => config.GetValue("Docker:CpuLimit", 8);
    private int DiskSizeGb => config.GetValue("Docker:DiskSizeGb", 64);

    public async Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        var containerName = $"egorbot-{request.JobId[..Math.Min(12, request.JobId.Length)]}";

        // Ensure Docker is available
        var dockerCheck = await RunDockerAsync("version --format {{.Server.Version}}", ct);
        if (!dockerCheck.Success)
        {
            throw new InvalidOperationException(
                $"Docker is not available or not running: {dockerCheck.Output}");
        }

        logger.LogInformation("[{JobId}] Docker version: {Version}", request.JobId, dockerCheck.Output.Trim());

        // The cloud-init script is a bash script — write it as the container's entrypoint
        // We pass the entire script via stdin using `docker run -i` with a bash heredoc
        var script = request.CloudInitScript;

        // Build docker run command
        // --rm is NOT used because we need the container name for deprovision
        // The container will stop naturally when the script finishes
        var dockerArgs = new List<string>
        {
            "run", "-d",               // detached
            "--name", containerName,
            "--memory", $"{MemoryLimitMb}m",
            "--cpus", CpuLimit.ToString(),
            "--network", "host",       // needs network to call back to EgorBot.Server
        };

        // Add tmpfs for /tmp to avoid filling up the overlay
        dockerArgs.AddRange(["--tmpfs", "/tmp:exec,size=2g"]);

        // Use the configured image
        dockerArgs.Add(DockerImage);

        // Execute the cloud-init script via bash -c
        dockerArgs.Add("bash");
        dockerArgs.Add("-c");
        dockerArgs.Add(script);

        var argsString = BuildArgString(dockerArgs);
        logger.LogInformation("[{JobId}] Starting container: docker {Args}", request.JobId,
            $"run -d --name {containerName} ... {DockerImage} bash -c <script>");

        var result = await RunDockerAsync(argsString, ct);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to start Docker container for job {request.JobId}: {result.Output}");
        }

        var containerId = result.Output.Trim();
        logger.LogInformation("[{JobId}] Container started: {ContainerId} ({ContainerName})",
            request.JobId, containerId[..12], containerName);

        return new ProvisionResult(
            InstanceId: containerName,
            IpAddress: "127.0.0.1");
    }

    public async Task DeprovisionAsync(string instanceId, CancellationToken ct = default)
    {
        logger.LogInformation("Stopping and removing container: {Container}", instanceId);

        // Force remove (stops if running, then removes)
        var result = await RunDockerAsync($"rm -f {instanceId}", ct);
        if (result.Success)
        {
            logger.LogInformation("Container {Container} removed", instanceId);
        }
        else
        {
            logger.LogWarning("Failed to remove container {Container}: {Output}", instanceId, result.Output);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<(bool Success, string Output)> RunDockerAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (proc.ExitCode == 0, output);
    }

    /// <summary>
    /// Build a properly escaped argument string for docker. The last argument (the script)
    /// may contain special characters, so we rely on ProcessStartInfo handling.
    /// </summary>
    private static string BuildArgString(List<string> args)
    {
        // For Process.Start, we need to build the argument string carefully.
        // The script content is the last argument and may contain quotes/newlines.
        var parts = new List<string>();
        foreach (var arg in args)
        {
            if (arg.Contains(' ') || arg.Contains('"') || arg.Contains('\n') || arg.Contains('\''))
            {
                // Escape for shell: wrap in single quotes, escape existing single quotes
                var escaped = arg.Replace("'", "'\\''");
                parts.Add($"'{escaped}'");
            }
            else
            {
                parts.Add(arg);
            }
        }
        return string.Join(" ", parts);
    }
}
