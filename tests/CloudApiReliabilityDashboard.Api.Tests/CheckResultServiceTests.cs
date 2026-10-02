using CloudApiReliabilityDashboard.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudApiReliabilityDashboard.Api.Tests;

public sealed class CheckResultServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public CheckResultServiceTests()
    {
        // The in-memory database lives only as long as this connection stays open.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> AddEndpointAsync()
    {
        using var db = new AppDbContext(_options);
        var endpoint = new Data.Endpoint { Url = "https://example.com/" };
        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync();
        return endpoint.Id;
    }

    [Fact]
    public async Task SaveCheckResultAsync_SavesRowWithExpectedFields()
    {
        var endpointId = await AddEndpointAsync();

        CheckResult returned;
        using (var db = new AppDbContext(_options))
        {
            returned = await new CheckResultService(db)
                .SaveCheckResultAsync(endpointId, true, 123, 200);
        }

        using var verifyDb = new AppDbContext(_options);
        var saved = await verifyDb.CheckResults.SingleAsync();

        Assert.Equal(returned.Id, saved.Id);
        Assert.Equal(endpointId, saved.EndpointId);
        Assert.True(saved.Success);
        Assert.Equal(123, saved.LatencyMs);
        Assert.Equal(200, saved.HttpStatusCode);
    }

    [Fact]
    public async Task SaveCheckResultAsync_SetsCheckTimeToNowInUtc()
    {
        var endpointId = await AddEndpointAsync();
        var before = DateTime.UtcNow;

        using (var db = new AppDbContext(_options))
        {
            await new CheckResultService(db).SaveCheckResultAsync(endpointId, true, 50, 200);
        }

        var after = DateTime.UtcNow;

        using var verifyDb = new AppDbContext(_options);
        var saved = await verifyDb.CheckResults.SingleAsync();

        Assert.Equal(DateTimeKind.Utc, saved.CheckTimeUtc.Kind);
        Assert.InRange(saved.CheckTimeUtc, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task SaveCheckResultAsync_AllowsNullHttpStatusCode()
    {
        var endpointId = await AddEndpointAsync();

        using (var db = new AppDbContext(_options))
        {
            await new CheckResultService(db).SaveCheckResultAsync(endpointId, false, 5000, null);
        }

        using var verifyDb = new AppDbContext(_options);
        var saved = await verifyDb.CheckResults.SingleAsync();

        Assert.False(saved.Success);
        Assert.Null(saved.HttpStatusCode);
    }

    [Fact]
    public async Task SaveCheckResultAsync_ThrowsWhenEndpointDoesNotExist()
    {
        using var db = new AppDbContext(_options);
        // SQLite enforces foreign keys by default in EF Core, so a missing endpoint fails on save.
        var service = new CheckResultService(db);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => service.SaveCheckResultAsync(999, true, 10, 200));
    }
}
