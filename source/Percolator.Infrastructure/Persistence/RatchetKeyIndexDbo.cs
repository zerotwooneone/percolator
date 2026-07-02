namespace Percolator.Infrastructure.Persistence;

public class RatchetKeyIndexDbo
{
    public int Id { get; set; }
    public uint SelfIdentityId { get; set; }
    public Guid DirectSessionId { get; set; }
    public byte[] RatchetPublicKey { get; set; } = Array.Empty<byte>();
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
