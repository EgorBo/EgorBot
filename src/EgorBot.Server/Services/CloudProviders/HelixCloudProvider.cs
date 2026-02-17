using System.Text.Json;
using System.Text.Json.Nodes;
using EgorBot.Server.Models;
using Microsoft.DotNet.Helix.Client;
using Microsoft.DotNet.Helix.Client.Models;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Provisions benchmark runs on Helix infrastructure.
/// Instead of managing VMs directly, this provider submits a Helix job that runs the
/// cloud-init script as a work item payload. The python agent inside the script calls
/// back to the EgorBot server just like it does on Azure/AWS VMs.
///
/// InstanceId = Helix job correlation ID.
/// No IP address is returned (Helix machines are not directly accessible).
/// </summary>
public sealed class HelixCloudProvider(IConfiguration config, ILogger<HelixCloudProvider> logger) : ICloudProvider
{
    private const string DefaultCreator = "EgorBot";
    private const string DefaultSource = "EgorBot/bench";
    private const string DefaultType = "EgorBot/runtime-perf";

    public string Name => "Helix";

    public async Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        var target = Platform.Resolve(request.Platform);
        var queueId = target.VmSizeTemplate
                      ?? throw new InvalidOperationException(
                          $"Helix target '{target.Name}' has no queue ID (VmSizeTemplate is null).");

        var isWindows = Platform.IsWindows(request.Platform);
        var isMacOs = Platform.GetOs(request.Platform).Equals("osx", StringComparison.OrdinalIgnoreCase);
        var scriptFileName = isWindows ? "egorbot-run.ps1" : "egorbot-run.sh";
        var command = isWindows ? $"powershell -ExecutionPolicy Bypass -File {scriptFileName}"
                                : $"bash {scriptFileName}";

        // Adapt the cloud-init script for Helix:
        //  - Remove 'cd /home' (use Helix work directory)
        //  - Run agent synchronously (remove nohup & background)
        //  - On macOS, replace wget with curl
        var payload = AdaptScriptForHelix(request.CloudInitScript, isWindows, isMacOs);

        var creator = config["Helix:Creator"] ?? DefaultCreator;
        var source = config["Helix:Source"] ?? DefaultSource;
        var jobType = config["Helix:Type"] ?? DefaultType;

        logger.LogInformation(
            "[{JobId}] Helix: submitting job. Queue={Queue}, Command={Command}",
            request.JobId, queueId, command);

        IHelixApi api = ApiFactory.GetAnonymous();

        ISentJob job = await api.Job.Define()
            .WithType(jobType)
            .WithTargetQueue(queueId)
            .WithCreator(creator)
            .WithSource(source)
            .DefineWorkItem($"egorbot-{request.JobId}")
            .WithCommand(command)
            .WithSingleFilePayload(scriptFileName, payload)
            .AttachToJob()
            .SendAsync(cancellationToken: ct);

        var correlationId = job.CorrelationId;

        logger.LogInformation(
            "[{JobId}] Helix: job submitted. CorrelationId={CorrelationId}",
            request.JobId, correlationId);

        // Start a background task to log Helix job status
        _ = Task.Run(() => MonitorHelixJobAsync(api, correlationId, request.JobId), CancellationToken.None);

        // InstanceId = correlationId (used for deprovisioning / status)
        return new ProvisionResult(correlationId, IpAddress: null);
    }

    public async Task DeprovisionAsync(string instanceId, CancellationToken ct = default)
    {
        // Helix jobs are self-cleaning — no VM to tear down.
        // We attempt to cancel the job if it's still running, but don't fail if we can't.
        try
        {
            logger.LogInformation("Helix: attempting to cancel job '{CorrelationId}'", instanceId);

            IHelixApi api = ApiFactory.GetAnonymous();
            await api.Job.CancelAsync(instanceId, "EgorBot deprovisioning", ct);

            logger.LogInformation("Helix: job '{CorrelationId}' cancelled", instanceId);
        }
        catch (Exception ex)
        {
            // Cancellation is best-effort; the job may have already finished
            logger.LogWarning(ex, "Helix: failed to cancel job '{CorrelationId}' (may have already finished)",
                instanceId);
        }
    }

    /// <summary>
    /// Adapt the generated cloud-init script to run properly inside a Helix work item.
    /// </summary>
    private static string AdaptScriptForHelix(string script, bool isWindows, bool isMacOs = false)
    {
        if (isWindows)
        {
            // For Windows PowerShell scripts — adjust working directory
            return script
                .Replace("$workDir = 'C:\\egorbot_work'", "$workDir = $PWD.Path")
                .Replace("New-Item -ItemType Directory -Force -Path $workDir | Out-Null\r\n", "")
                .Replace("New-Item -ItemType Directory -Force -Path $workDir | Out-Null\n", "")
                .Replace("Set-Location $workDir\r\n", "")
                .Replace("Set-Location $workDir\n", "")
                // Run synchronously instead of Start-Process
                .Replace("Start-Process python -ArgumentList '", "python ")
                .Replace("' -NoNewWindow -RedirectStandardOutput agent.log -RedirectStandardError agent_err.log", " 2>&1 | Tee-Object -FilePath agent.log");
        }

        // Linux / macOS bash scripts
        var adapted = script
            // Remove 'cd /home' — use Helix working directory
            .Replace("cd /home\n", "")
            // Run agent synchronously (not as background process)
            .Replace("nohup python3 egorbot-agent.py", "python3 egorbot-agent.py")
            // Remove trailing '&' from agent launch (keep tee)
            .Replace("2>&1 | tee agent.log &", "2>&1 | tee agent.log");

        if (isMacOs)
        {
            // macOS doesn't have wget by default — use curl instead
            // Pattern: wget -q -O <filename> "<url>" → curl -sL -o <filename> "<url>"
            adapted = System.Text.RegularExpressions.Regex.Replace(
                adapted,
                @"wget -q -O (\S+) ""([^""]+)""",
                @"curl -sL -o $1 ""$2""");
        }

        return adapted;
    }

    /// <summary>
    /// Background task that periodically polls Helix for job status and logs progress.
    /// </summary>
    private async Task MonitorHelixJobAsync(IHelixApi api, string correlationId, string jobId)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));

                JobDetails details;
                try
                {
                    details = await api.Job.DetailsAsync(correlationId);
                }
                catch
                {
                    // Transient failure — keep trying
                    continue;
                }

                if (details.WorkItems.Waiting > 0 || details.WorkItems.Unscheduled > 0)
                {
                    logger.LogInformation("[{JobId}] Helix: waiting for job to start...", jobId);
                    continue;
                }

                if (details.WorkItems.Running > 0)
                {
                    logger.LogInformation("[{JobId}] Helix: job is running...", jobId);
                    continue;
                }

                if (details.WorkItems.Running == 0 && details.WorkItems.Finished > 0)
                {
                    logger.LogInformation("[{JobId}] Helix: all work items finished.", jobId);

                    // Try to get the details URL for diagnostics
                    try
                    {
                        var summary = await api.Job.SummaryAsync(correlationId);
                        if (!string.IsNullOrEmpty(summary.DetailsUrl))
                        {
                            logger.LogInformation("[{JobId}] Helix: details URL: {DetailsUrl}",
                                jobId, summary.DetailsUrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[{JobId}] Helix: failed to fetch job summary", jobId);
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{JobId}] Helix: monitoring stopped due to error", jobId);
        }
    }
}
