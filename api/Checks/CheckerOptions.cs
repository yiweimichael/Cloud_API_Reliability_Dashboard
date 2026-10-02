namespace CloudApiReliabilityDashboard.Api.Checks;

public sealed record CheckerOptions
{
    public int IntervalSeconds { get; init; } = 60;
    public int TimeoutSeconds { get; init; } = 5;
    public int MaxRetries { get; init; } = 2;
    public bool Enabled { get; init; } = true;
}
