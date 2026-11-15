namespace Percolator.Application.Delivery;

public sealed record DrainSchedulerOptions
{
    public int MaxConcurrentSessions { get; init; } = 64;
    public TimeSpan MaxSessionAge { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public double BackoffMultiplier { get; init; } = 2.0;
}
