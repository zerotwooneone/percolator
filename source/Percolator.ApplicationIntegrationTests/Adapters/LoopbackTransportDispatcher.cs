using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class LoopbackTransportDispatcher : ITransportDispatcher
{
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;

    public LoopbackTransportDispatcher(InMemoryLoopbackNetworkSwitch networkSwitch)
    {
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
    }

    public async ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!job.RecipientIdentityId.HasValue)
        {
            return DomainResult.Failure(new DomainError("RECIPIENT_MISSING", "Job recipient identity is missing."));
        }

        if (job.Payload.IsEmpty)
        {
            return DomainResult.Failure(new DomainError("EMPTY_PAYLOAD", "Outbox job payload is empty."));
        }

        var recipientId = job.RecipientIdentityId.Value;

        switch (job.Route.Type)
        {
            case DeliveryRouteType.DirectP2P:
            {
                var kind = (WireFrameKind)job.Payload.Span[0];
                if (kind is not (WireFrameKind.DirectRatchet or WireFrameKind.GroupBroadcast or WireFrameKind.X3dhHandshake))
                {
                    return DomainResult.Failure(new DomainError("INVALID_FRAME_KIND", $"Unknown or unsupported WireFrameKind byte: 0x{job.Payload.Span[0]:X2}."));
                }

                var sealedBytes = SealedWireFraming.Encode(
                    kind,
                    job.ChannelId,
                    job.OwnerIdentityId,
                    DeviceId.Primary,
                    job.Payload);

                return await _networkSwitch.SendToPeerAsync(recipientId, sealedBytes).ConfigureAwait(false);
            }
            case DeliveryRouteType.RelayedOneToOne:
            {
                if (!_networkSwitch.TryGetRelayForRecipient(recipientId, out var relay) || relay is null)
                {
                    return DomainResult.Failure(new DomainError("RELAY_NOT_FOUND", $"No relay found for recipient {recipientId}."));
                }

                if (!relay.TryGetClientTokens(recipientId, out var tokens))
                {
                    return DomainResult.Failure(new DomainError("TOKENS_NOT_FOUND", $"Recipient {recipientId} is not registered on relay {relay.Name}."));
                }

                var kind = (WireFrameKind)job.Payload.Span[0];
                var sealedBytes = SealedWireFraming.Encode(
                    kind,
                    job.ChannelId,
                    job.OwnerIdentityId,
                    DeviceId.Primary,
                    job.Payload);

                var now = relay.TimeProvider.UtcNow;
                var envelope = new MailboxEnvelope(
                    EnvelopeId.New(),
                    tokens.RoutingToken,
                    sealedBytes,
                    now,
                    now.AddMinutes(10));

                return relay.MailboxQueue.Enqueue(envelope, tokens.DeliveryToken, relay.TimeProvider);
            }
            default:
                return DomainResult.Failure(new DomainError("UNSUPPORTED_ROUTE", $"Route {job.Route.Type} not yet supported in direct loopback."));
        }
    }
}
