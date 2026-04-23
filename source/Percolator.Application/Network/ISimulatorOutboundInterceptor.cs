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

    bool TryDeliverOpaqueMessage(
        DnsEndPoint endpoint,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken,
        out Task<DeliverOpaqueMessageResponse> result);

    /// <summary>
    /// Checks if a message to the specified peer should be routed via simulator relay.
    /// If the peer is a simulated peer connected via relay, enqueues the message in the simulator's
    /// relay queue and returns true. Otherwise, returns false to proceed with normal network send.
    /// </summary>
    /// <param name="recipientPublicKeyHash">The recipient identity PKH (32 bytes)</param>
    /// <param name="cipherBytes">The encrypted message payload (session cipher)</param>
    /// <param name="debugType">Optional debug type for diagnostics</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the message was enqueued in simulator relay (skip network send), false otherwise</returns>
    Task<bool> TryRouteMessageViaSimulatorRelayAsync(
        byte[] recipientPublicKeyHash,
        byte[] cipherBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);
}
