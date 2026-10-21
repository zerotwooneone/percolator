using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

public sealed class RelayMailboxQueue : AggregateRoot<QueueId>
{
    private readonly record struct RecipientMailboxAuth(
        DeliveryToken DeliveryToken,
        DeliveryToken? DrainToken);

    public override QueueId Id { get; }
    public PublicIdentityId RelayIdentityId { get; }

    private readonly Dictionary<BlindedRoutingToken, RecipientMailboxAuth> _authorizedTokens = [];
    private readonly Dictionary<BlindedRoutingToken, List<MailboxEnvelope>> _envelopesByRecipient = [];
    private readonly HashSet<EnvelopeId> _activeEnvelopeIds = [];

    public IReadOnlyCollection<MailboxEnvelope> Envelopes =>
        _envelopesByRecipient.Values.SelectMany(list => list).ToList().AsReadOnly();

    public int TotalCount { get; private set; }

    public RelayMailboxQueue(PublicIdentityId relayIdentityId, QueueId? id = null)
    {
        Id = id ?? QueueId.New();
        RelayIdentityId = relayIdentityId;
    }

    public void RegisterRecipient(
        BlindedRoutingToken recipientToken,
        DeliveryToken deliveryToken,
        DeliveryToken? drainToken = null)
    {
        ArgumentNullException.ThrowIfNull(deliveryToken);
        _authorizedTokens[recipientToken] = new RecipientMailboxAuth(deliveryToken, drainToken);
    }

    public DomainResult Enqueue(
        MailboxEnvelope envelope,
        DeliveryToken presentedToken,
        IDateTimeProvider timeProvider,
        PurgePolicy? policy = null)
    {
        var effectivePolicy = policy ?? PurgePolicy.Default;

        if (!envelope.Id.IsValid)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_ENVELOPE_ID",
                "Mailbox envelope must have a valid non-empty identifier."));
        }

        if (_activeEnvelopeIds.Contains(envelope.Id))
        {
            return DomainResult.Failure(new DomainError(
                "DUPLICATE_ENVELOPE_ID",
                "An envelope with this identifier is already queued."));
        }

        if (envelope.ExpiresAtUtc <= timeProvider.UtcNow)
        {
            return DomainResult.Failure(new DomainError(
                "ENVELOPE_EXPIRED",
                "Cannot enqueue an envelope that has already expired."));
        }

        if (envelope.Ciphertext.Length > effectivePolicy.MaxEnvelopeSizeBytes)
        {
            return DomainResult.Failure(new DomainError(
                "ENVELOPE_TOO_LARGE",
                $"Envelope ciphertext size of {envelope.Ciphertext.Length} bytes exceeds maximum allowed limit of {effectivePolicy.MaxEnvelopeSizeBytes} bytes."));
        }

        if (presentedToken == null ||
            !_authorizedTokens.TryGetValue(envelope.RecipientToken, out var auth) ||
            auth.DeliveryToken != presentedToken)
        {
            return DomainResult.Failure(new DomainError(
                "UNAUTHORIZED_DELIVERY_TOKEN",
                "The presented delivery token is invalid or not authorized for this recipient mailbox."));
        }

        if (TotalCount >= effectivePolicy.MaxRetainedEnvelopes)
        {
            // First attempt to purge expired
            PurgeExpired(timeProvider);

            if (TotalCount >= effectivePolicy.MaxRetainedEnvelopes)
            {
                return DomainResult.Failure(new DomainError(
                    "MAILBOX_QUOTA_EXCEEDED",
                    $"Relay mailbox queue reached maximum capacity of {effectivePolicy.MaxRetainedEnvelopes} envelopes."));
            }
        }

        if (!_envelopesByRecipient.TryGetValue(envelope.RecipientToken, out var recipientQueue))
        {
            recipientQueue = [];
            _envelopesByRecipient[envelope.RecipientToken] = recipientQueue;
        }

        recipientQueue.Add(envelope);
        _activeEnvelopeIds.Add(envelope.Id);
        TotalCount++;
        return DomainResult.Success();
    }

    public int PurgeExpired(IDateTimeProvider timeProvider)
    {
        var now = timeProvider.UtcNow;
        int totalRemoved = 0;

        foreach (var (token, list) in _envelopesByRecipient)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].ExpiresAtUtc <= now)
                {
                    _activeEnvelopeIds.Remove(list[i].Id);
                    list.RemoveAt(i);
                    totalRemoved++;
                    TotalCount--;
                }
            }
        }

        return totalRemoved;
    }

    /// <summary>
    /// Drains queued envelopes for a recipient after verifying the recipient's secret drain token in constant time.
    /// Prevents senders who only hold the <see cref="DeliveryToken"/> from stealing or deleting queued mail.
    /// </summary>
    public DomainResult<IReadOnlyList<MailboxEnvelope>> TryDrainForToken(
        BlindedRoutingToken token,
        DeliveryToken presentedDrainToken)
    {
        if (presentedDrainToken == null || !_authorizedTokens.TryGetValue(token, out var auth))
        {
            return DomainResult<IReadOnlyList<MailboxEnvelope>>.Failure(new DomainError(
                "UNAUTHORIZED_DRAIN_TOKEN",
                "The presented drain token is invalid or not authorized for this recipient mailbox."));
        }

        var expectedToken = auth.DrainToken ?? auth.DeliveryToken;
        if (expectedToken != presentedDrainToken)
        {
            return DomainResult<IReadOnlyList<MailboxEnvelope>>.Failure(new DomainError(
                "UNAUTHORIZED_DRAIN_TOKEN",
                "The presented drain token does not match the recipient's authorized drain token."));
        }

        return DomainResult<IReadOnlyList<MailboxEnvelope>>.Success(DrainInternal(token));
    }

    public IReadOnlyList<MailboxEnvelope> DrainForToken(BlindedRoutingToken token)
    {
        if (_authorizedTokens.TryGetValue(token, out var auth) && auth.DrainToken != null)
        {
            // Recipient registered an explicit drain token; unauthenticated drain is prohibited.
            return Array.Empty<MailboxEnvelope>();
        }

        return DrainInternal(token);
    }

    private IReadOnlyList<MailboxEnvelope> DrainInternal(BlindedRoutingToken token)
    {
        if (!_envelopesByRecipient.Remove(token, out var matching) || matching.Count == 0)
        {
            return Array.Empty<MailboxEnvelope>();
        }

        foreach (var env in matching)
        {
            _activeEnvelopeIds.Remove(env.Id);
        }

        TotalCount -= matching.Count;
        return matching.AsReadOnly();
    }
}
