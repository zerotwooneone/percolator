namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public class SenderKeyRatchetDbo
{
    public Guid ConversationId { get; set; }
    public uint SenderKeyId { get; set; }
    public byte[] AuthorPublicIdentityId { get; set; } = [];
    public byte[] ChainKey { get; set; } = [];
    public byte[] SignatureKey { get; set; } = [];
}
