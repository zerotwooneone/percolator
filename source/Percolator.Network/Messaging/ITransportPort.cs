namespace Percolator.Network.Messaging;

public interface ITransportPort
{
    // Attempt direct delivery to target. Implementation decides endpoint selection.
    Task<TransportSendResult> SendDirectAsync(PeerId target, NetworkPayload payload, CancellationToken ct = default);

    // Attempt relay delivery using the relay peer to the final target. Implementation handles envelope construction.
    Task<TransportSendResult> SendViaRelayAsync(PeerId relay, PeerId target, NetworkPayload payload, CancellationToken ct = default);
}
