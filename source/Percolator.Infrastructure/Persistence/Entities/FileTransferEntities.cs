namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class TransferSessionEntity
{
    public Guid TransferId { get; set; }
    public byte[] InitiatorId { get; set; } = Array.Empty<byte>();
    public int TopologyType { get; set; }
    public int Role { get; set; }
    public int State { get; set; }
    public string RootName { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public int ChunkSizeBytes { get; set; }
    public byte[] MerkleRoot { get; set; } = Array.Empty<byte>();
    public byte[] ConcatenatedLeaves { get; set; } = Array.Empty<byte>();
    public bool AllFilesSelected { get; set; }
    public double TargetSeedRatio { get; set; }
    public long UploadedSizeBytes { get; set; }
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

public sealed class FilterPolicyEntity
{
    public byte[] OwnerId { get; set; } = Array.Empty<byte>();
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TransferSessionCertificateEntity
{
    public Guid TransferId { get; set; }
    public byte[] CertSha256 { get; set; } = Array.Empty<byte>();
    public byte[] CertificateDer { get; set; } = Array.Empty<byte>();
    public byte[] PrivateKeyPkcs8 { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class TransferSessionRecipientEntity
{
    public Guid TransferId { get; set; }
    public byte[] RecipientIdentityId { get; set; } = Array.Empty<byte>();
}

public sealed class TransferSessionSelectedFileEntity
{
    public Guid TransferId { get; set; }
    public int FileIndex { get; set; }
}

public sealed class TransferSessionTransportContextEntity
{
    public Guid TransferId { get; set; }
    public Guid ChannelId { get; set; }
    public byte[] LocalIdentityId { get; set; } = Array.Empty<byte>();
    public byte[] SwarmRendezvousId { get; set; } = Array.Empty<byte>();
    public byte[] EphemeralPeerId { get; set; } = Array.Empty<byte>();
    public byte[] StreamAuthToken { get; set; } = Array.Empty<byte>();
    public int WanPolicy { get; set; }
    public string? LocalRootPath { get; set; }
    public string? RelayEndpointUri { get; set; }
    public byte[]? RelayPipeToken { get; set; }
    public bool IsGroupTransfer { get; set; }
    public byte[]? SenderCertSha256 { get; set; }
}

public sealed class TransferSessionDirectCandidateEntity
{
    public Guid TransferId { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int Kind { get; set; }
}

public sealed class FilterPolicyBlockedPeerEntity
{
    public byte[] OwnerId { get; set; } = Array.Empty<byte>();
    public byte[] BlockedPeerId { get; set; } = Array.Empty<byte>();
}

public sealed class FilterPolicyBlockedRelayEntity
{
    public byte[] OwnerId { get; set; } = Array.Empty<byte>();
    public string RelayEndpointUri { get; set; } = string.Empty;
}
