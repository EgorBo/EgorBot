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
    public string Build(BenchmarkJob job, bool skipDeps = false)
    {
        var commonUrl = config["EgorBot:AgentScriptCommonUrl"]
            ?? throw new InvalidOperationException("EgorBot:AgentScriptCommonUrl configuration is required");
        var serviceBaseUrl = config["EgorBot:ServiceBaseUrl"]
                             ?? throw new InvalidOperationException("EgorBot:ServiceBaseUrl configuration is required");
        var callbackUrl = $"{serviceBaseUrl.TrimEnd('/')}/api/internal";
        var csprojUrl = config["EgorBot:DefaultCsprojUrl"] 
                        ?? throw new InvalidOperationException("EgorBot:DefaultCsprojUrl configuration is required");

        var isWindows = Platform.IsWindows(job.Platform);
        var isMacOs = Platform.GetOs(job.Platform).Equals("osx", StringComparison.OrdinalIgnoreCase);

        var platformUrl = isWindows
            ? config["EgorBot:AgentScriptWindowsUrl"]
              ?? throw new InvalidOperationException("EgorBot:AgentScriptWindowsUrl configuration is required")
            : isMacOs
                ? config["EgorBot:AgentScriptMacosUrl"]
                  ?? throw new InvalidOperationException("EgorBot:AgentScriptMacosUrl configuration is required")
                : config["EgorBot:AgentScriptLinuxUrl"]
                  ?? throw new InvalidOperationException("EgorBot:AgentScriptLinuxUrl configuration is required");

        var platformFileName = isWindows ? "egorbot-agent-windows.py"
            : isMacOs ? "egorbot-agent-macos.py"
            : "egorbot-agent-linux.py";

        return isWindows
            ? BuildWindowsScript(job, commonUrl, platformUrl, platformFileName, callbackUrl, csprojUrl, skipDeps)
            : BuildLinuxScript(job, commonUrl, platformUrl, platformFileName, callbackUrl, csprojUrl, skipDeps);
    }

    private static string BuildLinuxScript(BenchmarkJob job, string commonUrl, string platformUrl,
        string platformFileName, string callbackUrl, string csprojUrl, bool skipDeps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.AppendLine();
        sb.AppendLine("cd /home");
        sb.AppendLine("mkdir -p egorbot_work");
        sb.AppendLine("cd egorbot_work");
        sb.AppendLine();

        // Download the agent scripts (common + platform-specific)
        sb.AppendLine($"curl -sL -o egorbot-agent-common.py \"{commonUrl}\"");
        sb.AppendLine($"curl -sL -o {platformFileName} \"{platformUrl}\"");
        sb.AppendLine("chmod +x egorbot-agent-common.py");
        sb.AppendLine($"chmod +x {platformFileName}");
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
            sb.AppendLine($"curl -sL -o bench.csproj \"{csprojUrl}\"");
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

        // Compose the agent invocation — use HELIX_PYTHONPATH if set, else python3
        var agentArgs = BuildAgentArgs(job, callbackUrl, hasBenchmarkFile: !string.IsNullOrWhiteSpace(job.BenchmarkCode), skipDeps: skipDeps);

        sb.AppendLine("# Resolve Python");
        sb.AppendLine("PYTHON=${HELIX_PYTHONPATH:-python3}");
        sb.AppendLine("echo \"Using Python: $PYTHON\"");
        sb.AppendLine();
        sb.AppendLine("# Launch agent in background (tee to both file and cloud-init log)");
        sb.AppendLine($"nohup $PYTHON egorbot-agent-common.py {agentArgs} 2>&1 | tee agent.log &");

        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string BuildWindowsScript(BenchmarkJob job, string commonUrl, string platformUrl,
        string platformFileName, string callbackUrl, string csprojUrl, bool skipDeps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PowerShell bootstrap for EgorBot agent");
        sb.AppendLine();
        // TLS 1.2 for HTTPS downloads (Windows PowerShell 5.1 default is TLS 1.0)
        sb.AppendLine("[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12");
        sb.AppendLine();
        sb.AppendLine("$workDir = 'C:\\egorbot_work'");
        sb.AppendLine("New-Item -ItemType Directory -Force -Path $workDir | Out-Null");
        sb.AppendLine("Set-Location $workDir");
        sb.AppendLine();

        // Error reporting helper — posts a log line to EgorBot before the agent is running
        sb.AppendLine("function Report-Error($msg) {");
        sb.AppendLine("    Write-Host \"FATAL: $msg\"");
        sb.AppendLine($"    try {{ $body = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json @($msg)))");
        sb.AppendLine($"        Invoke-WebRequest -Uri '{callbackUrl}/jobs/{job.Id}/logs' -Method POST -ContentType 'application/json' -Body $body -UseBasicParsing -ErrorAction SilentlyContinue | Out-Null");
        sb.AppendLine($"    }} catch {{}}");
        sb.AppendLine("}");
        sb.AppendLine();

        // Find Python executable: HELIX_PYTHONPATH → python3 → python → py → download embeddable
        // Must verify each candidate actually works (Windows Store stubs appear in Get-Command but fail)
        sb.AppendLine("Write-Host 'Searching for Python...'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("$python = $null");
        sb.AppendLine("if ($env:HELIX_PYTHONPATH -and (Test-Path $env:HELIX_PYTHONPATH)) { $python = $env:HELIX_PYTHONPATH }");
        sb.AppendLine("if (-not $python) {");
        sb.AppendLine("    foreach ($cmd in @('python3', 'python', 'py')) {");
        sb.AppendLine("        if (Get-Command $cmd -ErrorAction SilentlyContinue) {");
        sb.AppendLine("            try { $out = & $cmd --version 2>&1; if ($LASTEXITCODE -eq 0) { $python = $cmd; break } } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $python) {");
        sb.AppendLine("    Write-Host 'Python not found, downloading embeddable Python...'");
        sb.AppendLine("    Report-Error 'Python not found — downloading embeddable Python...'");
        sb.AppendLine("    try {");
        sb.AppendLine("        $pyVer = '3.12.8'");
        sb.AppendLine("        $pyZip = Join-Path $workDir \"python-$pyVer-embed-amd64.zip\"");
        sb.AppendLine("        Invoke-WebRequest -Uri \"https://www.python.org/ftp/python/$pyVer/python-$pyVer-embed-amd64.zip\" -OutFile $pyZip -UseBasicParsing");
        sb.AppendLine("        $pyDir = Join-Path $workDir 'python-embed'");
        sb.AppendLine("        Expand-Archive $pyZip -DestinationPath $pyDir -Force");
        sb.AppendLine("        $pthFile = Get-ChildItem $pyDir -Filter '*._pth' | Select-Object -First 1");
        sb.AppendLine("        if ($pthFile) { (Get-Content $pthFile.FullName) -replace '#import site', 'import site' | Set-Content $pthFile.FullName }");
        sb.AppendLine("        $python = Join-Path $pyDir 'python.exe'");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        Report-Error \"Failed to download Python: $_\"");
        sb.AppendLine("        exit 1");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host \"Using Python: $python\"");
        sb.AppendLine();

        // Download agent scripts (common + platform-specific)
        sb.AppendLine("try {");
        sb.AppendLine($"    Invoke-WebRequest -Uri '{commonUrl}' -OutFile 'egorbot-agent-common.py' -UseBasicParsing");
        sb.AppendLine($"    Invoke-WebRequest -Uri '{platformUrl}' -OutFile '{platformFileName}' -UseBasicParsing");
        sb.AppendLine("} catch {");
        sb.AppendLine("    Report-Error \"Failed to download agent scripts: $_\"");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine();

        // Write benchmark code
        if (!string.IsNullOrWhiteSpace(job.BenchmarkCode))
        {
            sb.AppendLine("# Write benchmark code");
            sb.AppendLine($"Set-Content -Path 'Benchmark.cs' -Encoding UTF8 -Value @'");
            sb.AppendLine(job.BenchmarkCode);
            sb.AppendLine("'@");
            sb.AppendLine();

            sb.AppendLine("try {");
            sb.AppendLine($"    Invoke-WebRequest -Uri '{csprojUrl}' -OutFile 'bench.csproj' -UseBasicParsing");
            sb.AppendLine("} catch {");
            sb.AppendLine("    Report-Error \"Failed to download csproj template: $_\"");
            sb.AppendLine("    exit 1");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Write BDN args
        if (!string.IsNullOrWhiteSpace(job.BdnArguments))
        {
            sb.AppendLine("# Write BDN arguments");
            sb.AppendLine("Set-Content -Path 'BDN_ARGS.rsp' -Encoding UTF8 -Value @(");
            foreach (var arg in SplitBdnArgs(job.BdnArguments))
            {
                sb.AppendLine($"    '{arg.Replace("'", "''")}'");
            }
            sb.AppendLine(")");
            sb.AppendLine();
        }

        // Launch agent — use direct invocation so stdout/stderr flow to the parent process
        var agentArgs = BuildAgentArgs(job, callbackUrl, hasBenchmarkFile: !string.IsNullOrWhiteSpace(job.BenchmarkCode), skipDeps: skipDeps);
        sb.AppendLine("# Launch agent");
        sb.AppendLine("Write-Host 'Launching agent...'");
        sb.AppendLine("try {");
        sb.AppendLine($"    & $python egorbot-agent-common.py {agentArgs} 2>&1 | Tee-Object -FilePath agent.log");
        sb.AppendLine("} catch {");
        sb.AppendLine("    Report-Error \"Agent process failed: $_\"");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host 'Agent process completed.'");

        return sb.ToString();
    }

    private static string BuildAgentArgs(BenchmarkJob job, string callbackUrl, bool hasBenchmarkFile, bool skipDeps = false)
    {
        var parts = new List<string>
        {
            $"--job_tag \"{job.Id}\"",
            $"--callback_url \"{callbackUrl}\"",
            $"--job_id \"{job.Id}\"",
        };

        if (!string.IsNullOrWhiteSpace(job.CommitsAndPrs))
        {
            parts.Add($"--gh_commits_and_prs \"{job.CommitsAndPrs}\"");
        }

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

        if (skipDeps)
        {
            parts.Add("--skip_deps 1");
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
