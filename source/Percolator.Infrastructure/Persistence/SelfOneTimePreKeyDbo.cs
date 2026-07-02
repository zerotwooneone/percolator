using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class SelfOneTimePreKeyDbo
{
    public int Id { get; set; }
    public SelfId SelfIdentityId { get; set; }
    public Guid OneTimePreKeyId { get; set; }
    public byte[] OneTimePreKeyPrivate { get; set; } = Array.Empty<byte>();
    public byte[] OneTimePreKeyPublicSpki { get; set; } = Array.Empty<byte>();

    public Guid? ReservedForRequestCorrelationId { get; set; }
    public DateTimeOffset? ReservedUntilUtc { get; set; }
}
