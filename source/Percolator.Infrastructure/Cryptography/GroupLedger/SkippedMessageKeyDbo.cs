namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public class SkippedMessageKeyDbo
{
    public Guid ConversationId { get; set; }
    public uint SenderKeyId { get; set; }
    public int MessageIndex { get; set; }
    public byte[] MessageKey { get; set; } = [];
}
