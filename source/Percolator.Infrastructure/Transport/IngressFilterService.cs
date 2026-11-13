namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;

public sealed class IngressFilterOptions
{
    public int MaxRequestsPerMinute { get; set; } = 120;
    public bool EnableRateLimiting { get; set; } = true;
    public int MaxTrackedSenders { get; set; } = 10_000;
}

public sealed class IngressFilterService : IIngressFilterService
{
    private readonly IPeerContactRepository _contactRepository;
    private readonly IngressFilterOptions _options;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ConcurrentDictionary<PublicIdentityId, RateLimitTracker> _rateLimiters = new();
    private int _lastCleanupMinute;

    private sealed class RateLimitTracker
    {
        public int WindowMinute;
        public int RequestCount;
    }

    public IngressFilterService(
        IPeerContactRepository contactRepository,
        IngressFilterOptions options,
        IDateTimeProvider timeProvider)
    {
        _contactRepository = contactRepository ?? throw new ArgumentNullException(nameof(contactRepository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lastCleanupMinute = (int)(_timeProvider.UtcNow.ToUnixTimeSeconds() / 60);
    }

    public async ValueTask<DomainResult> CheckIngressAllowedAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        // 1. Check contact trust level
        var contact = await _contactRepository.GetByPeerIdAsync(recipientId, senderId, ct);
        if (contact != null && contact.TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult.Failure(new DomainError("BLOCKED_SENDER", "Messages from this sender are blocked."));
        }

        // 2. Enforce rate limiting
        if (_options.EnableRateLimiting && _options.MaxRequestsPerMinute > 0)
        {
            var now = _timeProvider.UtcNow;
            int currentMinute = (int)(now.ToUnixTimeSeconds() / 60);

            // Periodic bounded cleanup of stale window entries to prevent memory leak
            if (currentMinute > _lastCleanupMinute || _rateLimiters.Count > _options.MaxTrackedSenders)
            {
                _lastCleanupMinute = currentMinute;
                CleanupStaleTrackers(currentMinute);
            }

            var tracker = _rateLimiters.GetOrAdd(senderId, _ => new RateLimitTracker { WindowMinute = currentMinute, RequestCount = 0 });

            lock (tracker)
            {
                if (tracker.WindowMinute != currentMinute)
                {
                    tracker.WindowMinute = currentMinute;
                    tracker.RequestCount = 0;
                }

                tracker.RequestCount++;
                if (tracker.RequestCount > _options.MaxRequestsPerMinute)
                {
                    return DomainResult.Failure(new DomainError("RATE_LIMIT_EXCEEDED", "Sender has exceeded the ingress rate limit."));
                }
            }
        }

        return DomainResult.Success();
    }

    private void CleanupStaleTrackers(int currentMinute)
    {
        foreach (var kvp in _rateLimiters)
        {
            if (kvp.Value.WindowMinute < currentMinute)
            {
                _rateLimiters.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class DefaultDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
