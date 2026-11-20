namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class TransferSessionEntity
{
    public Guid TransferId { get; set; }
    public Guid ChannelId { get; set; }
    public byte[] LocalIdentityId { get; set; } = Array.Empty<byte>();
    public byte[] InitiatorId { get; set; } = Array.Empty<byte>();
    public int TopologyType { get; set; }
    public string RecipientsJson { get; set; } = string.Empty;
    public int Role { get; set; }
    public int State { get; set; }
    public string RootName { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public int ChunkSizeBytes { get; set; }
    public byte[] MerkleRoot { get; set; } = Array.Empty<byte>();
    public byte[] ConcatenatedLeaves { get; set; } = Array.Empty<byte>();
    public string? SelectedFilesJson { get; set; }
    public double TargetSeedRatio { get; set; }
    public long UploadedSizeBytes { get; set; }
    public string? LocalRootPath { get; set; }
    public int WanPolicy { get; set; }
    public byte[] TransferKey { get; set; } = Array.Empty<byte>();
    public byte[] BaseNonce { get; set; } = Array.Empty<byte>();
    public byte[] StreamAuthToken { get; set; } = Array.Empty<byte>();
    public string? RelayEndpointUri { get; set; }
    public byte[]? RelayPipeToken { get; set; }
    public string DirectCandidatesJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class ManifestFileEntity
{
    public Guid TransferId { get; set; }
    public int FileIndex { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public long StartOffset { get; set; }
    public long LastModifiedUnixSeconds { get; set; }
}

public sealed class TransferBitfieldEntity
{
    public Guid TransferId { get; set; }
    public byte[] BitfieldBytes { get; set; } = Array.Empty<byte>();
    public int TotalPieces { get; set; }
    public int VerifiedPieces { get; set; }
}

public sealed class SwarmEndpointEntity
{
    public byte[] RendezvousId { get; set; } = Array.Empty<byte>();
    public byte[] PeerId { get; set; } = Array.Empty<byte>();
    public string Endpoint { get; set; } = string.Empty;
    public int PeerRole { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class FilterPolicyEntity
{
    public byte[] OwnerId { get; set; } = Array.Empty<byte>();
    public string BlockedPeersJson { get; set; } = string.Empty;
    public string BlockedRelaysJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
