using System.Data;
using EgorBot.Server.Data;
using EgorBot.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace EgorBot.Server.Services;

public sealed record JobAdmissionResult(
    bool Accepted,
    string UserKey,
    int Limit,
    int Used,
    int Requested,
    DateTime? RetryAtUtc);

/// <summary>
/// Atomically reserves rolling-window job capacity and persists accepted jobs.
/// </summary>
public sealed class JobRateLimitService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<JobRateLimitService> logger)
{
    public const string AnonymousUserKey = "(anonymous)";
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _globalLimit = ReadGlobalLimit(configuration);

    public int GlobalLimit => _globalLimit;

    public async Task<JobAdmissionResult> TryAdmitAsync(
        string? requestedBy,
        IReadOnlyCollection<BenchmarkJob> jobs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
            throw new ArgumentException("At least one job is required.", nameof(jobs));

        var userKey = NormalizeUserKey(requestedBy);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now - Window;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var overrideLimit = await db.UserJobLimits
                .Where(l => l.UserKey == userKey)
                .Select(l => (int?)l.MaxJobs)
                .SingleOrDefaultAsync(cancellationToken);
            var limit = overrideLimit ?? _globalLimit;

            var admittedAt = await db.JobAdmissions
                .Where(a => a.UserKey == userKey && a.AdmittedAt > cutoff)
                .OrderBy(a => a.AdmittedAt)
                .Select(a => a.AdmittedAt)
                .ToListAsync(cancellationToken);

            if (admittedAt.Count + jobs.Count > limit)
            {
                var expirationsNeeded = admittedAt.Count + jobs.Count - limit;
                DateTime? retryAt = expirationsNeeded > 0 && expirationsNeeded <= admittedAt.Count
                    ? admittedAt[expirationsNeeded - 1] + Window
                    : null;

                await transaction.RollbackAsync(cancellationToken);
                logger.LogWarning(
                    "Rejected {Requested} job(s) for {User}: {Used}/{Limit} admissions in the rolling window",
                    jobs.Count, userKey, admittedAt.Count, limit);
                return new JobAdmissionResult(
                    Accepted: false,
                    UserKey: userKey,
                    Limit: limit,
                    Used: admittedAt.Count,
                    Requested: jobs.Count,
                    RetryAtUtc: retryAt);
            }

            foreach (var job in jobs)
                job.RequestedBy = userKey;

            db.Jobs.AddRange(jobs);
            db.JobAdmissions.AddRange(jobs.Select(job => new JobAdmission
            {
                JobId = job.Id,
                Job = job,
                UserKey = userKey,
                AdmittedAt = now,
            }));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Admitted {Requested} job(s) for {User}: {Used}/{Limit} previously used in the rolling window",
                jobs.Count, userKey, admittedAt.Count, limit);
            return new JobAdmissionResult(
                Accepted: true,
                UserKey: userKey,
                Limit: limit,
                Used: admittedAt.Count,
                Requested: jobs.Count,
                RetryAtUtc: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetUserLimitAsync(
        string userName,
        int maxJobs,
        CancellationToken cancellationToken = default)
    {
        if (maxJobs < 0)
            throw new ArgumentOutOfRangeException(nameof(maxJobs), "The job limit cannot be negative.");

        var userKey = NormalizeNamedUserKey(userName);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.UserJobLimits.FindAsync([userKey], cancellationToken);
            if (existing is null)
            {
                db.UserJobLimits.Add(new UserJobLimit
                {
                    UserKey = userKey,
                    MaxJobs = maxJobs,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.MaxJobs = maxJobs;
                existing.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        logger.LogInformation("Set rolling job limit for {User} to {Limit}", userKey, maxJobs);
    }

    public async Task<bool> RemoveUserLimitAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        var userKey = NormalizeNamedUserKey(userName);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.UserJobLimits.FindAsync([userKey], cancellationToken);
            if (existing is null)
                return false;

            db.UserJobLimits.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Removed rolling job-limit override for {User}", userKey);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string NormalizeUserKey(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return AnonymousUserKey;

        var normalized = userName.Trim().TrimStart('@').Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return AnonymousUserKey;
        if (normalized.Length > 128)
            throw new ArgumentException("The user name cannot exceed 128 characters.", nameof(userName));

        return normalized;
    }

    private static string NormalizeNamedUserKey(string userName)
    {
        var normalized = NormalizeUserKey(userName);
        if (normalized == AnonymousUserKey)
            throw new ArgumentException("A GitHub user name is required.", nameof(userName));
        return normalized;
    }

    private static int ReadGlobalLimit(IConfiguration configuration)
    {
        var limit = configuration.GetValue("EgorBot:MaxJobsPerUser24Hours", 16);
        if (limit < 0)
            throw new InvalidOperationException("EgorBot:MaxJobsPerUser24Hours cannot be negative.");
        return limit;
    }
}
