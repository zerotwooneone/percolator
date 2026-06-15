namespace Percolator.Infrastructure.Persistence;

public class SelfIdentityDbo
{
    public int Id { get; set; }
    public Guid PeerId { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset LastUsedUtc { get; set; }
    public int ListeningPort { get; set; }
    public uint DeviceId { get; set; } = 1;
    public byte[]? ProfileKey { get; set; }
    public byte[]? EncryptedProfileData { get; set; }
    public byte[]? ProfileNonce { get; set; }
    public byte[]? ProfileTag { get; set; }
    public int ProfileRevision { get; set; }
    public byte[]? RelayDeliveryRootKey { get; set; }

    // Navigation to the associated keys record (one-to-one)
    public SelfIdentityKeysDbo? Keys { get; set; }
}
