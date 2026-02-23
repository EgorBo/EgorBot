using System.Text;
using EgorBot.Server.Models;
using EgorBot.Shared;

namespace EgorBot.Server.Services.CloudInit;

/// <summary>
/// Composes the cloud-init / bootstrap script that will run on the provisioned VM.
/// Downloads agent scripts from the EgorBot repo's ClientData folder and launches the agent.
/// </summary>
public sealed class CloudInitBuilder(IConfiguration config)
{
    // GitHub archive URL — downloads the entire repo as a tarball/zip so we can
    // extract just the src/ClientData/ folder (all agent scripts + csproj template).
    private const string RepoArchiveBase = "https://github.com/EgorBo/EgorBot/archive/refs/heads/main";
    private const string TarballUrl = $"{RepoArchiveBase}.tar.gz";
    private const string ZipUrl = $"{RepoArchiveBase}.zip";
    // Inside the archive, files live under this prefix:
    private const string ArchivePrefix = "EgorBot-main/src/ClientData";

    /// <summary>
    /// Build the cloud-init script for a given job.
    /// </summary>
    public string Build(BenchmarkJob job, bool skipDeps = false)
    {
        var serviceBaseUrl = config["EgorBot:ServiceBaseUrl"]
                             ?? throw new InvalidOperationException("EgorBot:ServiceBaseUrl configuration is required");
        var callbackUrl = $"{serviceBaseUrl.TrimEnd('/')}/api/internal";

        var target = TargetCatalog.GetTarget(job.Platform);
        var isWindows = target.OsFamily == "windows";

        return isWindows
            ? BuildWindowsScript(job, callbackUrl, skipDeps)
            : BuildLinuxScript(job, callbackUrl, skipDeps);
    }

    private static string BuildLinuxScript(BenchmarkJob job, string callbackUrl, bool skipDeps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.AppendLine();
        sb.AppendLine("cd /home");
        sb.AppendLine("mkdir -p egorbot_work");
        sb.AppendLine("cd egorbot_work");
        sb.AppendLine();

        // Download all agent scripts from the repo's ClientData folder via tarball
        sb.AppendLine("# Download agent scripts from GitHub repo");
        sb.AppendLine($"curl -sL \"{TarballUrl}\" | tar xz --strip-components=3 \"{ArchivePrefix}/\"");
        sb.AppendLine("chmod +x *.py");
        sb.AppendLine();

        // Write benchmark code file if provided
        if (!string.IsNullOrWhiteSpace(job.BenchmarkCode))
        {
            sb.AppendLine("# Write benchmark code");
            sb.AppendLine($"cat > Benchmark.cs << 'EGORBOT_BENCH_EOF'");
            sb.AppendLine(job.BenchmarkCode);
            sb.AppendLine("EGORBOT_BENCH_EOF");
            sb.AppendLine();

            // benchapp.csproj was already downloaded from the tarball
            sb.AppendLine("cp benchapp.csproj bench.csproj");
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
        sb.AppendLine($"nohup $PYTHON bdn-benchmarking-common.py {agentArgs} 2>&1 | tee agent.log &");

        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string BuildWindowsScript(BenchmarkJob job, string callbackUrl, bool skipDeps)
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

        // Download all agent scripts from the repo's ClientData folder via zip
        sb.AppendLine("# Download agent scripts from GitHub repo");
        sb.AppendLine("try {");
        sb.AppendLine($"    Invoke-WebRequest -Uri '{ZipUrl}' -OutFile 'repo.zip' -UseBasicParsing");
        sb.AppendLine("    Expand-Archive 'repo.zip' -DestinationPath 'repo-tmp' -Force");
        sb.AppendLine($"    Copy-Item 'repo-tmp\\{ArchivePrefix.Replace('/', '\\')}\\*' -Destination . -Force");
        sb.AppendLine("    Remove-Item 'repo.zip', 'repo-tmp' -Recurse -Force");
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

            // benchapp.csproj was already downloaded from the zip
            sb.AppendLine("Copy-Item 'benchapp.csproj' 'bench.csproj' -Force");
            sb.AppendLine();
        }

        // Write BDN args
        if (!string.IsNullOrWhiteSpace(job.BdnArguments))
        {
            sb.AppendLine("# Write BDN arguments (UTF8NoBOM to avoid BOM corrupting args)");
            sb.AppendLine("[System.IO.File]::WriteAllLines((Join-Path $workDir 'BDN_ARGS.rsp'), @(");
            foreach (var arg in SplitBdnArgs(job.BdnArguments))
            {
                sb.AppendLine($"    '{arg.Replace("'", "''")}'");
            }
            sb.AppendLine("), (New-Object System.Text.UTF8Encoding $false))");
            sb.AppendLine();
        }

        // Launch agent — use direct invocation so stdout/stderr flow to the parent process
        var agentArgs = BuildAgentArgs(job, callbackUrl, hasBenchmarkFile: !string.IsNullOrWhiteSpace(job.BenchmarkCode), skipDeps: skipDeps);
        sb.AppendLine("# Launch agent");
        sb.AppendLine("Write-Host 'Launching agent...'");
        sb.AppendLine("try {");
        sb.AppendLine($"    & $python bdn-benchmarking-common.py {agentArgs} 2>&1 | Tee-Object -FilePath agent.log");
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

        if (job.Attempts > 1)
        {
            parts.Add($"--attempts {job.Attempts}");
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
