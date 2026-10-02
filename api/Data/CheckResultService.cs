namespace CloudApiReliabilityDashboard.Api.Data;

internal sealed class CheckResultService
{
    private readonly AppDbContext _db;

    public CheckResultService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CheckResult> SaveCheckResultAsync(
        int endpointId,
        bool success,
        int latencyMs,
        int? httpStatusCode,
        CancellationToken cancellationToken = default)
    {
        var result = new CheckResult
        {
            EndpointId = endpointId,
            CheckTimeUtc = DateTime.UtcNow,
            Success = success,
            LatencyMs = latencyMs,
            HttpStatusCode = httpStatusCode
        };

        _db.CheckResults.Add(result);
        await _db.SaveChangesAsync(cancellationToken);

        return result;
    }
}
