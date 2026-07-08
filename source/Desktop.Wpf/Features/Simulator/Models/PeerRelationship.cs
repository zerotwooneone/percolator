using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator.Models;

public enum RelationshipType
{
    PublishedKey,
    RelayActiveSession
}

public sealed record PeerRelationship(NetworkPeerId SourceNetworkPeerId, NetworkPeerId TargetNetworkPeerId, RelationshipType Type);

public sealed record PeerRelationshipSnapshot(NetworkPeerId SourceNetworkPeerId, NetworkPeerId TargetNetworkPeerId, RelationshipType Type);
