namespace Percolator.Infrastructure.Persistence;

public class RelayLinkDbo
{
    public long Id { get; set; }
    public uint PeerId { get; set; }
    public uint RelayPeerId { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
