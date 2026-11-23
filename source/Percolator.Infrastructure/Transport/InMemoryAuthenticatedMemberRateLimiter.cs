namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;

public sealed class AuthenticatedMemberRateLimiterOptions
{
    public int MaxRequestsPerMinute { get; set; } = 120;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class InMemoryAuthenticatedMemberRateLimiter : IAuthenticatedMemberRateLimiter
{
    private readonly AuthenticatedMemberRateLimiterOptions _options;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _requestTimestamps = new();

    public InMemoryAuthenticatedMemberRateLimiter(
        AuthenticatedMemberRateLimiterOptions? options = null,
        IDateTimeProvider? timeProvider = null)
    {
        _options = options ?? new AuthenticatedMemberRateLimiterOptions();
        _timeProvider = timeProvider ?? new Percolator.Infrastructure.Common.SystemDateTimeProvider();
    }

    public Task<bool> TryConsumeAsync(BlindedRoutingToken memberRoutingToken, string operation, CancellationToken ct = default)
    {
        var key = $"{memberRoutingToken.Value:N}:{operation}";
        var timestamps = _requestTimestamps.GetOrAdd(key, _ => new List<DateTimeOffset>());
        var now = _timeProvider.UtcNow;
        var cutoff = now - _options.Window;

        lock (timestamps)
        {
            timestamps.RemoveAll(t => t < cutoff);
            if (timestamps.Count >= _options.MaxRequestsPerMinute)
            {
                return Task.FromResult(false);
            }

            timestamps.Add(now);
            return Task.FromResult(true);
        }
    }
}
