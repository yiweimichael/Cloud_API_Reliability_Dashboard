namespace CloudApiReliabilityDashboard.Api.Data;

public class CheckResult
{
    public int Id { get; set; }
    public int EndpointId { get; set; }
    public DateTime CheckTimeUtc { get; set; }
    public bool Success { get; set; }
    public int LatencyMs { get; set; }
    public int? HttpStatusCode { get; set; }
}
