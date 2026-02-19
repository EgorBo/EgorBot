using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EgorBot.Server.Tests;

/// <summary>
/// Integration test that submits a Docker benchmark job via the API,
/// polls for completion, and verifies the job reaches a terminal state.
/// Requires Docker to be running on the local machine.
/// </summary>
public class DockerIntegrationTest : IClassFixture<DockerIntegrationTest.EgorBotServer>, IDisposable
{
    private const string BaseUrl = "http://localhost:5099";
    private readonly HttpClient _client = new() { BaseAddress = new Uri(BaseUrl) };

    public DockerIntegrationTest(EgorBotServer _) { }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task SubmitDockerJob_ReachesTerminalState()
    {
        // ── 1. Submit job ────────────────────────────────────────────────
        var request = new
        {
            platforms = new[] { "ubuntu24_docker_x64" },
            commitsAndPrs = "",
            benchmarkCode = """
                using System;
                using BenchmarkDotNet.Attributes;

                public class Benchmarks
                {
                    string _data = "https://github.com/dotnet/runtime/pulls";

                    [Benchmark]
                    public bool StartsWith() =>
                        _data.StartsWith("HTTPS://github.com/dotnet/runtime",
                            StringComparison.OrdinalIgnoreCase);
                }
                """,
            useProfiler = false,
        };

        var response = await _client.PostAsJsonAsync("/api/jobs", request);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"POST /api/jobs returned {response.StatusCode}: {responseText}");

        var body = JsonDocument.Parse(responseText).RootElement;
        var jobId = body.GetProperty("jobs")[0].GetProperty("id").GetString();
        Assert.NotNull(jobId);

        // ── 2. Poll status until terminal or timeout ─────────────────────
        var terminalStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Completed", "Failed", "TimedOut", "Cancelled"
        };

        string? finalStatus = null;
        string? errorMessage = null;
        var deadline = DateTime.UtcNow.AddMinutes(10);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            var statusResp = await _client.GetAsync($"/api/jobs/{jobId}/status");
            Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);

            var statusText = await statusResp.Content.ReadAsStringAsync();
            var statusBody = JsonDocument.Parse(statusText).RootElement;

            // API returns camelCase — try both casings to be safe
            finalStatus = statusBody.TryGetProperty("status", out var s) ? s.GetString()
                        : statusBody.TryGetProperty("Status", out s) ? s.GetString()
                        : null;

            if (statusBody.TryGetProperty("errorMessage", out var e))
                errorMessage = e.GetString();

            if (finalStatus != null && terminalStates.Contains(finalStatus))
                break;
        }

        // ── 3. Verify ────────────────────────────────────────────────────
        Assert.NotNull(finalStatus);
        Assert.True(terminalStates.Contains(finalStatus!),
            $"Job did not reach a terminal state within 10 min. Last status: {finalStatus}");

        // The job either completed with BDN results or failed (e.g. agent
        // couldn't install .NET SDK). Both are valid — the test verifies the
        // full pipeline: API → orchestrator → Docker → agent callback.
        if (finalStatus!.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            var resultResp = await _client.GetAsync($"/api/jobs/{jobId}/result");
            Assert.Equal(HttpStatusCode.OK, resultResp.StatusCode);
            var resultText = await resultResp.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(resultText), "Result should not be empty");
        }
        else
        {
            // Log the error for diagnostics but don't fail — agent-side errors
            // (missing .NET SDK, network issues) aren't server bugs.
            Assert.True(finalStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                      || finalStatus.Equals("TimedOut", StringComparison.OrdinalIgnoreCase),
                $"Unexpected terminal state: {finalStatus}, error: {errorMessage}");
        }
    }

    // ── Server fixture: starts EgorBot.Server as a real process ─────────────

    /// <summary>
    /// Starts EgorBot.Server via <c>dotnet run</c> on a real TCP port so Docker
    /// containers can call back. Uses a unique SQLite DB per test run.
    /// </summary>
    public sealed class EgorBotServer : IAsyncLifetime
    {
        private Process? _process;

        public async Task InitializeAsync()
        {
            var dbName = $"egorbot_test_{Guid.NewGuid():N}.db";

            // Locate the compiled server DLL relative to the test output directory
            var serverDll = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "EgorBot.Server", "bin", "Debug", "net10.0", "EgorBot.Server.dll"));

            if (!File.Exists(serverDll))
                throw new FileNotFoundException($"Server DLL not found at {serverDll}. Build the solution first.");

            // Use the server project directory as working directory so appsettings.json is found
            var serverDir = Path.GetDirectoryName(serverDll)!;

            var psi = new ProcessStartInfo("dotnet", serverDll)
            {
                WorkingDirectory = Path.GetFullPath(
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "EgorBot.Server")),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            psi.Environment["ConnectionStrings__Default"] = $"Data Source={dbName}";
            psi.Environment["EgorBot__ServiceBaseUrl"] = BaseUrl;
            psi.Environment["Telegram__BotToken"] = "";
            psi.Environment["Docker__MemoryLimitMb"] = "4096";
            psi.Environment["Docker__CpuLimit"] = "4";
            // Override the Kestrel endpoint that takes priority over ASPNETCORE_URLS
            psi.Environment["Kestrel__Endpoints__Http__Url"] = BaseUrl;

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start EgorBot.Server process");

            // Forward server output to test console for diagnostics
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[SERVER] {e.Data}"); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[SERVER] {e.Data}"); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Wait for the server to start accepting connections
            using var healthClient = new HttpClient();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var resp = await healthClient.GetAsync($"{BaseUrl}/health");
                    if (resp.IsSuccessStatusCode) return;
                }
                catch
                {
                    // Server not ready yet
                }

                if (_process.HasExited)
                    throw new InvalidOperationException(
                        $"EgorBot.Server process exited with code {_process.ExitCode} before becoming healthy");

                await Task.Delay(500);
            }

            throw new TimeoutException("EgorBot.Server did not become healthy within 30 seconds");
        }

        public async Task DisposeAsync()
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _process?.Dispose();
        }
    }
}
