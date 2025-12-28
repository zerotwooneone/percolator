using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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

    public InviteHandshakeResponseDeliveryService(
        ILogger<InviteHandshakeResponseDeliveryService> logger,
        IGrpcSessionService grpc,
        IRelayTopology relayTopology,
        ITransportPort transport)
    {
        _logger = logger;
        _grpc = grpc;
        _relayTopology = relayTopology;
        _transport = transport;
    }

    public async Task<InviteHandshakeResponseDeliveryResult> DeliverAsync(
        Percolator.Network.PeerId inviterPeerId,
        DnsEndPoint? directCallbackEndpoint,
        InviteHandshakeResponse response,
        CancellationToken ct = default)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        if (directCallbackEndpoint is not null)
        {
            try
            {
                _ = await _grpc.DeliverInviteHandshakeResponseAsync(directCallbackEndpoint, response).ConfigureAwait(false);
                return new InviteHandshakeResponseDeliveryResult(true, "Direct");
            }
            catch (Exception ex)
            {
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
            var (ok, _, reason, error) = await _transport.SendViaRelayAsync(new PeerId(relay.Value), inviterPeerId, payload, ct).ConfigureAwait(false);
            if (ok)
            {
                return new InviteHandshakeResponseDeliveryResult(true, $"Relay:{relay.Value}");
            }

            return new InviteHandshakeResponseDeliveryResult(false, $"Relay:{relay.Value}", error ?? new InvalidOperationException(reason?.ToString() ?? "Relay send failed"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deliver InviteHandshakeResponse via relay {Relay} to {Target}", relay.Value, inviterPeerId);
            return new InviteHandshakeResponseDeliveryResult(false, $"Relay:{relay.Value}", ex);
        }
    }
}
