using System.Text;
using EgorBot.Server.Models;

namespace EgorBot.Server.Services.CloudInit;

/// <summary>
/// Composes the cloud-init / bootstrap script that will run on the provisioned VM.
/// Downloads the agent script, writes benchmark files, and launches the agent with proper args.
/// </summary>
public sealed class CloudInitBuilder(IConfiguration config)
{
    /// <summary>
    /// Build the cloud-init script for a given job.
    /// </summary>
    public string Build(BenchmarkJob job)
    {
        var agentUrl = config["EgorBot:AgentScriptUrl"]
            ?? throw new InvalidOperationException("EgorBot:AgentScriptUrl configuration is required");
        var serviceBaseUrl = config["EgorBot:ServiceBaseUrl"]
                             ?? throw new InvalidOperationException("EgorBot:ServiceBaseUrl configuration is required");
        var callbackUrl = $"{serviceBaseUrl.TrimEnd('/')}/api/internal";
        var csprojUrl = config["EgorBot:DefaultCsprojUrl"] 
                        ?? throw new InvalidOperationException("EgorBot:DefaultCsprojUrl configuration is required");

        return Platform.IsWindows(job.Platform)
            ? BuildWindowsScript(job, agentUrl, callbackUrl, csprojUrl)
            : BuildLinuxScript(job, agentUrl, callbackUrl, csprojUrl);
    }

    private static string BuildLinuxScript(BenchmarkJob job, string agentUrl, string callbackUrl, string csprojUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.AppendLine();
        sb.AppendLine("cd /home");
        sb.AppendLine("mkdir -p egorbot_work");
        sb.AppendLine("cd egorbot_work");
        sb.AppendLine();

        // Download the agent script
        sb.AppendLine($"wget -q -O egorbot-agent.py \"{agentUrl}\"");
        sb.AppendLine("chmod +x egorbot-agent.py");
        sb.AppendLine();

        // Write benchmark code file if provided
        if (!string.IsNullOrWhiteSpace(job.BenchmarkCode))
        {
            sb.AppendLine("# Write benchmark code");
            sb.AppendLine($"cat > Benchmark.cs << 'EGORBOT_BENCH_EOF'");
            sb.AppendLine(job.BenchmarkCode);
            sb.AppendLine("EGORBOT_BENCH_EOF");
            sb.AppendLine();

            // Download the default csproj template
            sb.AppendLine($"wget -q -O bench.csproj \"{csprojUrl}\"");
            sb.AppendLine();
        }

        // Write BDN args to .rsp file if provided
        if (!string.IsNullOrWhiteSpace(job.BdnArguments))
        {
            sb.AppendLine("# Write BDN arguments");
            sb.AppendLine("cat > BDN_ARGS.rsp << 'EGORBOT_RSP_EOF'");
            foreach (var arg in SplitBdnArgs(job.BdnArguments))
            {
                sb.AppendLine(arg);
            }
            sb.AppendLine("EGORBOT_RSP_EOF");
            sb.AppendLine();
        }

        // Compose the agent invocation
        var agentArgs = BuildAgentArgs(job, callbackUrl, hasBenchmarkFile: !string.IsNullOrWhiteSpace(job.BenchmarkCode));

        sb.AppendLine("# Launch agent in background (tee to both file and cloud-init log)");
        sb.AppendLine($"nohup python3 egorbot-agent.py {agentArgs} 2>&1 | tee agent.log &");

        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string BuildWindowsScript(BenchmarkJob job, string agentUrl, string callbackUrl, string csprojUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PowerShell bootstrap for EgorBot agent");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$workDir = 'C:\\egorbot_work'");
        sb.AppendLine("New-Item -ItemType Directory -Force -Path $workDir | Out-Null");
        sb.AppendLine("Set-Location $workDir");
        sb.AppendLine();

        // Download agent
        sb.AppendLine($"Invoke-WebRequest -Uri '{agentUrl}' -OutFile 'egorbot-agent.py'");
        sb.AppendLine();

        // Write benchmark code
        if (!string.IsNullOrWhiteSpace(job.BenchmarkCode))
        {
            var escapedCode = job.BenchmarkCode.Replace("'", "''");
            sb.AppendLine("# Write benchmark code");
            sb.AppendLine($"Set-Content -Path 'Benchmark.cs' -Value @'");
            sb.AppendLine(job.BenchmarkCode);
            sb.AppendLine("'@");
            sb.AppendLine();

            sb.AppendLine($"Invoke-WebRequest -Uri '{csprojUrl}' -OutFile 'bench.csproj'");
            sb.AppendLine();
        }

        // Write BDN args
        if (!string.IsNullOrWhiteSpace(job.BdnArguments))
        {
            sb.AppendLine("# Write BDN arguments");
            sb.AppendLine("Set-Content -Path 'BDN_ARGS.rsp' -Value @(");
            foreach (var arg in SplitBdnArgs(job.BdnArguments))
            {
                sb.AppendLine($"    '{arg.Replace("'", "''")}'");
            }
            sb.AppendLine(")");
            sb.AppendLine();
        }

        // Launch agent
        var agentArgs = BuildAgentArgs(job, callbackUrl, hasBenchmarkFile: !string.IsNullOrWhiteSpace(job.BenchmarkCode));
        sb.AppendLine("# Launch agent");
        sb.AppendLine($"Start-Process python -ArgumentList 'egorbot-agent.py {agentArgs}' -NoNewWindow -RedirectStandardOutput agent.log -RedirectStandardError agent_err.log");

        return sb.ToString();
    }

    private static string BuildAgentArgs(BenchmarkJob job, string callbackUrl, bool hasBenchmarkFile)
    {
        var parts = new List<string>
        {
            $"--job_tag \"{job.Id}\"",
            $"--gh_commits_and_prs \"{job.CommitsAndPrs}\"",
            $"--callback_url \"{callbackUrl}\"",
            $"--job_id \"{job.Id}\"",
        };

        if (hasBenchmarkFile)
        {
            parts.Add("--bench_code_file Benchmark.cs");
            parts.Add("--bench_csproj_file bench.csproj");
        }

        if (job.UseProfiler)
        {
            parts.Add("--perf_enabled 1");
        }

        if (!string.IsNullOrWhiteSpace(job.BdnArguments))
        {
            parts.Add("--bdn_args_file BDN_ARGS.rsp");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Split BDN argument string into individual lines for .rsp file.
    /// Handles quoted tokens properly.
    /// </summary>
    private static List<string> SplitBdnArgs(string args)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '"';

        foreach (var ch in args)
        {
            if (!inQuotes && (ch == '"' || ch == '\''))
            {
                inQuotes = true;
                quoteChar = ch;
                current.Append(ch);
            }
            else if (inQuotes && ch == quoteChar)
            {
                inQuotes = false;
                current.Append(ch);
            }
            else if (!inQuotes && ch == ' ')
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        // Group --key value pairs on the same line
        var grouped = new List<string>();
        for (int i = 0; i < result.Count; i++)
        {
            if (result[i].StartsWith('-') && i + 1 < result.Count && !result[i + 1].StartsWith('-'))
            {
                grouped.Add($"{result[i]} {result[i + 1]}");
                i++;
            }
            else
            {
                grouped.Add(result[i]);
            }
        }

        return grouped;
    }
}
