using System.Diagnostics;
using EgorBot.Server.Models;
using EgorBot.Server.Services.CloudInit;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Runs the agent as a local process — no VM provisioning.
/// Uses <see cref="CloudInitBuilder"/> to generate the bootstrap script (same as
/// other providers), then executes it locally via bash or PowerShell.
/// </summary>
public sealed class LocalRunnerProvider(
    IConfiguration config,
    CloudInitBuilder cloudInitBuilder,
    ILogger<LocalRunnerProvider> logger) : ICloudProvider
{
    public string Name => "Local";

    public Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        var job = request.Job
            ?? throw new InvalidOperationException("LocalRunnerProvider requires Job in ProvisionRequest");

        // Use configured work dir or create a temp one
        var workDir = config["LocalRunner:LocalWorkDir"];
        if (string.IsNullOrWhiteSpace(workDir))
            workDir = Path.Combine(Path.GetTempPath(), "egorbot", request.JobId);
        Directory.CreateDirectory(workDir);
        logger.LogInformation("Work directory: {WorkDir}", workDir);

        // Generate the same cloud-init script that VMs/containers use
        var script = cloudInitBuilder.Build(job);

        // Write script to work dir and execute it
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(workDir, "bootstrap.ps1");
            File.WriteAllText(scriptPath, script);
            logger.LogInformation("Wrote bootstrap script: {Path}", scriptPath);

            var shell = ResolvePowerShell();
            logger.LogInformation("Using shell: {Shell}", shell);

            psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment =
                {
                    ["PYTHONIOENCODING"] = "utf-8",
                    ["PYTHONUTF8"] = "1"
                }
            };
        }
        else
        {
            var scriptPath = Path.Combine(workDir, "bootstrap.sh");
            File.WriteAllText(scriptPath, script);
            logger.LogInformation("Wrote bootstrap script: {Path}", scriptPath);

            psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = scriptPath,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
        }

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start bootstrap process.");

        logger.LogInformation("Started local agent process PID={Pid} for job {JobId}", process.Id, request.JobId);

        // Fire-and-forget: drain stdout/stderr to logger and monitor exit
        _ = DrainStreamAsync(process.StandardOutput, request.JobId, "stdout");
        _ = DrainStreamAsync(process.StandardError, request.JobId, "stderr");
        _ = MonitorProcessAsync(process, request.JobId);

        return Task.FromResult(new ProvisionResult(
            InstanceId: process.Id.ToString(),
            IpAddress: "127.0.0.1"));
    }

    public Task DeprovisionAsync(string instanceId, CancellationToken ct = default)
    {
        if (int.TryParse(instanceId, out var pid))
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    logger.LogInformation("Killed local agent process PID={Pid}", pid);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                logger.LogDebug("Process PID={Pid} already exited", pid);
            }
        }

        return Task.CompletedTask;
    }

    private static string ResolvePowerShell()
    {
        // Prefer pwsh (PowerShell 7+), fall back to powershell (5.1)
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "-Version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                p?.WaitForExit(3000);
                p?.Kill();
                return candidate;
            }
            catch { /* not found, try next */ }
        }
        return "powershell"; // last resort
    }

    private async Task MonitorProcessAsync(Process process, string jobId)
    {
        try
        {
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                logger.LogInformation("[{JobId}] Bootstrap process exited with code 0", jobId);
            else
                logger.LogWarning("[{JobId}] Bootstrap process exited with code {ExitCode}", jobId, process.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{JobId}] Error monitoring bootstrap process", jobId);
        }
    }

    private async Task DrainStreamAsync(StreamReader reader, string jobId, string streamName)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                logger.LogInformation("[{JobId}/{Stream}] {Line}", jobId, streamName, line);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error draining {Stream} for job {JobId}", streamName, jobId);
        }
    }
}
