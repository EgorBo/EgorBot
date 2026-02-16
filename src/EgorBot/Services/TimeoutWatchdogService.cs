using EgorBot.Data;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Services;

/// <summary>
/// Background service that watches for timed-out sub-jobs and marks them accordingly.
/// </summary>
public class TimeoutWatchdogService(
    IServiceScopeFactory scopeFactory,
    ILogger<TimeoutWatchdogService> logger,
    IConfiguration config)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromMinutes(1);
        var timeout = TimeSpan.FromMinutes(config.GetValue("Bot:SubJobTimeoutMinutes", 120));

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(checkInterval, stoppingToken);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<JobOrchestrator>();

                var cutoff = DateTime.UtcNow - timeout;
                var timedOut = await db.SubJobs
                    .Where(s => (s.Status == SubJobStatus.Provisioning || s.Status == SubJobStatus.Running)
                                && s.CreatedAt < cutoff)
                    .ToListAsync(stoppingToken);

                foreach (var subJob in timedOut)
                {
                    logger.LogWarning("Sub-job {SubJobId} timed out after {Timeout} minutes", subJob.Id, timeout.TotalMinutes);
                    subJob.Status = SubJobStatus.TimedOut;
                    subJob.CompletedAt = DateTime.UtcNow;
                    subJob.ErrorMessage = $"Timed out after {timeout.TotalMinutes} minutes";
                }

                if (timedOut.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);

                    // Check if any parent jobs are now fully complete
                    var jobIds = timedOut.Select(s => s.JobId).Distinct();
                    foreach (var jobId in jobIds)
                    {
                        await orchestrator.CompleteSubJobAsync(
                            timedOut.First(s => s.JobId == jobId).Id,
                            false, null, "Timed out");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in timeout watchdog");
            }
        }
    }
}
