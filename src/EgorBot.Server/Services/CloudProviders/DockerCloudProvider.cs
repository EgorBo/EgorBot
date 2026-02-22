using System.Diagnostics;
using EgorBot.Server.Models;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Runs the agent inside a Docker container for local sandboxed execution.
/// Uses the same cloud-init bash script that Azure/AWS would run, executed
/// inside a container via a bind-mounted script file.
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
        var dockerCheck = await RunDockerAsync(["version", "--format", "{{.Server.Version}}"], ct);
        if (!dockerCheck.Success)
        {
            throw new InvalidOperationException(
                $"Docker is not available or not running: {dockerCheck.Output}");
        }

        logger.LogInformation("[{JobId}] Docker version: {Version}", request.JobId, dockerCheck.Output.Trim());

        // Prepare the script for Docker (install deps, run in foreground,
        // fix callback URLs for Docker Desktop where localhost != host)
        var script = PrepareScript(request.CloudInitScript);

        // Write the script to a temp file and bind-mount it into the container.
        // This avoids all shell escaping issues with passing the script via `bash -c`.
        var scriptDir = Path.Combine(Path.GetTempPath(), "egorbot-docker", request.JobId);
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, "bootstrap.sh");
        await File.WriteAllTextAsync(scriptPath, script, ct);
        logger.LogInformation("[{JobId}] Wrote bootstrap script: {Path} ({Len} bytes)",
            request.JobId, scriptPath, script.Length);

        // Convert Windows path to Docker-compatible path for bind-mount
        var mountSource = scriptPath.Replace('\\', '/');
        if (mountSource.Length >= 2 && mountSource[1] == ':')
            mountSource = "/" + char.ToLower(mountSource[0]) + mountSource[2..];

        var dockerArgs = new List<string>
        {
            "run", "-d",
            "--name", containerName,
            "--memory", $"{MemoryLimitMb}m",
            "--cpus", CpuLimit.ToString(),
            "--network", "host",
            "--tmpfs", "/tmp:exec,size=2g",
            // On Docker Desktop (Windows/macOS) host.docker.internal resolves
            // automatically, but add it explicitly for Linux Docker compatibility.
            "--add-host", "host.docker.internal:host-gateway",
            "-v", $"{mountSource}:/egorbot-bootstrap.sh:ro",
            DockerImage,
            "bash", "/egorbot-bootstrap.sh"
        };

        logger.LogInformation("[{JobId}] Starting container: docker run -d --name {Name} ... {Image} bash /egorbot-bootstrap.sh",
            request.JobId, containerName, DockerImage);

        var result = await RunDockerAsync(dockerArgs, ct);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to start Docker container for job {request.JobId}: {result.Output}");
        }

        var containerId = result.Output.Trim();
        logger.LogInformation("[{JobId}] Container started: {ContainerId} ({ContainerName})",
            request.JobId, containerId[..Math.Min(12, containerId.Length)], containerName);

        return new ProvisionResult(
            InstanceId: containerName,
            IpAddress: "127.0.0.1");
    }

    public async Task DeprovisionAsync(string instanceId, CancellationToken ct = default)
    {
        logger.LogInformation("Stopping and removing container: {Container}", instanceId);

        var result = await RunDockerAsync(["rm", "-f", instanceId], ct);
        if (result.Success)
            logger.LogInformation("Container {Container} removed", instanceId);
        else
            logger.LogWarning("Failed to remove container {Container}: {Output}", instanceId, result.Output);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<(bool Success, string Output)> RunDockerAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (proc.ExitCode == 0, output);
    }

    /// <summary>
    /// Adapt the cloud-init script for a Docker container:
    /// 1. Prepend package installation (curl, python3) — bare ubuntu images lack these.
    /// 2. Replace background agent launch (nohup ... &amp;) with foreground execution
    ///    so the container stays alive until the agent finishes.
    /// </summary>
    private static string PrepareScript(string script)
    {
        // Install sudo (agent scripts use 'sudo apt ...' but Docker runs as root)
        // along with curl, python3, and libicu which aren't in the bare ubuntu image.
        // DOTNET_SYSTEM_GLOBALIZATION_INVARIANT is set early so any .NET tool
        // invoked before libicu is fully available still works.
        const string preamble =
            "export DEBIAN_FRONTEND=noninteractive\n" +
            "apt-get update -qq && apt-get install -y -qq curl python3 sudo libicu-dev > /dev/null 2>&1\n";

        var idx = script.IndexOf("curl ", StringComparison.Ordinal);
        if (idx > 0)
            script = script[..idx] + preamble + script[idx..];
        else
            script = script.Replace("#!/bin/bash\n", "#!/bin/bash\n" + preamble);

        // Run agent in foreground — container must stay alive while the agent runs.
        script = script.Replace(
            "nohup $PYTHON bdn-benchmarking-common.py",
            "$PYTHON bdn-benchmarking-common.py");
        script = script.Replace(
            "2>&1 | tee agent.log &",
            "2>&1 | tee agent.log");

        // On Docker Desktop (Windows/macOS), localhost inside the container
        // does NOT reach the host. Replace with host.docker.internal.
        script = script.Replace("localhost", "host.docker.internal");
        script = script.Replace("127.0.0.1", "host.docker.internal");

        return script;
    }
}
