namespace Percolator.Infrastructure.Persistence;

public sealed class RelayGroupStateDbo
{
    public Guid ConversationId { get; set; }
    public uint Epoch { get; set; }
    public byte[] GroupPublicParams { get; set; } = Array.Empty<byte>();
    public int Version { get; set; }
}
