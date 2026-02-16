using Microsoft.EntityFrameworkCore;

namespace EgorBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<SubJob> SubJobs => Set<SubJob>();

    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasMany(j => j.SubJobs).WithOne(s => s.Job).HasForeignKey(s => s.JobId);
            e.Property(j => j.Status).HasConversion<string>();
        });

        modelBuilder.Entity<SubJob>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Status).HasConversion<string>();
            e.Property(s => s.TargetOs).HasConversion<string>();
            e.Property(s => s.TargetArch).HasConversion<string>();
        });
    }
}
