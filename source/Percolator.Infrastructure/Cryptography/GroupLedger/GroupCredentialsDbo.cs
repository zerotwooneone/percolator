namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public class GroupCredentialsDbo
{
    public Guid ConversationId { get; set; }
    public byte[] GroupMasterKey { get; set; } = [];
    public byte[] AuthCredentialMac { get; set; } = [];
}
