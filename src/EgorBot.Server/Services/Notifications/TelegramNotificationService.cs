using System.Text;
using System.Text.Json;
using EgorBot.Server.Models;

namespace EgorBot.Server.Services.Notifications;

/// <summary>
/// Sends job notifications to a Telegram chat via the Bot API.
/// Optional — if Telegram:BotToken is not configured, all methods are no-ops.
/// Config keys:
///   Telegram:BotToken   — Bot API token (from @BotFather)
///   Telegram:AdminChatId — Chat ID (user or group) to send notifications to
/// Env-var fallbacks: EGORBOT_TG_TOK, EGORBOT_TG_ADMINID
/// </summary>
public sealed class TelegramNotificationService : INotificationService
{
    private readonly ILogger<TelegramNotificationService> _logger;
    private readonly string? _botToken;
    private readonly string? _chatId;
    private readonly IHttpClientFactory _httpFactory;

    public TelegramNotificationService(
        IConfiguration config,
        ILogger<TelegramNotificationService> logger,
        IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _httpFactory = httpFactory;

        _botToken = config["Telegram:BotToken"]
            ?? Environment.GetEnvironmentVariable("EGORBOT_TG_TOK");
        _chatId = config["Telegram:AdminChatId"]
            ?? Environment.GetEnvironmentVariable("EGORBOT_TG_ADMINID");

        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId))
        {
            _botToken = null;
            _chatId = null;
            _logger.LogInformation("Telegram notifications disabled (BotToken or AdminChatId not configured)");
        }
        else
        {
            _logger.LogInformation("Telegram notifications enabled (ChatId={ChatId})", _chatId);
        }
    }

    private bool IsEnabled => _botToken is not null;

    public async Task OnJobStartedAsync(BenchmarkJob job)
    {
        if (!IsEnabled) return;

        var sourceHint = !string.IsNullOrEmpty(job.SourceUrl)
            ? $"\nSource: {job.SourceUrl}"
            : "";
        var requestedByHint = !string.IsNullOrEmpty(job.RequestedBy)
            ? $"\nRequested by: @{job.RequestedBy}"
            : "";

        var msg = $"""
            🚀 *Job started*
            ID: `{job.Id}`
            Platform: `{job.Platform}`
            Commits: `{job.CommitsAndPrs}`{requestedByHint}{sourceHint}
            """;
        await SendMessageAsync(msg);
    }

    public async Task OnVmProvisionedAsync(BenchmarkJob job, string providerName, string? ipAddress)
    {
        if (!IsEnabled) return;

        var sshLine = ipAddress is not null
            ? $"\nSSH: `ssh ubuntu@{ipAddress}`"
            : "";
        var sourceHint = !string.IsNullOrEmpty(job.SourceUrl)
            ? $"\nSource: {job.SourceUrl}"
            : "";

        var msg = $"""
            🖥 *VM provisioned*
            ID: `{job.Id}`
            Platform: `{job.Platform}`
            Provider: {providerName}
            IP: `{ipAddress ?? "N/A"}`{sshLine}{sourceHint}
            """;
        await SendMessageAsync(msg);
    }

    public async Task OnJobCompletedAsync(BenchmarkJob job)
    {
        if (!IsEnabled) return;

        var sourceHint = !string.IsNullOrEmpty(job.SourceUrl)
            ? $"\nSource: {job.SourceUrl}"
            : "";

        var msg = $"""
            ✅ *Job completed*
            ID: `{job.Id}`
            Platform: `{job.Platform}`
            Commits: `{job.CommitsAndPrs}`{sourceHint}
            """;
        await SendMessageAsync(msg);
    }

    public async Task OnJobFailedAsync(BenchmarkJob job, string error)
    {
        if (!IsEnabled) return;

        var instanceHint = !string.IsNullOrEmpty(job.CloudProviderInstanceId)
            ? $"\nInstance: `{job.CloudProviderInstanceId}`"
            : "";
        var sourceHint = !string.IsNullOrEmpty(job.SourceUrl)
            ? $"\nSource: {job.SourceUrl}"
            : "";

        var msg = $"""
            ❌ *Job failed*
            ID: `{job.Id}`
            Platform: `{job.Platform}`
            Commits: `{job.CommitsAndPrs}`{instanceHint}{sourceHint}
            Error: {EscapeMarkdown(error)}
            """;
        await SendMessageAsync(msg);
    }

    /// <summary>
    /// Send a text message to the configured admin chat.
    /// Tries Markdown first; falls back to plain text on failure.
    /// </summary>
    private async Task SendMessageAsync(string text)
    {
        // Try with Markdown first
        if (await SendMessageCoreAsync(text, "Markdown"))
            return;

        // Fallback: plain text (in case of unescaped Markdown characters)
        await SendMessageCoreAsync(text, null);
    }

    private async Task<bool> SendMessageCoreAsync(string text, string? parseMode)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new Dictionary<string, string>
            {
                ["chat_id"] = _chatId!,
                ["text"] = text,
                ["disable_web_page_preview"] = "true"
            };
            if (parseMode is not null)
                payload["parse_mode"] = parseMode;

            using var http = _httpFactory.CreateClient();
            var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await http.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Telegram sendMessage failed ({Status}): {Body}",
                    response.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram sendMessage failed");
            return false;
        }
    }

    private static string EscapeMarkdown(string text)
    {
        // Escape characters that break Telegram Markdown v1
        return text
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("`", "\\`");
    }
}
