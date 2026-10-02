using Microsoft.EntityFrameworkCore;

namespace CloudApiReliabilityDashboard.Api.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CheckResult>(entity =>
        {
            entity.HasOne<Endpoint>()
                .WithMany()
                .HasForeignKey(c => c.EndpointId)
                .OnDelete(DeleteBehavior.Cascade);

            // Check times are always UTC: stamp values read back as DateTimeKind.Utc.
            entity.Property(c => c.CheckTimeUtc)
                .HasConversion(
                    v => v,
                    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            // Serves per-endpoint history queries ordered by time.
            entity.HasIndex(c => new { c.EndpointId, c.CheckTimeUtc });
        });
    }
}
