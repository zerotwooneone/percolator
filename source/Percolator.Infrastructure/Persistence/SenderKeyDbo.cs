namespace Percolator.Infrastructure.Persistence;

public class SenderKeyDbo
{
    public Guid ConversationId { get; set; }
    public Guid SenderPeerId { get; set; }
    public byte[] ChainKey { get; set; } = Array.Empty<byte>();
    public byte[] SigningKey { get; set; } = Array.Empty<byte>();
}
