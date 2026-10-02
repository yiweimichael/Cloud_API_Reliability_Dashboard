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
        modelBuilder.Entity<CheckResult>()
            .HasOne<Endpoint>()
            .WithMany()
            .HasForeignKey(c => c.EndpointId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
