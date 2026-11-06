using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.Simulator.Features.Logging;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatorTransportDispatcher : ITransportDispatcher
{
    private readonly ITransportClientInvoker _invoker;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorLogSink _logSink;
    private readonly string _senderDisplayName;

    public SimulatorTransportDispatcher(
        ITransportClientInvoker invoker,
        ISimulatedWorldStateService worldState,
        ISimulatorLogSink logSink,
        string senderDisplayName)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _senderDisplayName = senderDisplayName;
    }

    public async ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var route = job.Route;
        try
        {
            switch (route.Type)
            {
                case DeliveryRouteType.DirectP2P:
                {
                    Uri? targetEndpoint = route.DirectEndpoint;
                    if (targetEndpoint == null && job.RecipientIdentityId.HasValue)
                    {
                        var targetHost = job.RecipientIdentityId.HasValue ? _worldState.FindPersonaHostByIdentity(job.RecipientIdentityId.Value) : null;
                        if (targetHost != null)
                        {
                            if (!targetHost.IsListening.CurrentValue)
                            {
                                return DomainResult.Failure(new DomainError(
                                    "PEER_MUTED", $"Target peer '{targetHost.DisplayName}' has transport muted."));
                            }
                            targetEndpoint = targetHost.BoundEndpoint;
                        }
                    }

                    if (targetEndpoint == null)
                    {
                        return DomainResult.Failure(new DomainError(
                            "MISSING_DIRECT_ENDPOINT", $"Unable to resolve direct P2P endpoint for recipient {job.RecipientIdentityId}."));
                    }

                    WireFrameKind kind = DetermineWireFrameKind(job.Payload);

                    byte[] framedPayload = SealedWireFraming.Encode(
                        kind,
                        job.ChannelId,
                        job.OwnerIdentityId,
                        DeviceId.Primary,
                        job.Payload);

                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = route.TargetToken.HasValue
                            ? ByteString.CopyFrom(route.TargetToken.Value.ToByteArray())
                            : ByteString.Empty,
                        DeliveryToken = route.TargetDeliveryToken != null
                            ? ByteString.CopyFrom(route.TargetDeliveryToken.Span)
                            : ByteString.Empty,
                        EncryptedPackage = ByteString.CopyFrom(framedPayload),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var ack = await _invoker.DeliverDirectAsync(targetEndpoint, envelopeProto, ct);
                    if (!ack.Success)
                    {
                        return DomainResult.Failure(new DomainError(
                            "DELIVERY_REJECTED", ack.ErrorMessage ?? "Direct P2P delivery rejected."));
                    }

                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Debug,
                        _senderDisplayName,
                        null,
                        "TransportOutbox",
                        $"Direct envelope ({kind}) dispatched to {targetEndpoint} successfully."));

                    return DomainResult.Success();
                }

                case DeliveryRouteType.RelayedOneToOne:
                {
                    var relayEndpoint = route.RelayEndpoint;
                    var targetRoutingToken = route.TargetToken;
                    var targetDeliveryToken = route.TargetDeliveryToken;

                    if (relayEndpoint == null || !targetRoutingToken.HasValue || targetDeliveryToken == null)
                    {
                        var targetHost = job.RecipientIdentityId.HasValue ? _worldState.FindPersonaHostByIdentity(job.RecipientIdentityId.Value) : null;
                        if (targetHost?.HomeRelay != null)
                        {
                            relayEndpoint ??= RelayEndpoint.FromHostAndPort("127.0.0.1", targetHost.HomeRelay.BoundPort);
                            targetRoutingToken ??= targetHost.MailboxRoutingToken;
                            targetDeliveryToken ??= targetHost.MailboxDeliveryToken;
                        }
                    }

                    if (relayEndpoint == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "Relay endpoint is required for RelayedOneToOne route."));
                    }

                    var endpointStr = relayEndpoint.Value;
                    var relayUri = endpointStr.Contains("://")
                        ? new Uri(endpointStr)
                        : new Uri($"https://{endpointStr}");

                    WireFrameKind kind = DetermineWireFrameKind(job.Payload);

                    byte[] framedPayload = SealedWireFraming.Encode(
                        kind,
                        job.ChannelId,
                        job.OwnerIdentityId,
                        DeviceId.Primary,
                        job.Payload);

                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = targetRoutingToken.HasValue
                            ? ByteString.CopyFrom(targetRoutingToken.Value.ToByteArray())
                            : ByteString.Empty,
                        DeliveryToken = targetDeliveryToken != null
                            ? ByteString.CopyFrom(targetDeliveryToken.Span)
                            : ByteString.Empty,
                        EncryptedPackage = ByteString.CopyFrom(framedPayload),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var ack = await _invoker.EnqueueMailboxAsync(relayUri, envelopeProto, ct);
                    if (!ack.Accepted)
                    {
                        return DomainResult.Failure(new DomainError("RELAY_ENQUEUE_REJECTED", "Relay rejected mailbox enqueue."));
                    }

                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Debug,
                        _senderDisplayName,
                        null,
                        "TransportOutbox",
                        $"Relayed envelope ({kind}) enqueued to {relayUri} successfully."));

                    return DomainResult.Success();
                }

                default:
                    return DomainResult.Failure(new DomainError(
                        "UNSUPPORTED_ROUTE", $"Route type {route.Type} not yet supported in transport dispatcher."));
            }
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                _senderDisplayName,
                null,
                "TransportOutbox",
                $"Transport dispatch error: {ex.Message}",
                ex.ToString()));

            return DomainResult.Failure(new DomainError("TRANSPORT_ERROR", ex.Message));
        }
    }

    private static WireFrameKind DetermineWireFrameKind(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var hs = HandshakeInvitationPackageProto.Parser.ParseFrom(payload.Span);
            if (hs.InitiatorIdentityKey.Length == 32 && hs.InitiatorEphemeralKey.Length == 32)
            {
                return WireFrameKind.X3dhHandshake;
            }
        }
        catch
        {
            // Direct ratchet or other wire format
        }

        return WireFrameKind.DirectRatchet;
    }
}
