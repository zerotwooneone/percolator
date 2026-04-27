using System.Net;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network;

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
    /// Returns a 3-state result indicating whether the message is for the simulator,
    /// was delivered to the simulator, or is undeliverable (no matching simulated peer).
    /// </summary>
    Task<SimulatorOutboundInterceptResult> InterceptDeliverOpaqueMessageAsync(
        DnsEndPoint endpoint,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken);
}
