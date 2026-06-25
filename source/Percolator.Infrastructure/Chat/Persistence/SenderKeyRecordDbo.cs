namespace Percolator.Infrastructure.Chat.Persistence;

public class SenderKeyRecordDbo
{
    public Guid ConversationId { get; set; }
    public uint SenderPeerId { get; set; }
    public uint DeviceId { get; set; }
    public byte[] RecordBytes { get; set; } = null!;
}
