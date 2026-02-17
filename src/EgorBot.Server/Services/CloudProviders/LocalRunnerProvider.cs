using System.Diagnostics;
using EgorBot.Server.Models;

namespace EgorBot.Server.Services.CloudProviders;

/// <summary>
/// Runs egorbot-agent.py as a local process — no VM provisioning.
/// Used for testing with "local_x64" / "local_arm64" platforms.
/// Writes files directly from job data; does NOT download from gists.
/// </summary>
public sealed class LocalRunnerProvider(IConfiguration config, ILogger<LocalRunnerProvider> logger) : ICloudProvider
{
    public string Name => "Local";

    public Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        var agentPath = ResolveAgentPath();
        logger.LogInformation("Agent script: {AgentPath}", agentPath);

        // Use configured work dir or create a temp one
        var workDir = config["EgorBot:LocalWorkDir"];
        if (string.IsNullOrWhiteSpace(workDir))
            workDir = Path.Combine(Path.GetTempPath(), "egorbot", request.JobId);
        Directory.CreateDirectory(workDir);
        logger.LogInformation("Work directory: {WorkDir}", workDir);

        var job = request.Job
            ?? throw new InvalidOperationException("LocalRunnerProvider requires Job in ProvisionRequest");

        // Write benchmark files directly from the job data
        WriteBenchmarkFiles(job, workDir);

        // Build python args directly (no cloud-init parsing needed)
        var serviceBaseUrl = config["EgorBot:ServiceBaseUrl"] ?? "http://localhost:5000";
        var callbackUrl = $"{serviceBaseUrl.TrimEnd('/')}/api/internal";
        var args = BuildAgentArgs(job, agentPath, workDir, callbackUrl);
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        logger.LogInformation("Agent command: {Python} {Args}", python, args);

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Environment =
            {
                // Force UTF-8 for Python on Windows (avoids cp1251 UnicodeEncodeError)
                ["PYTHONIOENCODING"] = "utf-8",
                ["PYTHONUTF8"] = "1"
            }
        };

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start agent process.");

        logger.LogInformation("Started local agent process PID={Pid} for job {JobId}", process.Id, request.JobId);

        // Fire-and-forget: drain stdout/stderr to logger
        _ = DrainStreamAsync(process.StandardOutput, request.JobId, "stdout");
        _ = DrainStreamAsync(process.StandardError, request.JobId, "stderr");

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

    /// <summary>Resolve the local path to egorbot-agent.py.</summary>
    private string ResolveAgentPath()
    {
        var agentPath = config["EgorBot:AgentScriptLocalPath"];
        if (string.IsNullOrWhiteSpace(agentPath))
        {
            agentPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "egorbot-agent.py"));
        }

        if (!File.Exists(agentPath))
            throw new FileNotFoundException($"Agent script not found at: {agentPath}");

        return agentPath;
    }

    /// <summary>
    /// Write Benchmark.cs, bench.csproj, and BDN_ARGS.rsp directly from job data.
    /// No gist downloads, no cloud-init parsing.
    /// </summary>
    private void WriteBenchmarkFiles(BenchmarkJob job, string workDir)
    {
        if (!string.IsNullOrWhiteSpace(job.BenchmarkCode))
        {
            var benchPath = Path.Combine(workDir, "Benchmark.cs");
            File.WriteAllText(benchPath, job.BenchmarkCode);
            logger.LogInformation("Wrote {File} ({Len} chars)", benchPath, job.BenchmarkCode.Length);

            // Write a default csproj template — same content the gist would provide
            var csprojPath = Path.Combine(workDir, "bench.csproj");
            if (!File.Exists(csprojPath))
            {
                // Check for a local template next to the agent script
                var localTemplate = Path.Combine(
                    Path.GetDirectoryName(ResolveAgentPath()) ?? "", "bench.csproj");
                if (File.Exists(localTemplate))
                {
                    File.Copy(localTemplate, csprojPath);
                    logger.LogInformation("Copied local bench.csproj from {Src}", localTemplate);
                }
                else
                {
                    // Download template from gist as last resort
                    var url = config["EgorBot:DefaultCsprojUrl"]
                        ?? "https://gist.githubusercontent.com/EgorBo/c3378873ad204ebf522a07138f621128/raw";
                    logger.LogInformation("Downloading bench.csproj from {Url}", url);
                    try
                    {
                        using var http = new HttpClient();
                        var data = http.GetStringAsync(url).GetAwaiter().GetResult();
                        File.WriteAllText(csprojPath, data);
                        logger.LogInformation("Downloaded bench.csproj ({Len} chars)", data.Length);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to download bench.csproj, writing minimal template");
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(job.BdnArguments))
        {
            var rspPath = Path.Combine(workDir, "BDN_ARGS.rsp");
            File.WriteAllText(rspPath, job.BdnArguments);
            logger.LogInformation("Wrote {File}", rspPath);
        }
    }

    /// <summary>Build the python command-line args directly from job data.</summary>
    private static string BuildAgentArgs(BenchmarkJob job, string agentPath, string workDir, string callbackUrl)
    {
        var parts = new List<string>
        {
            $"\"{agentPath}\"",
            $"--work_dir \"{workDir}\"",
            $"--job_tag \"{job.Id}\"",
            $"--gh_commits_and_prs \"{job.CommitsAndPrs}\"",
            $"--callback_url \"{callbackUrl}\"",
            $"--job_id \"{job.Id}\"",
        };

        if (!string.IsNullOrWhiteSpace(job.BenchmarkCode))
        {
            parts.Add("--bench_code_file Benchmark.cs");
            parts.Add("--bench_csproj_file bench.csproj");
        }

        if (job.UseProfiler)
            parts.Add("--perf_enabled 1");

        if (!string.IsNullOrWhiteSpace(job.BdnArguments))
            parts.Add("--bdn_args_file BDN_ARGS.rsp");

        return string.Join(" ", parts);
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
