namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class VaultRatchetSessionEntity
{
    public string SessionId { get; set; } = string.Empty;
    public Guid OwnerIdentityId { get; set; }
    public uint OwnerDeviceId { get; set; }
    public Guid RemotePeerId { get; set; }
    public uint RemoteDeviceId { get; set; }
    public byte[] RootKey { get; set; } = Array.Empty<byte>();
    public byte[]? SendChainKey { get; set; }
    public byte[]? RecvChainKey { get; set; }
    public byte[]? LocalEphemeralPriv { get; set; }
    public byte[]? LocalEphemeralPub { get; set; }
    public byte[]? RemoteEphemeralPub { get; set; }
    public uint SendingCounter { get; set; }
    public uint ReceivingCounter { get; set; }
    public uint PreviousChainLength { get; set; }
    public byte[]? RatchetStateBlob { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class VaultGroupSenderKeyEntity
{
    public Guid ChannelId { get; set; }
    public Guid AuthorIdentityId { get; set; }
    public uint AuthorDeviceId { get; set; }
    public uint KeyId { get; set; }
    public uint Iteration { get; set; }
    public byte[] ChainKey { get; set; } = Array.Empty<byte>();
    public byte[]? SigningPriv { get; set; }
    public byte[]? AuthorSigningPub { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class VaultGroupReceiverSessionEntity
{
    public Guid ChannelId { get; set; }
    public Guid AuthorIdentityId { get; set; }
    public uint AuthorDeviceId { get; set; }
    public uint KeyId { get; set; }
    public byte[]? AuthorSigningPub { get; set; }
    public byte[] ChainKey { get; set; } = Array.Empty<byte>();
    public uint ReceivingCounter { get; set; }
    public byte[]? SkippedKeysBlob { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class VaultGroupCredentialsEntity
{
    public Guid ChannelId { get; set; }
    public byte[] MasterKey { get; set; } = Array.Empty<byte>();
    public byte[] AuthCredentialMac { get; set; } = Array.Empty<byte>();
    public uint CurrentEpoch { get; set; }
    public byte[]? LocalBlobKey { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class VaultPendingHandshakeEntity
{
    public Guid RecipientIdentityId { get; set; }
    public Guid SenderIdentityId { get; set; }
    public byte[] EnvelopePayload { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAtUtc { get; set; }
}
