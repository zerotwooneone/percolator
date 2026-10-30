namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class PeerProfileEntity
{
    public Guid PeerId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? StatusBio { get; set; }
    public string? AvatarReferenceJson { get; set; }
    public uint Revision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class PeerProfileSnapshotEntity
{
    public Guid PeerId { get; set; }
    public string SourceEndpoint { get; set; } = string.Empty;
    public int SourceType { get; set; } // 1 = DirectP2P, 2 = Relay
    public byte[] BlindedLocator { get; set; } = Array.Empty<byte>();
    public string MetadataJson { get; set; } = string.Empty;
    public DateTimeOffset LastObservedAtUtc { get; set; }
}

public sealed class PeerMailboxRoutingEntity
{
    public Guid PeerId { get; set; }
    public string RelayEndpoint { get; set; } = string.Empty;
    public Guid RoutingToken { get; set; }
    public byte[] DeliveryToken { get; set; } = Array.Empty<byte>();
    public DateTimeOffset LastObservedAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public DateTimeOffset? BackpressureCooldownUntilUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
