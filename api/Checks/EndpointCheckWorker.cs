using System.Diagnostics;
using CloudApiReliabilityDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Endpoint = CloudApiReliabilityDashboard.Api.Data.Endpoint;

namespace CloudApiReliabilityDashboard.Api.Checks;

internal sealed class EndpointCheckWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CheckerOptions _options;
    private readonly ILogger<EndpointCheckWorker> _logger;

    public EndpointCheckWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IOptions<CheckerOptions> options,
        ILogger<EndpointCheckWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    List<Endpoint> endpoints;
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        endpoints = await db.Endpoints.AsNoTracking().ToListAsync(stoppingToken);
                    }

                    await Task.WhenAll(endpoints.Select(e => CheckOneAsync(e, stoppingToken)));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Check cycle failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }

    internal async Task CheckOneAsync(Endpoint endpoint, CancellationToken stoppingToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("checker");
            var success = false;
            var latencyMs = 0;
            int? statusCode = null;

            for (var attempt = 1; attempt <= 1 + _options.MaxRetries; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

                var stopwatch = Stopwatch.StartNew();
                var retry = false;
                try
                {
                    using var response = await client.GetAsync(
                        endpoint.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    statusCode = (int)response.StatusCode;
                    success = statusCode is >= 200 and < 400;
                    retry = statusCode >= 500;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    statusCode = null;
                    success = false;
                    retry = true;
                }
                stopwatch.Stop();
                latencyMs = (int)stopwatch.ElapsedMilliseconds;

                if (!retry || attempt == 1 + _options.MaxRetries)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), stoppingToken);
            }

            // Own scope per endpoint: DbContext is not thread-safe.
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<CheckResultService>();
            await service.SaveCheckResultAsync(endpoint.Id, success, latencyMs, statusCode, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check failed for endpoint {EndpointId}.", endpoint.Id);
        }
    }
}
