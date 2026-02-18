using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EgorBot.BenchmarkValidator.Services;

/// <summary>
/// Validates BDN benchmark snippets by building and running them with <c>--list flat</c>.
/// </summary>
public sealed partial class BenchmarkValidationService(IConfiguration config, ILogger<BenchmarkValidationService> logger)
{
    // ── Configuration ────────────────────────────────────────────────────

    private string Tfm => config["Validator:TargetFramework"] ?? "net10.0";
    private int MaxBenchmarkCount => config.GetValue("Validator:MaxBenchmarkCount", 40);
    private int BuildTimeoutSec => config.GetValue("Validator:BuildTimeoutSeconds", 120);
    private int RunTimeoutSec => config.GetValue("Validator:RunTimeoutSeconds", 60);

    private string WorkRoot
    {
        get
        {
            var dir = config["Validator:WorkDirectory"];
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Path.GetTempPath(), "egorbot-validator");
            return dir;
        }
    }

    // ── Pre-built .csproj template ───────────────────────────────────────

    private static string BuildCsproj(string tfm) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{tfm}</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            <LangVersion>preview</LangVersion>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
          </ItemGroup>
        </Project>
        """;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Validate a benchmark snippet by building and running it with <c>--list flat</c>.
    /// Returns (isValid, benchmarkCount, errorMessage).
    /// </summary>
    public async Task<(bool IsValid, int BenchmarkCount, string? Error)> ValidateAsync(
        string benchmarkCode, string? bdnArguments, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var workDir = Path.Combine(WorkRoot, runId);

        try
        {
            Directory.CreateDirectory(workDir);

            // 1. Write project files
            var csproj = Path.Combine(workDir, "BenchValidator.csproj");
            var programCs = Path.Combine(workDir, "Program.cs");

            await File.WriteAllTextAsync(csproj, BuildCsproj(Tfm), ct);
            await File.WriteAllTextAsync(programCs, benchmarkCode, ct);

            // 2. Build
            logger.LogInformation("[{RunId}] Building benchmark in {WorkDir}", runId, workDir);
            var (buildOk, buildOutput) = await RunProcessAsync(
                "dotnet", $"build -c Release --nologo -v q",
                workDir, BuildTimeoutSec, ct);

            if (!buildOk)
            {
                logger.LogWarning("[{RunId}] Build failed:\n{Output}", runId, buildOutput);
                return (false, 0, $"Benchmark build failed:\n```\n{TrimOutput(buildOutput, 2000)}\n```");
            }

            // 3. Run with --list flat
            var listArgs = BuildListArgs(bdnArguments);
            logger.LogInformation("[{RunId}] Running: dotnet run -c Release --no-build -- {Args}", runId, listArgs);
            var (runOk, runOutput) = await RunProcessAsync(
                "dotnet", $"run -c Release --no-build -- {listArgs}",
                workDir, RunTimeoutSec, ct);

            if (!runOk)
            {
                logger.LogWarning("[{RunId}] Run failed:\n{Output}", runId, runOutput);
                return (false, 0, $"Benchmark `--list flat` failed:\n```\n{TrimOutput(runOutput, 2000)}\n```");
            }

            // 4. Parse output
            return ParseListOutput(runOutput, runId);
        }
        finally
        {
            // Clean up
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{RunId}] Failed to clean up {WorkDir}", runId, workDir);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string BuildListArgs(string? bdnArguments)
    {
        // Append --list flat to user's BDN arguments.
        // If user already has --list, replace it with --list flat.
        var args = bdnArguments ?? "";
        if (ListArgRegex().IsMatch(args))
        {
            args = ListArgRegex().Replace(args, "--list flat");
        }
        else
        {
            args = $"{args} --list flat".Trim();
        }
        return args;
    }

    private (bool IsValid, int BenchmarkCount, string? Error) ParseListOutput(string output, string runId)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Check for known error signatures
        if (lines.Any(l => l.Contains("No benchmarks found", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning("[{RunId}] No benchmarks found in output", runId);
            return (false, 0, "No benchmarks found. Make sure your class has `[Benchmark]` methods and is `public`.");
        }

        // BDN --list flat prints one benchmark per line as "Namespace.Class.Method"
        // Filter out noise lines (BDN logo, blank, etc.) — benchmark lines contain at least one '.'
        var benchmarkLines = lines
            .Where(l => l.Contains('.') && !l.StartsWith("//") && !l.StartsWith('#'))
            .ToList();

        if (benchmarkLines.Count == 0)
        {
            logger.LogWarning("[{RunId}] Could not find any benchmark entries in output:\n{Output}", runId, output);
            return (false, 0, $"Could not find benchmark entries in `--list flat` output:\n```\n{TrimOutput(output, 1500)}\n```");
        }

        if (benchmarkLines.Count > MaxBenchmarkCount)
        {
            logger.LogWarning("[{RunId}] Too many benchmarks: {Count} (max {Max})", runId, benchmarkLines.Count, MaxBenchmarkCount);
            return (false, benchmarkLines.Count,
                $"Too many benchmarks: {benchmarkLines.Count} (max {MaxBenchmarkCount}). " +
                $"Use `--filter` to reduce the number of benchmarks.");
        }

        logger.LogInformation("[{RunId}] Validation passed: {Count} benchmark(s)", runId, benchmarkLines.Count);
        return (true, benchmarkLines.Count, null);
    }

    private static async Task<(bool Success, string Output)> RunProcessAsync(
        string fileName, string arguments, string workDir, int timeoutSec, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Read stdout and stderr concurrently
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (false, "Process timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = $"{stdout}\n{stderr}".Trim();

        return (proc.ExitCode == 0, combined);
    }

    private static string TrimOutput(string output, int maxLen) =>
        output.Length <= maxLen ? output : output[..maxLen] + "\n... (truncated)";

    [GeneratedRegex(@"--list\s+\w+", RegexOptions.IgnoreCase)]
    private static partial Regex ListArgRegex();
}
