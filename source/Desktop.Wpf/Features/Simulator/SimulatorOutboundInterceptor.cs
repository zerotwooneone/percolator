using System;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorOutboundInterceptor : ISimulatorOutboundInterceptor
{
    private readonly ISimulatorStateService _state;
    private readonly ILogger<SimulatorOutboundInterceptor> _logger;
    private readonly ActiveIdentityContext _active;

    public SimulatorOutboundInterceptor(
        ISimulatorStateService state,
        ILogger<SimulatorOutboundInterceptor> logger,
        ActiveIdentityContext active)
    {
        _state = state;
        _logger = logger;
        _active = active;
    }

    public bool TryEstablishDirectSession(
        DnsEndPoint endpoint,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken,
        out Task<EstablishDirectSessionResponse> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new EstablishDirectSessionResponse
            {
                Version = 1,
                Never = new EstablishDirectSessionResponse.Types.Never()
            });
            return false;
        }

        _logger.LogInformation("[simulator] Intercepted EstablishDirectSession to {SimPeer}", peerId);
        result = EstablishDirectSessionAsync(peerId, request, cancellationToken);
        return true;
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
        result = _state.ReceiveEstablishSessionFromMainAsync(peerId, request, cancellationToken);
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

        result = _state.ReceiveOpaqueMessageFromMainAsync(peerId, request, cancellationToken);
        return true;
    }

    private async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(PeerId simulatedPeerId, InviteHandshakeResponse response)
    {
        _logger.LogInformation("[simulator] Intercepted DeliverInviteHandshakeResponse to {SimPeer}", simulatedPeerId);
        await _state.ReceiveInviteHandshakeResponseFromMainAsync(simulatedPeerId, response).ConfigureAwait(false);
        return new DeliverInviteHandshakeResponseAck { Version = 1 };
    }

    private async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(
        PeerId simulatedPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var inviterPeerId = _active.Identity is not null ? new PeerId(_active.Identity.Id) : new PeerId(Guid.Empty);
            return await _state.ReceiveEstablishDirectSessionFromMainAsync(
                    simulatedPeerId: simulatedPeerId,
                    mainPeerId: inviterPeerId,
                    request: request,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[simulator] Failed to intercept EstablishDirectSession to {SimPeer}", simulatedPeerId);
            return new EstablishDirectSessionResponse
            {
                Version = 1,
                Never = new EstablishDirectSessionResponse.Types.Never()
            };
        }
    }

    private bool TryResolveSimulatedPeerId(DnsEndPoint endpoint, out PeerId simulatedPeerId)
    {
        simulatedPeerId = new PeerId(Guid.Empty);

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
            !string.IsNullOrWhiteSpace(p.Host.CurrentValue)
            && string.Equals(p.Host.CurrentValue, endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && p.Port.CurrentValue == endpoint.Port);

        if (match is null)
        {
            return false;
        }

        simulatedPeerId = match.PeerId;
        return true;
    }

}
