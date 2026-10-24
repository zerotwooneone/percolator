using System.Security.Cryptography;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
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

        if (job.Payload.IsEmpty)
        {
            return DomainResult.Failure(new DomainError("EMPTY_PAYLOAD", "Outbox job payload is empty."));
        }

        switch (job.Route.Type)
        {
            case DeliveryRouteType.DirectP2P:
            {
                if (!job.RecipientIdentityId.HasValue)
                {
                    return DomainResult.Failure(new DomainError("RECIPIENT_MISSING", "Job recipient identity is missing for DirectP2P."));
                }

                var recipientId = job.RecipientIdentityId.Value;
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
                if (!job.RecipientIdentityId.HasValue)
                {
                    return DomainResult.Failure(new DomainError("RECIPIENT_MISSING", "Job recipient identity is missing for RelayedOneToOne."));
                }

                var recipientId = job.RecipientIdentityId.Value;
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
            case DeliveryRouteType.RelayedGroup:
            {
                if (!_networkSwitch.TryGetRelayForChannel(job.ChannelId, out var relay) || relay is null)
                {
                    return DomainResult.Failure(new DomainError("RELAY_NOT_FOUND", $"No relay found for group channel {job.ChannelId}."));
                }

                var kind = (WireFrameKind)job.Payload.Span[0];
                var sealedBytes = SealedWireFraming.Encode(
                    kind,
                    job.ChannelId,
                    job.OwnerIdentityId,
                    DeviceId.Primary,
                    job.Payload);

                var ledger = relay.LedgerRepository.GetByChannelIdAsync(job.ChannelId).GetAwaiter().GetResult();
                if (ledger is null)
                {
                    return DomainResult.Failure(new DomainError("LEDGER_NOT_FOUND", $"Group ledger not found on relay for {job.ChannelId}."));
                }

                Span<byte> challengeHash = stackalloc byte[32];
                RelayGroupLedger.ComputeDispatchChallenge(sealedBytes, challengeHash);

                byte[] dummy = new byte[32];
                Array.Fill(dummy, (byte)0x42);

                byte[] masterKey = dummy;
                byte[] authMac = dummy;

                if (_networkSwitch.TryGetGroupCredentials(job.ChannelId, out var creds))
                {
                    masterKey = creds.MasterKey.Span.ToArray();
                    authMac = creds.AuthMac.Span.ToArray();
                }

                var zkProof = relay.ZkEngine.GenerateGroupPresentation(
                    ledger.CurrentEpoch.Value,
                    challengeHash,
                    masterKey,
                    authMac);

                return relay.DispatchGroupMessage(job.ChannelId, zkProof, sealedBytes, relay.ZkEngine);
            }
            default:
                return DomainResult.Failure(new DomainError("UNSUPPORTED_ROUTE", $"Route {job.Route.Type} not yet supported in direct loopback."));
        }
    }
}
