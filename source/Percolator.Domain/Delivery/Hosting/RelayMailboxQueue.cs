using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Events;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

public sealed class RelayMailboxQueue : AggregateRoot<Guid>
{
    public override Guid Id { get; }
    public PublicIdentityId RelayIdentityId { get; }

    private readonly List<MailboxEnvelope> _envelopes = [];
    public IReadOnlyList<MailboxEnvelope> Envelopes => _envelopes.AsReadOnly();
    public int TotalCount => _envelopes.Count;

    public RelayMailboxQueue(PublicIdentityId relayIdentityId, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        RelayIdentityId = relayIdentityId;
    }

    public DomainResult Enqueue(MailboxEnvelope envelope, IDateTimeProvider timeProvider)
    {
        _envelopes.Add(envelope);
        AddDomainEvent(new EnvelopeBufferedEvent(envelope.Id, envelope.RecipientToken, timeProvider.UtcNow));
        return DomainResult.Success();
    }

    public int PurgeExpired(IDateTimeProvider timeProvider)
    {
        var now = timeProvider.UtcNow;
        int removed = _envelopes.RemoveAll(e => e.ExpiresAtUtc <= now);
        return removed;
    }

    public IReadOnlyList<MailboxEnvelope> DrainForToken(BlindedRoutingToken token)
    {
        var matching = _envelopes.Where(e => e.RecipientToken == token).ToList();
        _envelopes.RemoveAll(e => e.RecipientToken == token);
        return matching;
    }

    public int PruneDiscretionary(int maxRetainedCount)
    {
        if (_envelopes.Count <= maxRetainedCount)
        {
            return 0;
        }

        int toRemoveCount = _envelopes.Count - maxRetainedCount;
        // Sort by EnqueuedAtUtc ascending (oldest first)
        _envelopes.Sort((a, b) => a.EnqueuedAtUtc.CompareTo(b.EnqueuedAtUtc));
        _envelopes.RemoveRange(0, toRemoveCount);
        return toRemoveCount;
    }
}
