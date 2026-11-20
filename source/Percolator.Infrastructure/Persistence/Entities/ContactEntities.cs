namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class PeerContactEntity
{
    public Guid OwnerIdentityId { get; set; }
    public Guid RemotePeerId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public int TrustLevel { get; set; }
    public int State { get; set; }
    public byte[]? PrimaryPublicKey { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class PeerContactDeviceEntity
{
    public Guid OwnerIdentityId { get; set; }
    public Guid RemotePeerId { get; set; }
    public uint DeviceId { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; }
}
