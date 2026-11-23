namespace Percolator.Domain.Delivery.Ports;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// Token-only mailbox capability store. The relay deliberately never persists an owner
/// identity — mailboxes, queued envelopes, and delivery authorization are addressed
/// purely by blinded routing token + bearer tokens.
/// </summary>
public interface IRelayMailboxCapabilityStore
{
    ValueTask RegisterAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        CancellationToken ct = default);

    ValueTask<DomainResult> RegisterVerifiedAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        DeliveryToken? existingDrainToken = null,
        CancellationToken ct = default);

    ValueTask<bool> VerifyDeliveryTokenAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        CancellationToken ct = default);

    ValueTask<bool> VerifyDrainTokenAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken drainToken,
        CancellationToken ct = default);

    ValueTask<DeliveryToken?> GetDrainTokenAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default);

    ValueTask EnqueueEnvelopeAsync(
        BlindedRoutingToken routingToken,
        RelayQueuedEnvelopeRecord envelope,
        CancellationToken ct = default);

    ValueTask<DomainResult> EnqueueEnvelopeAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        MailboxEnvelope envelope,
        long maxMailboxBytes,
        CancellationToken ct = default);

    ValueTask<DomainResult> EnqueueAuthenticatedInitialAsync(
        BlindedRoutingToken routingToken,
        MailboxEnvelope envelope,
        long maxMailboxBytes,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<RelayQueuedEnvelopeRecord>> GetEnvelopesAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default);

    /// <summary>
    /// Destructively removes every queued envelope for the mailbox without
    /// materializing payloads — used when a mailbox binding is deleted.
    /// Returns the number of envelopes removed.
    /// </summary>
    ValueTask<int> PurgeMailboxAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default);

    /// <summary>
    /// Registers a live-envelope subscriber for a connected mailbox stream.
    /// Notification is best-effort: implementations persist before notifying and
    /// push via <c>TryWrite</c>, so a full channel drops only the live copy — the
    /// envelope still arrives on the next connect's initial replay.
    /// </summary>
    IDisposable SubscribeMailbox(
        BlindedRoutingToken routingToken,
        ChannelWriter<RelayQueuedEnvelopeRecord> writer);

    ValueTask<bool> AcknowledgeEnvelopeAsync(
        BlindedRoutingToken routingToken,
        MailboxQueueId queueId,
        CancellationToken ct = default);
}
