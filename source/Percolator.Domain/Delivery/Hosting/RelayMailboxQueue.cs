using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

public sealed class RelayMailboxQueue : AggregateRoot<QueueId>
{
    public override QueueId Id { get; }
    public PublicIdentityId RelayIdentityId { get; }

    private readonly Dictionary<BlindedRoutingToken, DeliveryToken> _authorizedTokens = [];
    private readonly List<MailboxEnvelope> _envelopes = [];

    public IReadOnlyCollection<MailboxEnvelope> Envelopes => _envelopes.AsReadOnly();
    public int TotalCount => _envelopes.Count;

    public RelayMailboxQueue(PublicIdentityId relayIdentityId, QueueId? id = null)
    {
        Id = id ?? QueueId.New();
        RelayIdentityId = relayIdentityId;
    }

    public void RegisterRecipient(BlindedRoutingToken recipientToken, DeliveryToken deliveryToken)
    {
        _authorizedTokens[recipientToken] = deliveryToken;
    }

    public DomainResult Enqueue(
        MailboxEnvelope envelope,
        DeliveryToken presentedToken,
        IDateTimeProvider timeProvider,
        PurgePolicy? policy = null)
    {
        var effectivePolicy = policy ?? PurgePolicy.Default;

        if (envelope.ExpiresAtUtc <= timeProvider.UtcNow)
        {
            return DomainResult.Failure(new DomainError(
                "ENVELOPE_EXPIRED",
                "Cannot enqueue an envelope that has already expired."));
        }

        if (!_authorizedTokens.TryGetValue(envelope.RecipientToken, out var authorizedToken) || authorizedToken != presentedToken)
        {
            return DomainResult.Failure(new DomainError(
                "UNAUTHORIZED_DELIVERY_TOKEN",
                "The presented delivery token is invalid or not authorized for this recipient mailbox."));
        }

        if (_envelopes.Count >= effectivePolicy.MaxRetainedEnvelopes)
        {
            // First attempt to purge expired
            PurgeExpired(timeProvider);

            if (_envelopes.Count >= effectivePolicy.MaxRetainedEnvelopes)
            {
                return DomainResult.Failure(new DomainError(
                    "MAILBOX_QUOTA_EXCEEDED",
                    $"Relay mailbox queue reached maximum capacity of {effectivePolicy.MaxRetainedEnvelopes} envelopes."));
            }
        }

        _envelopes.Add(envelope);
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
        return matching.AsReadOnly();
    }
}
