namespace Percolator.Infrastructure.Network.RelayLedger;

/// <summary>
/// Database object for storing Relay group ledger state.
/// </summary>
public sealed class RelayGroupStateDbo
{
    public Guid ConversationId { get; set; }
    public uint Epoch { get; set; }
    public byte[] GroupPublicParams { get; set; } = Array.Empty<byte>();
    public byte[] EncryptedProfile { get; set; } = Array.Empty<byte>();
    public int Version { get; set; }
}
