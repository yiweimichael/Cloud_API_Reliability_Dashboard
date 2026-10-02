using Microsoft.EntityFrameworkCore;

namespace CloudApiReliabilityDashboard.Api.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }
}
