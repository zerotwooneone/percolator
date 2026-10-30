namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class RelayPreKeyEntity
{
    public Guid OwnerIdentityId { get; set; }
    public uint DeviceId { get; set; }
    public byte[] IdentityKey { get; set; } = Array.Empty<byte>();
    public byte[] SignedPreKey { get; set; } = Array.Empty<byte>();
    public byte[] SignedPreKeySignature { get; set; } = Array.Empty<byte>();
    public string OneTimePreKeysJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class RelayProfileSlotEntity
{
    public byte[] BlindedLocator { get; set; } = Array.Empty<byte>();
    public byte[] WriteTokenHash { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public uint Revision { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class RelayLedgerEpochEntity
{
    public Guid ChannelId { get; set; }
    public Guid RelayIdentityId { get; set; }
    public uint CurrentEpoch { get; set; }
    public byte[] EncryptedRosterBlob { get; set; } = Array.Empty<byte>();
    public byte[] ZkPublicParams { get; set; } = Array.Empty<byte>();
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class RelayLedgerRoutingTokenEntity
{
    public Guid ChannelId { get; set; }
    public Guid RoutingToken { get; set; }
}
