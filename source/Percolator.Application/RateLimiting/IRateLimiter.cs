namespace Percolator.Application.RateLimiting;

public interface IRateLimiter
{
    RateLimitDecision IsRequestAllowed(string peerIdentifier);
}

public class RateLimitDecision
{
    public bool IsAllowed { get; set; }
    public DateTime? RetryAfterUtc { get; set; }
}
