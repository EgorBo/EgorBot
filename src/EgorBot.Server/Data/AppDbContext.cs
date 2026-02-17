using Microsoft.EntityFrameworkCore;
using EgorBot.Server.Models;

namespace EgorBot.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<BenchmarkJob> Jobs => Set<BenchmarkJob>();
    public DbSet<JobLogEntry> JobLogs => Set<JobLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchmarkJob>(e =>
        {
            e.HasIndex(j => j.GroupId);
            e.HasIndex(j => j.Status);
            e.HasIndex(j => j.CreatedAt);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<JobLogEntry>(e =>
        {
            e.HasIndex(l => new { l.JobId, l.Id });
        });
    }
}
