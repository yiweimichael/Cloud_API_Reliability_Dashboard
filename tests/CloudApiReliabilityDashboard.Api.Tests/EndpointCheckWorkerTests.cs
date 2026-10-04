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

    // Returns the given statuses in order, repeating the last one once they run out.
    private sealed class StubHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public readonly List<DateTime> CallTimes = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int call;
            lock (CallTimes)
            {
                call = CallTimes.Count;
                CallTimes.Add(DateTime.UtcNow);
            }
            return Task.FromResult(new HttpResponseMessage(statuses[Math.Min(call, statuses.Length - 1)]));
        }
    }

    // Never responds; the request ends only when its cancellation token fires.
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private async Task<Data.Endpoint> AddEndpointAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var endpoint = new Data.Endpoint { Url = "https://example.com/" };
        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync();
        return endpoint;
    }

    private EndpointCheckWorker CreateWorker(HttpMessageHandler handler, CheckerOptions options) =>
        new(
            _services.GetRequiredService<IServiceScopeFactory>(),
            new StubFactory(handler),
            Options.Create(options),
            NullLogger<EndpointCheckWorker>.Instance);

    private async Task<CheckResult> GetSingleResultAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().CheckResults.SingleAsync();
    }

    [Fact]
    public async Task CheckOneAsync_RetriesAfter500_AndSavesSuccessWhenNextAttemptReturns200()
    {
        var endpoint = await AddEndpointAsync();
        var handler = new StubHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var worker = CreateWorker(handler, new CheckerOptions { TimeoutSeconds = 5, MaxRetries = 2 });

        // A 1s retry delay separates the two attempts.
        await worker.CheckOneAsync(endpoint, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        var saved = await GetSingleResultAsync();
        Assert.Equal(endpoint.Id, saved.EndpointId);
        Assert.True(saved.Success);
        Assert.Equal(200, saved.HttpStatusCode);
        // Stops after the 200 even though a third attempt was allowed.
        Assert.Equal(2, handler.CallTimes.Count);
    }

    [Fact]
    public async Task CheckOneAsync_WhenResponseNeverArrives_TimesOutAndSavesFailureWithNullStatus()
    {
        var endpoint = await AddEndpointAsync();
        var worker = CreateWorker(new HangingHandler(), new CheckerOptions { TimeoutSeconds = 1, MaxRetries = 0 });

        // The bound turns a missing timeout into a test failure instead of a hung test run.
        await worker.CheckOneAsync(endpoint, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        var saved = await GetSingleResultAsync();
        Assert.Equal(endpoint.Id, saved.EndpointId);
        Assert.False(saved.Success);
        Assert.Null(saved.HttpStatusCode);
        Assert.InRange(saved.LatencyMs, 900, 10_000);
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
