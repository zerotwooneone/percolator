using System.Net;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Network;
using Percolator.Infrastructure.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorOutboundInterceptor : ISimulatorOutboundInterceptor
{
    private readonly ISimulatorStateService _state;
    private readonly ILogger<SimulatorOutboundInterceptor> _logger;
    private readonly ActiveIdentityContext _active;
    private readonly ISimulatorDiagnosticsService _diagnostics;

    public SimulatorOutboundInterceptor(
        ISimulatorStateService state,
        ILogger<SimulatorOutboundInterceptor> logger,
        ActiveIdentityContext active,
        ISimulatorDiagnosticsService diagnostics)
    {
        _state = state;
        _logger = logger;
        _active = active;
        _diagnostics = diagnostics;
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
        CancellationToken cancellationToken,
        out Task<DeliverInviteHandshakeResponseAck> result)
    {
        if (!TryResolveSimulatedPeerId(endpoint, out var peerId))
        {
            result = Task.FromResult(new DeliverInviteHandshakeResponseAck { Version = 1 });
            return false;
        }

        result = DeliverInviteHandshakeResponseAsync(peerId, request, cancellationToken);
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

    public async Task<SimulatorOutboundInterceptResult> InterceptDeliverOpaqueMessageAsync(
        DnsEndPoint endpoint,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Detection rule: treat any literal dotted-quad 127.77.* as simulator-owned
        if (!IPAddress.TryParse(endpoint.Host, out var ip))
        {
            return new SimulatorOutboundInterceptResult.NotForSimulator();
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return new SimulatorOutboundInterceptResult.NotForSimulator();
        }

        if (bytes[0] != 127 || bytes[1] != 77)
        {
            return new SimulatorOutboundInterceptResult.NotForSimulator();
        }

        // Routing rule: check if endpoint matches a simulated peer using fast index lookup
        if (!_state.TryResolvePeerId(endpoint, out var simulatedPeerId) || simulatedPeerId is null)
        {
            return new SimulatorOutboundInterceptResult.Undeliverable(
                endpoint,
                $"No simulated peer listening at {endpoint.Host}:{endpoint.Port}");
        }

        var match = _state.Peers.FirstOrDefault(p => p.NetworkPeerId == simulatedPeerId.Value);
        if (match is null)
        {
            return new SimulatorOutboundInterceptResult.Undeliverable(
                endpoint,
                $"No simulated peer listening at {endpoint.Host}:{endpoint.Port}");
        }

        // Deliver to simulator
        try
        {
            var response = await _state.ReceiveOpaqueMessageFromMainAsync(match.NetworkPeerId, request, cancellationToken).ConfigureAwait(false);
            return new SimulatorOutboundInterceptResult.DeliveredToSimulator(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[simulator] Failed to deliver opaque message to {SimPeer}", match.NetworkPeerId);
            throw; // Propagate exception as per plan
        }
    }

    private async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(NetworkPeerId simulatedNetworkPeerId, InviteHandshakeResponse response, CancellationToken cancellationToken)
    {
        var correlationId = response.RequestCorrelationId ?? "(missing)";
        _logger.LogInformation("[simulator] Intercepted DeliverInviteHandshakeResponse to {SimPeer} with correlation {CorrelationId}", simulatedNetworkPeerId, correlationId);
        await _state.HandleInboundInviteHandshakeResponseFromMainAsync(simulatedNetworkPeerId, response, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("[simulator] Handshake response processed for {SimPeer} correlation {CorrelationId}", simulatedNetworkPeerId, correlationId);
        return new DeliverInviteHandshakeResponseAck { Version = 1 };
    }

    private async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(
        NetworkPeerId simulatedNetworkPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var inviterPeerId = _active.Identity is not null ? new NetworkPeerId((uint) _active.Identity.SelfIdentityId.Value) : new NetworkPeerId(0);
            return await _state.ReceiveEstablishDirectSessionFromMainAsync(
                    simulatedNetworkPeerId: simulatedNetworkPeerId,
                    mainNetworkPeerId: inviterPeerId,
                    request: request,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[simulator] Failed to intercept EstablishDirectSession to {SimPeer}", simulatedNetworkPeerId);
            return new EstablishDirectSessionResponse
            {
                Version = 1,
                Never = new EstablishDirectSessionResponse.Types.Never()
            };
        }
    }

    private bool TryResolveSimulatedPeerId(DnsEndPoint endpoint, out NetworkPeerId simulatedNetworkPeerId)
    {
        simulatedNetworkPeerId = new NetworkPeerId(0);

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

        if (!_state.TryResolvePeerId(endpoint, out var resolvedPeerId) || resolvedPeerId is null)
        {
            return false;
        }

        simulatedNetworkPeerId = resolvedPeerId.Value;
        return true;
    }
}
