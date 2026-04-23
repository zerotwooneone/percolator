using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator.Models;

public enum RelationshipType
{
    PublishedKey,
    RelayActiveSession
}

public sealed record PeerRelationship(PeerId SourcePeerId, PeerId TargetPeerId, RelationshipType Type);

public sealed record PeerRelationshipSnapshot(PeerId SourcePeerId, PeerId TargetPeerId, RelationshipType Type);
