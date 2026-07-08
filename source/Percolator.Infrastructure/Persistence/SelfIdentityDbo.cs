using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class SelfIdentityDbo
{
    public uint Id { get; set; }
    public PublicIdentityId PublicIdentityId { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset LastUsedUtc { get; set; }
    public Percolator.Identity.Model.ListeningPort ListeningPort { get; set; }
    public DeviceId DeviceId { get; set; } = new DeviceId(1);
    public byte[]? ProfileKey { get; set; }
    public byte[]? EncryptedProfileData { get; set; }
    public byte[]? ProfileNonce { get; set; }
    public byte[]? ProfileTag { get; set; }
    public int ProfileRevision { get; set; }
    public byte[]? RelayDeliveryRootKey { get; set; }
    public byte[]? ActiveIdentityKeySpki { get; set; }
    public byte[]? ActiveIdentityKeyFingerprint { get; set; }
    public byte[]? ZkServerSecretParamsSeed { get; set; }

    // Navigation to the associated keys record (one-to-one)
    public SelfIdentityKeysDbo? Keys { get; set; }
}
