namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class IdentityProfileEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int Role { get; set; }
    public int State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DisabledAtUtc { get; set; }
}

public sealed class LocalDeviceEntity
{
    public Guid OwnerIdentityId { get; set; }
    public uint DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public byte[] DevicePublicKey { get; set; } = Array.Empty<byte>();
    public byte[]? LinkProofSignature { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
}
