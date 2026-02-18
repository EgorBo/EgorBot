using System.Text;
using System.Text.Json;
using EgorBot.Server.Data;
using EgorBot.Server.Models;
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
    IHostApplicationLifetime appLifetime,
    IHttpClientFactory httpFactory,
    ILogger<TelegramCommandService> logger) : BackgroundService
{
    private readonly string? _botToken = config["Telegram:BotToken"]
                                         ?? Environment.GetEnvironmentVariable("EGORBOT_TG_TOK");
    private readonly string? _adminChatId = config["Telegram:AdminChatId"]
                                            ?? Environment.GetEnvironmentVariable("EGORBOT_TG_ADMINID");

    private long _lastUpdateId;

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
                    await HandleCommandAsync(text, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramCommandService poll error");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("TelegramCommandService stopped");
    }

    private async Task HandleCommandAsync(string text, CancellationToken ct)
    {
        // Strip leading '/' (Telegram sends /jobs, /quit, etc.)
        var command = text.TrimStart('/').ToLowerInvariant();

        switch (command)
        {
            case "jobs":
                await HandleJobsCommandAsync(ct);
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
                await SendReplyAsync(
                    "📋 *Available commands:*\n" +
                    "`jobs` — list active jobs\n" +
                    "`quit` — shut down the service\n" +
                    "`help` — show this message");
                break;
            default:
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
