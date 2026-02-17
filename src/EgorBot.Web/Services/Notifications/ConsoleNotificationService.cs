using EgorBot.Web.Models;

namespace EgorBot.Web.Services.Notifications;

/// <summary>
/// Default notification service that simply logs to the console/logger.
/// </summary>
public sealed class ConsoleNotificationService(ILogger<ConsoleNotificationService> logger) : INotificationService
{
    public Task OnJobStartedAsync(BenchmarkJob job)
    {
        logger.LogInformation(
            "[Notification] Job {JobId} started — platform={Platform}, commits={Commits}",
            job.Id, job.Platform, job.CommitsAndPrs);
        return Task.CompletedTask;
    }

    public Task OnVmProvisionedAsync(BenchmarkJob job, string providerName, string? ipAddress)
    {
        logger.LogInformation(
            "[Notification] Job {JobId} VM provisioned — provider={Provider}, IP={IP}",
            job.Id, providerName, ipAddress ?? "N/A");
        return Task.CompletedTask;
    }

    public Task OnJobCompletedAsync(BenchmarkJob job)
    {
        logger.LogInformation(
            "[Notification] Job {JobId} completed — platform={Platform}",
            job.Id, job.Platform);
        return Task.CompletedTask;
    }

    public Task OnJobFailedAsync(BenchmarkJob job, string error)
    {
        logger.LogWarning(
            "[Notification] Job {JobId} FAILED — platform={Platform}, error={Error}",
            job.Id, job.Platform, error);
        return Task.CompletedTask;
    }
}
