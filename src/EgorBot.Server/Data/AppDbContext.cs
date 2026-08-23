using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using EgorBot.Server.Models;

namespace EgorBot.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>
    /// SQLite has no date type, so EF reads every DateTime back with Kind=Unspecified.
    /// Those serialize to JSON without a 'Z', and browsers then parse them as *local*
    /// time — every timestamp and duration in the web UI was off by the viewer's offset.
    /// Force UTC on the way in and out.
    /// </summary>
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    public DbSet<BenchmarkJob> Jobs => Set<BenchmarkJob>();
    public DbSet<JobLogEntry> JobLogs => Set<JobLogEntry>();
    public DbSet<JobAdmission> JobAdmissions => Set<JobAdmission>();
    public DbSet<UserJobLimit> UserJobLimits => Set<UserJobLimit>();
    public DbSet<JobRequestLimit> JobRequestLimits => Set<JobRequestLimit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchmarkJob>(e =>
        {
            e.HasIndex(j => j.GroupId);
            e.HasIndex(j => j.Status);
            e.HasIndex(j => j.CreatedAt);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(j => j.Kind).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<JobLogEntry>(e => e.HasIndex(l => new { l.JobId, l.Id }));

        modelBuilder.Entity<JobAdmission>(e =>
        {
            e.Property(a => a.UserKey).UseCollation("NOCASE");
            e.HasIndex(a => new { a.UserKey, a.AdmittedAt });
            e.HasOne(a => a.Job)
                .WithOne()
                .HasForeignKey<JobAdmission>(a => a.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserJobLimit>(e =>
        {
            e.Property(l => l.UserKey).UseCollation("NOCASE");
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(UtcConverter);
            }
        }
    }
}
