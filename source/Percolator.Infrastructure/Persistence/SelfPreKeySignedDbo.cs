using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class SelfPreKeySignedDbo
{
    public int Id { get; set; }
    public SelfId SelfIdentityId { get; set; }
    public Guid SignedPreKeyId { get; set; }
    public byte[] SignedPreKeyPrivate { get; set; } = Array.Empty<byte>();
    public byte[] SignedPreKeyPublicSpki { get; set; } = Array.Empty<byte>();
    public byte[] PreKeySignature { get; set; } = Array.Empty<byte>();
    public DateTimeOffset ExpiresUtc { get; set; }
}
