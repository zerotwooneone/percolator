using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorOutboundInterceptor : ISimulatorOutboundInterceptor
{
    private readonly ISimulatorStateService _state;
    private readonly ISimulatedPeerRuntimeService _peerRuntime;
    private readonly ILogger<SimulatorOutboundInterceptor> _logger;

    public SimulatorOutboundInterceptor(
        ISimulatorStateService state,
        ISimulatedPeerRuntimeService peerRuntime,
        ILogger<SimulatorOutboundInterceptor> logger)
    {
        _state = state;
        _peerRuntime = peerRuntime;
        _logger = logger;
    }

    public bool TryDeliverInviteHandshakeResponse(
        DnsEndPoint endpoint,
        InviteHandshakeResponse request,
        out Task<DeliverInviteHandshakeResponseAck> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new DeliverInviteHandshakeResponseAck { Version = 1 });
            return false;
        }

        result = DeliverInviteHandshakeResponseAsync(peerId, request);
        return true;
    }

    public bool TryEstablishSession(
        DnsEndPoint endpoint,
        EstablishSessionRequest request,
        CancellationToken cancellationToken,
        out Task<EstablishSessionResponse> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new EstablishSessionResponse
            {
                Version = 1,
                Never = new EstablishSessionResponse.Types.Never { Version = 1 }
            });
            return false;
        }

        _logger.LogInformation("[simulator] Intercepted EstablishSession to {SimPeer}", peerId);
        result = Task.FromResult(new EstablishSessionResponse
        {
            Version = 1,
            Never = new EstablishSessionResponse.Types.Never { Version = 1 }
        });
        return true;
    }

    public bool TryDeliverOpaqueMessage(
        DnsEndPoint endpoint,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken,
        out Task<DeliverOpaqueMessageResponse> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new DeliverOpaqueMessageResponse { Version = 1 });
            return false;
        }

        result = _peerRuntime.ReceiveOpaqueMessageFromMainAsync(peerId, request, cancellationToken);
        return true;
    }

    private async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(Guid simulatedPeerId, InviteHandshakeResponse response)
    {
        _logger.LogInformation("[simulator] Intercepted DeliverInviteHandshakeResponse to {SimPeer}", simulatedPeerId);
        await _peerRuntime.ReceiveInviteHandshakeResponseFromMainAsync(simulatedPeerId, response).ConfigureAwait(false);
        return new DeliverInviteHandshakeResponseAck { Version = 1 };
    }

    private bool TryResolveSimulatedPeerId(DnsEndPoint endpoint, out Guid simulatedPeerId)
    {
        simulatedPeerId = default;

        if (!IPAddress.TryParse(endpoint.Host, out var ip))
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        if (bytes[0] != 127 || bytes[1] != 77)
        {
            return false;
        }

        var match = _state.Peers.FirstOrDefault(p =>
            string.Equals(p.Connection.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && p.Connection.Port == endpoint.Port);

        if (match is null)
        {
            return false;
        }

        simulatedPeerId = match.PeerId;
        return true;
    }
}
