using EgorBot.Shared;
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
public sealed class HelixCloudProvider(IConfiguration config, IServiceProvider serviceProvider, ILogger<HelixCloudProvider> logger) : ICloudProvider
{
    private const string DefaultCreator = "EgorBot";
    private const string DefaultSource = "EgorBot/bench";
    private const string DefaultType = "EgorBot/runtime-perf";

    public string Name => "Helix";

    public async Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        var target = TargetCatalog.GetTarget(request.Platform);
        var queueId = target.InstanceName
                      ?? throw new InvalidOperationException(
                          $"Helix target '{target.Name}' has no queue ID (InstanceName is null).");

        var isWindows = target.OsFamily == "windows";
        var isMacOs = target.OsFamily.Equals("osx", StringComparison.OrdinalIgnoreCase);
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
            .WithTimeout(TimeSpan.FromHours(2.5))
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
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonReaderException || ex.InnerException is Newtonsoft.Json.JsonReaderException)
        {
            // Helix API returns a non-JSON response when the job has already finished;
            // the SDK chokes on it.  This is harmless — the job is done.
            logger.LogDebug("Helix: job '{CorrelationId}' already finished (cancel returned non-JSON)", instanceId);
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
    /// Uses <c>$HELIX_WORKITEM_PAYLOAD</c> (<c>%HELIX_WORKITEM_PAYLOAD%</c> on Windows)
    /// as the root for a per-job working directory.
    /// </summary>
    private static string AdaptScriptForHelix(string script, bool isWindows, bool isMacOs = false)
    {
        if (isWindows)
        {
            // Use HELIX_WORKITEM_PAYLOAD as the work directory root
            return script
                .Replace("$workDir = 'C:\\egorbot_work'",
                         "$workDir = Join-Path $env:HELIX_WORKITEM_PAYLOAD 'egorbot_work'")
                // Run synchronously instead of Start-Process + Wait
                .Replace("Start-Process $python -ArgumentList '", "& $python ")
                .Replace("' -NoNewWindow -RedirectStandardOutput agent.log -RedirectStandardError agent_err.log -Wait", " 2>&1 | Tee-Object -FilePath agent.log");
        }

        // Linux / macOS bash scripts
        var adapted = script
            // Use HELIX_WORKITEM_PAYLOAD as the work directory root
            .Replace("cd /home\n", "cd \"$HELIX_WORKITEM_PAYLOAD\"\n")
            // Run agent synchronously (not as background process)
            .Replace("nohup $PYTHON bdn-benchmarking-common.py", "$PYTHON bdn-benchmarking-common.py")
            // Remove trailing '&' from agent launch (keep tee)
            .Replace("2>&1 | tee agent.log &", "2>&1 | tee agent.log");

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

                    string? detailsUrl = null;

                    // Try to get the details URL for diagnostics
                    try
                    {
                        var summary = await api.Job.SummaryAsync(correlationId);
                        detailsUrl = summary.DetailsUrl;
                        if (!string.IsNullOrEmpty(detailsUrl))
                        {
                            logger.LogInformation("[{JobId}] Helix: details URL: {DetailsUrl}",
                                jobId, detailsUrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[{JobId}] Helix: failed to fetch job summary", jobId);
                    }

                    // Fetch work item details (pass/fail, console logs)
                    var errorLines = new List<string>();
                    try
                    {
                        var workItems = await api.WorkItem.ListAsync(correlationId);
                        foreach (var wi in workItems)
                        {
                            logger.LogInformation(
                                "[{JobId}] Helix work item '{Name}': State={State}",
                                jobId, wi.Name, wi.State);

                            // Try to fetch console log for non-passed items
                            if (!string.Equals(wi.State, "Passed", StringComparison.OrdinalIgnoreCase))
                            {
                                errorLines.Add($"Work item '{wi.Name}' state: {wi.State}");
                                try
                                {
                                    using var logStream = await api.WorkItem.ConsoleLogAsync(wi.Name, correlationId);
                                    using var reader = new StreamReader(logStream);
                                    var consoleLog = await reader.ReadToEndAsync();
                                    if (!string.IsNullOrEmpty(consoleLog))
                                    {
                                        // Keep last 50 lines
                                        var lines = consoleLog.Split('\n');
                                        var tail = string.Join("\n", lines.Length > 50 ? lines[^50..] : lines);
                                        logger.LogWarning(
                                            "[{JobId}] Helix work item '{Name}' console (last 50 lines):\n{Log}",
                                            jobId, wi.Name, tail);
                                        errorLines.Add(tail);
                                    }
                                }
                                catch { /* best effort */ }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[{JobId}] Helix: failed to list work items", jobId);
                        errorLines.Add($"Failed to list work items: {ex.Message}");
                    }

                    // Signal the orchestrator that the Helix job is done.
                    // If the agent already called /complete, TrySetResult is a no-op.
                    SignalOrchestrator(jobId, errorLines, detailsUrl);

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{JobId}] Helix: monitoring stopped due to error", jobId);
        }
    }

    /// <summary>
    /// Signal the orchestrator that the Helix work items have finished.
    /// If the agent already called <c>/complete</c>, <c>TrySetResult</c> inside
    /// <see cref="JobOrchestrator.CompleteJob"/> is a harmless no-op.
    /// </summary>
    private void SignalOrchestrator(string jobId, List<string> errorLines, string? detailsUrl)
    {
        if (!Guid.TryParse(jobId, out var jobGuid))
        {
            logger.LogWarning("Helix monitor: cannot parse jobId '{JobId}' as Guid", jobId);
            return;
        }

        try
        {
            var orchestrator = serviceProvider.GetRequiredService<JobOrchestrator>();
            if (errorLines.Count > 0)
            {
                var errorMsg = $"Helix work item(s) did not pass.";
                if (!string.IsNullOrEmpty(detailsUrl))
                    errorMsg += $" Details: {detailsUrl}";
                errorMsg += "\n" + string.Join("\n", errorLines);
                orchestrator.CompleteJob(jobGuid, new JobOutcome(Success: false, Error: errorMsg));
            }
            else
            {
                // All work items passed — agent should have already called /complete,
                // but signal success as a safety net.
                orchestrator.CompleteJob(jobGuid, new JobOutcome(Success: true));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Helix monitor: failed to signal orchestrator for job {JobId}", jobId);
        }
    }
}
