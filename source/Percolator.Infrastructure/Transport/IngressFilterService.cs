namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Concurrent;
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
}

public sealed class IngressFilterService : IIngressFilterService
{
    private readonly IPeerContactRepository _contactRepository;
    private readonly IngressFilterOptions _options;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ConcurrentDictionary<PublicIdentityId, RateLimitTracker> _rateLimiters = new();

    private sealed class RateLimitTracker
    {
        public int WindowMinute;
        public int RequestCount;
    }

    public IngressFilterService(
        IPeerContactRepository contactRepository,
        IngressFilterOptions? options = null,
        IDateTimeProvider? timeProvider = null)
    {
        _contactRepository = contactRepository ?? throw new ArgumentNullException(nameof(contactRepository));
        _options = options ?? new IngressFilterOptions();
        _timeProvider = timeProvider ?? new DefaultDateTimeProvider();
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

    private sealed class DefaultDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
