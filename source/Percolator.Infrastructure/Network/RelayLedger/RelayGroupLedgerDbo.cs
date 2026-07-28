namespace Percolator.Infrastructure.Network.RelayLedger;

public class RelayGroupLedgerDbo
{
    public Guid ConversationId { get; set; }
    public uint Epoch { get; set; }
    public byte[] EncryptedEntriesBlob { get; set; } = [];
}
