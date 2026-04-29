using System.Net;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.Application.Network;

internal sealed class InviteHandshakeResponseDeliveryService : IInviteHandshakeResponseDeliveryService
{
    private readonly ILogger<InviteHandshakeResponseDeliveryService> _logger;
    private readonly IGrpcSessionService _grpc;
    private readonly IRelayTopology _relayTopology;
    private readonly ITransportPort _transport;
    private readonly IOutboundMessageWireTap _wireTap;

    public InviteHandshakeResponseDeliveryService(
        ILogger<InviteHandshakeResponseDeliveryService> logger,
        IGrpcSessionService grpc,
        IRelayTopology relayTopology,
        ITransportPort transport,
        IOutboundMessageWireTap wireTap)
    {
        _logger = logger;
        _grpc = grpc;
        _relayTopology = relayTopology;
        _transport = transport;
        _wireTap = wireTap;
    }

    public async Task<InviteHandshakeResponseDeliveryResult> DeliverAsync(
        Percolator.Network.PeerId inviterPeerId,
        DnsEndPoint? directCallbackEndpoint,
        InviteHandshakeResponse response,
        CancellationToken ct = default)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        if (_wireTap.Enabled && _wireTap.Mode == SimulatorOutboundMode.SimulateOnly)
        {
            const string sendPath = "Simulated";
            _wireTap.Tap(new OutboundWireMessage(
                DestinationPeerId: inviterPeerId,
                SendPath: sendPath,
                MessageType: nameof(InviteHandshakeResponse),
                RequestCorrelationId: response.HasRequestCorrelationId ? response.RequestCorrelationId : null,
                PayloadBytes: response.ToByteArray(),
                PayloadLength: response.CalculateSize()));
            return new InviteHandshakeResponseDeliveryResult(true, sendPath);
        }

        if (directCallbackEndpoint is not null)
        {
            try
            {
                _ = await _grpc.DeliverInviteHandshakeResponseAsync(directCallbackEndpoint, response).ConfigureAwait(false);
                if (_wireTap.Enabled)
                {
                    _wireTap.Tap(new OutboundWireMessage(
                        DestinationPeerId: inviterPeerId,
                        SendPath: "Direct",
                        MessageType: nameof(InviteHandshakeResponse),
                        RequestCorrelationId: response.HasRequestCorrelationId ? response.RequestCorrelationId : null,
                        PayloadBytes: response.ToByteArray(),
                        PayloadLength: response.CalculateSize()));
                }
                return new InviteHandshakeResponseDeliveryResult(true, "Direct");
            }
            catch (Exception ex)
            {
                if (_wireTap.Enabled)
                {
                    _wireTap.Tap(new OutboundWireMessage(
                        DestinationPeerId: inviterPeerId,
                        SendPath: "Direct",
                        MessageType: nameof(InviteHandshakeResponse),
                        RequestCorrelationId: response.HasRequestCorrelationId ? response.RequestCorrelationId : null,
                        PayloadBytes: response.ToByteArray(),
                        PayloadLength: response.CalculateSize()));
                }
                _logger.LogWarning(ex, "Failed to deliver InviteHandshakeResponse via direct callback endpoint {Endpoint}", directCallbackEndpoint);
                return new InviteHandshakeResponseDeliveryResult(false, "Direct", ex);
            }
        }

        Percolator.Network.PeerId? relay;
        try
        {
            relay = await _relayTopology.GetRelayForAsync(inviterPeerId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new InviteHandshakeResponseDeliveryResult(false, "Relay", ex);
        }

        if (relay is null)
        {
            return new InviteHandshakeResponseDeliveryResult(false, "Relay", new InvalidOperationException("No relay configured for target"));
        }

        try
        {
            var payload = NetworkPayload.FromArray(response.ToByteArray());
            var relayResult = await _transport.SendViaRelayAsync(new PeerId(relay.Value), inviterPeerId, payload, ct).ConfigureAwait(false);
            if (relayResult.Ok)
            {
                var sendPath = $"Relay:{relay.Value}";
                if (_wireTap.Enabled)
                {
                    _wireTap.Tap(new OutboundWireMessage(
                        DestinationPeerId: inviterPeerId,
                        SendPath: sendPath,
                        MessageType: nameof(InviteHandshakeResponse),
                        RequestCorrelationId: response.HasRequestCorrelationId ? response.RequestCorrelationId : null,
                        PayloadBytes: response.ToByteArray(),
                        PayloadLength: response.CalculateSize()));
                }
                return new InviteHandshakeResponseDeliveryResult(true, sendPath);
            }

            if (_wireTap.Enabled)
            {
                _wireTap.Tap(new OutboundWireMessage(
                    DestinationPeerId: inviterPeerId,
                    SendPath: $"Relay:{relay.Value}",
                    MessageType: nameof(InviteHandshakeResponse),
                    RequestCorrelationId: response.HasRequestCorrelationId ? response.RequestCorrelationId : null,
                    PayloadBytes: response.ToByteArray(),
                    PayloadLength: response.CalculateSize()));
            }
            return new InviteHandshakeResponseDeliveryResult(false, $"Relay:{relay.Value}", relayResult.Error ?? new InvalidOperationException(relayResult.Reason?.ToString() ?? "Relay send failed"));
        }
        catch (Exception ex)
        {
            if (_wireTap.Enabled)
            {
                _wireTap.Tap(new OutboundWireMessage(
                    DestinationPeerId: inviterPeerId,
                    SendPath: $"Relay:{relay.Value}",
                    MessageType: nameof(InviteHandshakeResponse),
                    RequestCorrelationId: response.HasRequestCorrelationId ? response.RequestCorrelationId : null,
                    PayloadBytes: response.ToByteArray(),
                    PayloadLength: response.CalculateSize()));
            }
            _logger.LogWarning(ex, "Failed to deliver InviteHandshakeResponse via relay {Relay} to {Target}", relay.Value, inviterPeerId);
            return new InviteHandshakeResponseDeliveryResult(false, $"Relay:{relay.Value}", ex);
        }
    }
}
