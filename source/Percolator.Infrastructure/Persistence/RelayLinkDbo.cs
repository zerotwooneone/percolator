namespace Percolator.Infrastructure.Persistence;

public class RelayLinkDbo
{
    public long Id { get; set; }
    public Guid PeerId { get; set; }
    public Guid RelayPeerId { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
