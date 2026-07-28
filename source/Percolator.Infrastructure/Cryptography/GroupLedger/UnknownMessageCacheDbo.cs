namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public class UnknownMessageCacheDbo
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public uint MissingKeyId { get; set; }
    public byte[] Ciphertext { get; set; } = [];
    public long ReceivedAtUtc { get; set; }
}
