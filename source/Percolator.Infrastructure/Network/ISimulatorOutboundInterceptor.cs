using System.Net;
using Percolator.Contracts;

namespace Percolator.Infrastructure.Network;

public interface ISimulatorOutboundInterceptor
{
    bool TryEstablishDirectSession(
        DnsEndPoint endpoint,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken,
        out Task<EstablishDirectSessionResponse> result);

    bool TryEstablishSession(
        DnsEndPoint endpoint,
        EstablishSessionRequest request,
        CancellationToken cancellationToken,
        out Task<EstablishSessionResponse> result);

    bool TryDeliverInviteHandshakeResponse(
        DnsEndPoint endpoint,
        InviteHandshakeResponse request,
        out Task<DeliverInviteHandshakeResponseAck> result);

    /// <summary>
    /// Intercepts outbound opaque message sends to simulator-reserved endpoints.
    /// Returns a discriminated-union result:
    /// - NotForSimulator: proceed with normal network send
    /// - DeliveredToSimulator: message was delivered; contains the simulator's response
    /// - Undeliverable: simulator-owned endpoint has no matching simulated peer
    /// </summary>
    Task<SimulatorOutboundInterceptResult> InterceptDeliverOpaqueMessageAsync(
        DnsEndPoint endpoint,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken);
}
