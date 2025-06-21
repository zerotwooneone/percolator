using System.Collections.Concurrent;

namespace Percolator.Application;

public class InMemoryRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, DateTime> _requestTimestamps = new();
    private readonly TimeSpan _requestCooldown = TimeSpan.FromSeconds(5);

    public RateLimitDecision IsRequestAllowed(string peerIdentifier)
    {
        var now = DateTime.UtcNow;
        if (_requestTimestamps.TryGetValue(peerIdentifier, out var lastRequestTime))
        {
            var nextAllowedTime = lastRequestTime.Add(_requestCooldown);
            if (now < nextAllowedTime)
            {
                return new RateLimitDecision { IsAllowed = false, RetryAfterUtc = nextAllowedTime };
            }
        }

        _requestTimestamps[peerIdentifier] = now;
        return new RateLimitDecision { IsAllowed = true };
    }
}
