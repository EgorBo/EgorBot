using System.ComponentModel;
using System.Text;
using EgorBot.Github.Models;
using EgorBot.Github.Services;
using EgorBot.Shared;
using ModelContextProtocol.Server;

namespace EgorBot.Github.Mcp;

/// <summary>
/// MCP tools exposed by the EgorBot server.
/// These tools allow AI agents to submit benchmark jobs and query available targets.
/// </summary>
[McpServerToolType]
public sealed class BenchmarkTools
{
    /// <summary>
    /// Submit a benchmark job to EgorBot and return a tracking URL.
    /// </summary>
    [McpServerTool(Name = "run_benchmark"), Description(
        "Submit a .NET benchmark job to EgorBot. Returns a tracking URL or an error. " +
        "Provide C# benchmark code (BenchmarkDotNet-based class with [Benchmark] methods) " +
        "Provide PR number (to compare against its base/main) or a list of dotnet/runtime commits." +
        "Use the 'list_targets' tool first to discover valid target platforms.")]
    public static async Task<string> RunBenchmark(
        EgorBotClient botClient,
        JobTrackerService tracker,
        IConfiguration config,
        [Description("C# BenchmarkDotNet code (the class body with [Benchmark] methods). " +
                     "Do NOT include 'using' statements or namespace — they are added automatically.")]
        string benchmarkCode,
        [Description("Semicolon-separated commits/PRs to compare, e.g. 'PR_124445;main' or 'abc123;def456'. " +
                     "Use PR_<number> for pull requests.")]
        string commitsAndPrs,
        [Description("Target platform (e.g. 'amd', 'arm', 'intel', 'windows_x64'). " +
                     "Use 'list_targets' tool to see available targets. If omitted, defaults to 'arm'.")]
        string? target = null,
        [Description("Additional BenchmarkDotNet CLI arguments (e.g. '--filter *MyBenchmark*').")]
        string? bdnArguments = null,
        [Description("Enable perf profiler for the benchmark run.")]
        bool useProfiler = false)
    {
        // Resolve targets
        var targetList = new List<string>();
        if (!string.IsNullOrWhiteSpace(target))
        {
            foreach (var t in target.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TargetCatalog.TryResolve(t.Trim(), out var resolved))
                    return $"Error: Unknown target '{t.Trim()}'. Use the 'list_targets' tool to see valid targets.";
                targetList.Add(resolved!);
            }
        }
        else
        {
            targetList.Add("ubuntu24_azure_cobalt100");
        }

        // Validate commits
        if (string.IsNullOrWhiteSpace(commitsAndPrs))
            return "Error: 'commitsAndPrs' is required. Provide at least one commit SHA or PR number (e.g. 'PR_124445;main').";

        var command = new BotCommand
        {
            Targets = targetList,
            CommitsAndPrs = commitsAndPrs.Trim(),
            BdnArguments = bdnArguments,
            BenchmarkCode = benchmarkCode,
            UseProfiler = useProfiler,
        };

        // Submit to EgorBot.Server
        var response = await botClient.StartJobAsync(command, requestedBy: "mcp", sourceUrl: null);
        if (response is null)
            return "Error: Failed to submit the benchmark job to EgorBot.Server. The service may be unavailable.";

        // Build result with tracking URLs
        var sb = new StringBuilder();
        sb.AppendLine($"Benchmark job submitted successfully! Group ID: {response.GroupId}");
        sb.AppendLine();

        foreach (var job in response.Jobs)
        {
            var logsUrl = botClient.GetLogsUrl(job.Id);
            sb.AppendLine($"- **{job.Platform}**: {logsUrl}");
        }

        sb.AppendLine();
        sb.AppendLine("The jobs are now running. Results will be available at the URLs above once complete.");

        return sb.ToString();
    }

    /// <summary>
    /// List all available benchmark target platforms.
    /// </summary>
    [McpServerTool(Name = "list_targets"), Description(
        "List all available benchmark target platforms with their architecture, cloud provider, and CPU. " +
        "Use the target names returned here as the 'target' parameter for 'run_benchmark'.")]
    public static string ListTargets()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available benchmark targets:");
        sb.AppendLine();

        var groupedByCloud = TargetCatalog.GetAllTargetNames()
            .Select(n => TargetCatalog.GetTarget(n))
            .GroupBy(t => t.CloudProvider)
            .OrderBy(g => g.Key);

        foreach (var group in groupedByCloud)
        {
            sb.AppendLine($"## {group.Key}");
            foreach (var t in group.OrderBy(t => t.Name))
            {
                var defaultMark = t.PreferredDefault ? " (default)" : "";
                sb.AppendLine($"  - {t.Name}  [{t.Arch}, {t.CpuVendor}]{defaultMark}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("You can also use shorthand like 'arm', 'genoa', 'windows', 'aws_graviton4', etc.");
        return sb.ToString();
    }

    /// <summary>
    /// Get the status of a previously submitted benchmark job.
    /// </summary>
    [McpServerTool(Name = "get_job_status"), Description(
        "Check the status of a previously submitted benchmark job by its job ID (GUID).")]
    public static async Task<string> GetJobStatus(
        EgorBotClient botClient,
        [Description("The job ID (GUID) returned by 'run_benchmark'.")]
        string jobId)
    {
        if (!Guid.TryParse(jobId, out var id))
            return "Error: Invalid job ID. Provide a valid GUID.";

        var status = await botClient.GetJobStatusAsync(id);
        if (status is null)
            return $"Error: Job '{jobId}' not found or the service is unavailable.";

        var sb = new StringBuilder();
        sb.AppendLine($"Job: {status.Id}");
        sb.AppendLine($"Platform: {status.Platform}");
        sb.AppendLine($"Status: {status.Status}");

        if (status.HasResult)
        {
            sb.AppendLine("Result: Available");
            var result = await botClient.GetJobResultAsync(id);
            if (result is not null)
            {
                sb.AppendLine();
                sb.AppendLine(result);
            }
        }

        if (status.ErrorMessage is not null)
            sb.AppendLine($"Error: {status.ErrorMessage}");

        sb.AppendLine($"Logs: {botClient.GetLogsUrl(id)}");
        return sb.ToString();
    }
}
