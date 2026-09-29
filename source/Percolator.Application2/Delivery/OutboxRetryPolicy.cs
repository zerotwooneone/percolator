namespace Percolator.Application2.Delivery;

public static class OutboxRetryPolicy
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(5);

    public static DateTimeOffset CalculateNextAttempt(int retryCount, DateTimeOffset now)
    {
        // Exponential backoff: BaseInterval * 2^retryCount + deterministic jitter
        double backoffMultiplier = Math.Pow(2, Math.Min(retryCount, 10));
        var backoffMs = BaseInterval.TotalMilliseconds * backoffMultiplier;
        
        // Add pseudo-jitter (0% to 25% of current backoff)
        var jitterMs = (backoffMs * 0.25) * ((retryCount % 3) / 2.0);
        var totalMs = Math.Min(backoffMs + jitterMs, MaxInterval.TotalMilliseconds);

        return now.AddMilliseconds(totalMs);
    }
}
