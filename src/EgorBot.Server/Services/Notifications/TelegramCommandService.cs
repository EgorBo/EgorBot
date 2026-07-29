using System.Text;
using System.Text.Json;
using EgorBot.Server.Data;
using EgorBot.Server.Models;
using EgorBot.Server.Services.CloudProviders;
using EgorBot.Shared;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Server.Services.Notifications;

/// <summary>
/// Background service that polls the Telegram Bot API for incoming messages from admins.
/// Supported commands:
///   jobs   — list active (non-terminal) jobs
///   quit   — gracefully shut down the application
///   help   — show available commands
/// Only messages from the configured AdminChatId are accepted.
/// </summary>
public sealed class TelegramCommandService(
    IConfiguration config,
    IServiceScopeFactory scopeFactory,
    JobOrchestrator orchestrator,
    RuntimeSettings runtimeSettings,
    CorePoolManager corePool,
    IHostApplicationLifetime appLifetime,
    IHttpClientFactory httpFactory,
    ILogger<TelegramCommandService> logger) : BackgroundService
{
    private readonly string? _botToken = config["Telegram:BotToken"]
                                         ?? Environment.GetEnvironmentVariable("EGORBOT_TG_TOK");
    private readonly string? _adminChatId = config["Telegram:AdminChatId"]
                                            ?? Environment.GetEnvironmentVariable("EGORBOT_TG_ADMINID");

    /// <summary>
    /// Custom commands loaded from Telegram:CustomCommands config section.
    /// Key = command name (lowercase), Value = bash command to execute.
    /// Only these pre-registered commands can be run — no arbitrary bash.
    /// </summary>
    private readonly Dictionary<string, string> _customCommands = LoadCustomCommands(config);

    private long _lastUpdateId;

    private static Dictionary<string, string> LoadCustomCommands(IConfiguration cfg)
    {
        var section = cfg.GetSection("Telegram:CustomCommands");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            var name = child.Key.ToLowerInvariant();
            var cmd = child.Value;
            if (!string.IsNullOrWhiteSpace(cmd))
                result[name] = cmd;
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_adminChatId))
        {
            logger.LogInformation("TelegramCommandService disabled (BotToken or AdminChatId not configured)");
            return;
        }

        logger.LogInformation("TelegramCommandService started. Polling for admin commands (ChatId={ChatId})", _adminChatId);

        using var http = httpFactory.CreateClient();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?offset={_lastUpdateId + 1}&timeout=30&allowed_updates=[\"message\"]";
                var response = await http.GetAsync(url, stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Telegram getUpdates failed: {Status}", response.StatusCode);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.GetProperty("ok").GetBoolean())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
                {
                    _lastUpdateId = update.GetProperty("update_id").GetInt64();

                    if (!update.TryGetProperty("message", out var message))
                        continue;
                    if (!message.TryGetProperty("text", out var textEl))
                        continue;
                    if (!message.TryGetProperty("chat", out var chat))
                        continue;

                    var chatId = chat.GetProperty("id").GetInt64().ToString();
                    var text = textEl.GetString()?.Trim() ?? "";

                    // Only accept messages from the admin chat
                    if (chatId != _adminChatId)
                    {
                        logger.LogDebug("Ignoring message from non-admin chat {ChatId}", chatId);
                        continue;
                    }

                    logger.LogInformation("Telegram admin command: \"{Command}\"", text);
                    try
                    {
                        await HandleCommandAsync(text, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // A failing command must not kill the polling loop (or the host —
                        // an escaping exception from ExecuteAsync stops the whole app).
                        logger.LogError(ex, "Telegram command \"{Command}\" failed", text);
                        await SendReplyAsync($"❌ Command failed: {EscapeMarkdown(ex.Message)}");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramCommandService poll error");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("TelegramCommandService stopped");
    }

    private async Task HandleCommandAsync(string text, CancellationToken ct)
    {
        // Strip leading '/' (Telegram sends /jobs, /quit, etc.) and any "@BotName" suffix
        var command = text.TrimStart('/').Trim().ToLowerInvariant();
        var atIndex = command.IndexOf('@');
        if (atIndex > 0 && !command.Contains(' '))
            command = command[..atIndex];

        if (command.Length == 0)
            return;

        switch (command)
        {
            case "jobs":
                await HandleJobsCommandAsync(ct);
                break;
            case "allvms":
            case "vms":
                await HandleAllVmsCommandAsync(ct);
                break;
            case "cancelall":
            case "cancel":
                await HandleCancelAllAsync(ct);
                break;
            case "quota":
            case "quotas":
                await HandleQuotaCommandAsync(ct);
                break;
            case "quit":
            case "stop":
            case "shutdown":
                await SendReplyAsync("🛑 Shutting down...");
                logger.LogWarning("Admin requested shutdown via Telegram");
                appLifetime.StopApplication();
                break;
            case "help":
            case "start":
            {
                var sb = new StringBuilder();
                sb.AppendLine("📋 *Available commands:*");
                sb.AppendLine("`jobs` — list active jobs");
                sb.AppendLine("`allvms` — list all VMs across cloud providers");
                sb.AppendLine("`cores` — show current default core count");
                sb.AppendLine("`cores N` — set default core count (e.g. `cores 16`)");
                sb.AppendLine("`pool` — show core pool usage (used/total + waiters)");
                sb.AppendLine("`pool reset` — force-release leaked cores (use when jobs hang on \"Waiting for N cores\")");
                sb.AppendLine("`quota` — show real cloud vCPU quotas (used/limit per family)");
                sb.AppendLine("`cancelall` — cancel all active jobs & deprovision VMs");
                sb.AppendLine("`quit` — shut down the service");
                sb.AppendLine("`help` — show this message");
                if (_customCommands.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("🔧 *Custom commands:*");
                    foreach (var (name, cmd) in _customCommands)
                        sb.AppendLine($"`{name}` — `{EscapeMarkdown(cmd)}`");
                }
                await SendReplyAsync(sb.ToString());
                break;
            }
            default:
                if (command.StartsWith("cores"))
                {
                    await HandleCoresCommandAsync(command);
                    break;
                }
                if (command.StartsWith("pool"))
                {
                    await HandlePoolCommandAsync(command);
                    break;
                }
                // Check custom commands (registered in config)
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && _customCommands.TryGetValue(parts[0], out var bashCmd))
                {
                    _ = HandleCustomCommandAsync(parts[0], bashCmd, ct);
                    break;
                }
                await SendReplyAsync($"Unknown command: `{EscapeMarkdown(command)}`\nSend `help` for available commands.");
                break;
        }
    }

    private async Task HandleJobsCommandAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var activeStatuses = new[]
        {
            JobStatus.Pending,
            JobStatus.Provisioning,
            JobStatus.Running
        };

        var jobs = await db.Jobs
            .Where(j => activeStatuses.Contains(j.Status))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        if (jobs.Count == 0)
        {
            await SendReplyAsync("No active jobs.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"📊 *Active jobs ({jobs.Count}):*");
        sb.AppendLine();

        foreach (var job in jobs)
        {
            var age = DateTime.UtcNow - job.CreatedAt;
            var ageStr = age.TotalHours >= 1
                ? $"{age.TotalHours:F0}h{age.Minutes}m"
                : $"{age.TotalMinutes:F0}m";

            var statusEmoji = job.Status switch
            {
                JobStatus.Pending => "⏳",
                JobStatus.Provisioning => "🔄",
                JobStatus.Running => "▶️",
                _ => "❓"
            };

            sb.AppendLine($"{statusEmoji} `{job.Id.ToString()[..8]}` {EscapeMarkdown(job.Platform)} ({ageStr})");
            sb.AppendLine($"    Commits: `{job.CommitsAndPrs}`");
            if (!string.IsNullOrEmpty(job.RequestedBy))
                sb.AppendLine($"    By: @{job.RequestedBy}");
        }

        await SendReplyAsync(sb.ToString());
    }

    private async Task HandleAllVmsCommandAsync(CancellationToken ct)
    {
        await SendReplyAsync("🔍 Querying cloud providers...");

        using var scope = scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetServices<ICloudProvider>();

        var sb = new StringBuilder();
        sb.AppendLine("🖥 *All active VMs:*");
        var totalCount = 0;

        foreach (var provider in providers)
        {
            try
            {
                var vms = await provider.ListActiveVmsAsync(ct);
                sb.AppendLine();
                sb.AppendLine($"*{provider.Name}* ({vms.Count}):");
                if (vms.Count == 0)
                {
                    sb.AppendLine("  (none)");
                }
                else
                {
                    foreach (var vm in vms)
                        sb.AppendLine($"  • `{EscapeMarkdown(vm)}`");
                    totalCount += vms.Count;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list VMs from {Provider}", provider.Name);
                sb.AppendLine();
                sb.AppendLine($"*{provider.Name}*: ❌ error");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Total: *{totalCount}* VM(s)");

        await SendReplyAsync(sb.ToString());
    }

    private async Task HandleCancelAllAsync(CancellationToken ct)
    {
        await SendReplyAsync("🔄 Cancelling all active jobs...");
        var count = await orchestrator.CancelAllJobsAsync();
        await SendReplyAsync(count > 0
            ? $"✅ Cancelled {count} job(s) and deprovisioned their VMs."
            : "No active jobs to cancel.");
    }

    private async Task HandleCustomCommandAsync(string name, string bashCommand, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Executing custom command '{Name}': {Cmd}", name, bashCommand);
            await SendReplyAsync($"⏳ Running `{EscapeMarkdown(name)}`...");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // safety timeout

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c {bashCommand}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                await SendReplyAsync($"❌ Failed to start process for `{EscapeMarkdown(name)}`");
                return;
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var output = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdout))
                output.AppendLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
                output.AppendLine(stderr.TrimEnd());

            var exitCode = proc.ExitCode;
            var emoji = exitCode == 0 ? "✅" : "❌";
            var result = output.Length > 0
                ? output.ToString()
                : "(no output)";

            // Telegram message limit is ~4096 chars; truncate if needed
            if (result.Length > 3500)
                result = result[..3500] + "\n... (truncated)";

            await SendReplyAsync($"{emoji} `{EscapeMarkdown(name)}` exited with code {exitCode}:\n```\n{result}\n```");
        }
        catch (OperationCanceledException)
        {
            await SendReplyAsync($"⏰ `{EscapeMarkdown(name)}` timed out (5 min limit)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Custom command '{Name}' failed", name);
            await SendReplyAsync($"❌ `{EscapeMarkdown(name)}` failed: {EscapeMarkdown(ex.Message)}");
        }
    }

    private async Task HandleQuotaCommandAsync(CancellationToken ct)
    {
        await SendReplyAsync("🔍 Querying cloud quotas...");

        using var scope = scopeFactory.CreateScope();
        var quotaProviders = scope.ServiceProvider.GetServices<ICloudProvider>().OfType<ICoreQuotaProvider>().ToList();
        if (quotaProviders.Count == 0)
        {
            await SendReplyAsync("No cloud provider reports quotas.");
            return;
        }

        // Only show families the bot can actually use.
        var usedFamilies = TargetCatalog.GetAllTargetNames()
            .Select(TargetCatalog.GetTarget)
            .Where(t => t.InstanceName is not null)
            .Select(t => t.InstanceName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("📊 *Cloud vCPU quotas:*");

        foreach (var provider in quotaProviders)
        {
            try
            {
                var quotas = await provider.GetQuotasAsync(ct);
                var relevant = quotas
                    .Where(q => q.Limit > 0 && (q.Used > 0 || usedFamilies.Any(f => FamilyMatches(f, q.Family))))
                    .OrderByDescending(q => q.Used)
                    .ThenBy(q => q.DisplayName)
                    .Take(25)
                    .ToList();

                sb.AppendLine();
                sb.AppendLine($"*{provider.Name}*");
                if (relevant.Count == 0)
                {
                    sb.AppendLine("  (no matching families)");
                    continue;
                }

                foreach (var q in relevant)
                    sb.AppendLine($"  `{EscapeMarkdown(q.DisplayName)}` ({q.Region}): {q.Used}/{q.Limit}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read quotas from {Provider}", provider.Name);
                sb.AppendLine();
                sb.AppendLine($"*{provider.Name}*: ❌ {EscapeMarkdown(ex.Message)}");
            }
        }

        await SendReplyAsync(sb.ToString());
    }

    /// <summary>Loose match between a VM size template ("Standard_D{0}ads_v6") and a quota family name.</summary>
    private static bool FamilyMatches(string vmSizeTemplate, string quotaFamily)
    {
        var compact = vmSizeTemplate.Replace("{0}", "", StringComparison.Ordinal)
                                    .Replace("_", "", StringComparison.Ordinal);
        return quotaFamily.Replace("_", "", StringComparison.Ordinal)
                          .Contains(compact.Replace("Standard", "", StringComparison.OrdinalIgnoreCase),
                                    StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandlePoolCommandAsync(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length > 1 && parts[1] is "reset" or "clear")
        {
            var leaked = corePool.ResetAll();
            logger.LogWarning("Admin reset the core pool ({Cores} cores were marked as used)", leaked);
            await SendReplyAsync($"♻️ Core pool reset — released *{leaked}* core(s) that were marked as in use.");
            return;
        }

        var snapshot = corePool.GetSnapshot();
        if (snapshot.Count == 0)
        {
            await SendReplyAsync("🧮 Core pool is empty (no rents yet).");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("🧮 *Core pools:*");
        foreach (var (key, (used, total, waiters)) in snapshot.OrderBy(p => p.Key))
        {
            sb.AppendLine($"`{EscapeMarkdown(key)}`: {used}/{total} used"
                          + (waiters > 0 ? $", {waiters} waiting" : ""));
        }
        sb.AppendLine();
        sb.AppendLine($"Default cores per job: *{runtimeSettings.DefaultCores}*");
        await SendReplyAsync(sb.ToString());
    }

    private async Task HandleCoresCommandAsync(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            // Just "cores" — show current value
            await SendReplyAsync($"⚙️ Default cores: *{runtimeSettings.DefaultCores}*");
            return;
        }

        if (int.TryParse(parts[1], out var newCores) && newCores is >= 1 and <= 64)
        {
            var old = runtimeSettings.DefaultCores;
            runtimeSettings.DefaultCores = newCores;
            logger.LogInformation("Admin changed DefaultCores: {Old} → {New}", old, newCores);
            await SendReplyAsync($"✅ Default cores changed: {old} → *{newCores}*");
        }
        else
        {
            await SendReplyAsync("❌ Invalid value. Usage: `cores N` (1–64)");
        }
    }

    private async Task SendReplyAsync(string text)
    {
        if (_botToken is null || _adminChatId is null) return;

        try
        {
            using var http = httpFactory.CreateClient();
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new Dictionary<string, string>
            {
                ["chat_id"] = _adminChatId,
                ["text"] = text,
                ["parse_mode"] = "Markdown",
                ["disable_web_page_preview"] = "true"
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await http.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                logger.LogWarning("Telegram sendMessage failed ({Status}): {Body}", response.StatusCode, body);

                // Retry without Markdown (in case of escaping issues)
                payload.Remove("parse_mode");
                content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                await http.PostAsync(url, content);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send Telegram reply");
        }
    }

    private static string EscapeMarkdown(string text)
    {
        return text
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("`", "\\`");
    }
}
