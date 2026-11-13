namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class RelayPreKeyEntity
{
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();   // 16-byte BlindedRoutingToken — PK component
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

public sealed class RelayPeerDirectoryEntity
{
    public byte[] PeerId { get; set; } = Array.Empty<byte>();
    public int Visibility { get; set; }
    public bool HasAuthenticatedHandshake { get; set; }
    public bool HasActiveSignedPreKey { get; set; }
    public byte[]? RoutingToken { get; set; }    // 16-byte mailbox routing token advertised when discoverable
    public byte[]? DeliveryToken { get; set; }   // 32-byte delivery token advertised when discoverable
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
}

public sealed class RelayRendezvousEntryEntity
{
    public byte[] BlindedLocator { get; set; } = Array.Empty<byte>();
    public byte[]? EncryptedRoute { get; set; }
    public string? RouteDescriptor { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; }
}

public sealed class RelayRegistrationEntity
{
    public Guid RelayIdentityId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public byte[]? PinnedCertificateSha256 { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class RelayMailboxBindingEntity
{
    public Guid RelayIdentityId { get; set; }
    public Guid OwnerIdentityId { get; set; }
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();
    public byte[] DrainToken { get; set; } = Array.Empty<byte>();
    public byte[]? PreviousDrainToken { get; set; }
    public DateTimeOffset LinkedAtUtc { get; set; }
}

public sealed class RelayMailboxCapabilityEntity
{
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();   // PK — 16 bytes
    public byte[] DeliveryToken { get; set; } = Array.Empty<byte>();  // 32 bytes
    public byte[] DrainToken { get; set; } = Array.Empty<byte>();     // 32 bytes
}

public sealed class RelayMailboxAuthorizedTokenEntity
{
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();   // composite PK {RoutingToken, Ordinal}
    public int Ordinal { get; set; }                                  // ring-buffer position (cap 10)
    public byte[] DeliveryToken { get; set; } = Array.Empty<byte>();
}

public sealed class RelayQueuedEnvelopeEntity
{
    public string QueueId { get; set; } = string.Empty;               // PK
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();   // FK → capability, indexed
    public long Ordinal { get; set; }                                 // app-assigned enqueue order (max+1 at insert; permuted by ShuffleQueue)
    public byte[] EnvelopePayload { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedAtUtc { get; set; }
}
