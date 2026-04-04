namespace Desktop.Wpf.Features.Simulator.Models;

public enum RelationshipType
{
    PublishedKey,
    RelayActiveSession
}

public sealed record PeerRelationship(Guid SourcePeerId, Guid TargetPeerId, RelationshipType Type);

public sealed record PeerRelationshipSnapshot(Guid SourcePeerId, Guid TargetPeerId, RelationshipType Type);
