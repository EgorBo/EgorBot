using EgorBot.Server.Models;

namespace EgorBot.Server.Services.Notifications;

/// <summary>
/// Abstract notification service — called when jobs start, complete, or fail.
/// Register multiple implementations to fan out notifications (console, Telegram, etc.).
/// </summary>
public interface INotificationService
{
    Task OnJobStartedAsync(BenchmarkJob job);
    Task OnVmProvisionedAsync(BenchmarkJob job, string providerName, string? ipAddress);
    Task OnJobCompletedAsync(BenchmarkJob job);
    Task OnJobFailedAsync(BenchmarkJob job, string error);
}
