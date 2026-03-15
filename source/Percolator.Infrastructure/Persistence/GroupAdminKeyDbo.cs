namespace Percolator.Infrastructure.Persistence;

public class GroupAdminKeyDbo
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public byte[] AdminPublicKeySpki { get; set; } = Array.Empty<byte>();
    public DateTimeOffset AddedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
