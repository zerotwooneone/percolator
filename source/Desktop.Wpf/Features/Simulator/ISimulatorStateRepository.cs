using Desktop.Wpf.Features.Simulator.Models;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateRepository
{
    Task<IReadOnlyList<SimulatedPeerModel>> LoadPeersAsync(CancellationToken cancellationToken = default);

    Task SavePeersAsync(
        IReadOnlyList<PeerStateSnapshot> peers,
        IReadOnlyList<PeerRelationshipSnapshot> relationships,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PeerRelationship>> LoadRelationshipsAsync(CancellationToken cancellationToken = default);

    Task<SimulatedRelayModel?> LoadRelayAsync(Guid relayHostPeerId, CancellationToken cancellationToken = default);

    Task SaveRelayAsync(RelayStateSnapshot relay, CancellationToken cancellationToken = default);
}
