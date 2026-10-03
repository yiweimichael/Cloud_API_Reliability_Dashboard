using System.Net;
using CloudApiReliabilityDashboard.Api.Checks;
using CloudApiReliabilityDashboard.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudApiReliabilityDashboard.Api.Tests;

public sealed class EndpointCheckWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public EndpointCheckWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection))
            .AddScoped<CheckResultService>()
            .BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public readonly List<DateTime> CallTimes = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (CallTimes) CallTimes.Add(DateTime.UtcNow);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Endpoint_Returning500_SavesSingleFailedResultAfterRetries()
    {
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Endpoints.Add(new Data.Endpoint { Url = "https://example.com/" });
            await db.SaveChangesAsync();
        }

        var handler = new StubHandler(HttpStatusCode.InternalServerError);
        var options = Options.Create(new CheckerOptions { IntervalSeconds = 1, TimeoutSeconds = 5, MaxRetries = 2 });
        var worker = new EndpointCheckWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            new StubFactory(handler),
            options,
            NullLogger<EndpointCheckWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        // First tick at 1s, then attempts separated by 1s and 2s delays.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var count = 0;
        while (count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            using var scope = _services.CreateScope();
            count = await scope.ServiceProvider.GetRequiredService<AppDbContext>().CheckResults.CountAsync();
        }

        await worker.StopAsync(CancellationToken.None);

        using var verifyScope = _services.CreateScope();
        var saved = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .CheckResults.SingleAsync();

        Assert.False(saved.Success);
        Assert.Equal(500, saved.HttpStatusCode);

        // The next tick can start a second cycle right after the save, so count
        // only the requests made before the result was saved.
        lock (handler.CallTimes)
        {
            Assert.Equal(3, handler.CallTimes.Count(t => t <= saved.CheckTimeUtc));
        }
    }
}
