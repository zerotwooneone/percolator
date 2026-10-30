namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class VaultIdentityKeyEntity
{
    public Guid Id { get; set; }
    public byte[] PrivateKeyCiphertext { get; set; } = Array.Empty<byte>();
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class VaultSignedPreKeyEntity
{
    public Guid OwnerIdentityId { get; set; }
    public uint DeviceId { get; set; }
    public byte[] PrivateKeyCiphertext { get; set; } = Array.Empty<byte>();
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public byte[] Signature { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class VaultOneTimePreKeyEntity
{
    public Guid OwnerIdentityId { get; set; }
    public uint DeviceId { get; set; }
    public uint KeyId { get; set; }
    public byte[] PrivateKeyCiphertext { get; set; } = Array.Empty<byte>();
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public bool IsConsumed { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
