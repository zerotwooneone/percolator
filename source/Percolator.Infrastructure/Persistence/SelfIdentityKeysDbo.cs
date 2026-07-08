using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class SelfIdentityKeysDbo
{
    // Primary key and foreign key to SelfIdentity
    public uint SelfIdentityId { get; set; }

    // X3dhKeys components (private key material blobs, e.g., ECParameters or PKCS#8)
    public byte[] IdentitySigningKey { get; set; } = Array.Empty<byte>();
    public byte[] SignedPreKey { get; set; } = Array.Empty<byte>();
}
