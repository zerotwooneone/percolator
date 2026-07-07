using Percolator.Network;

namespace Percolator.Infrastructure.Persistence;

public class RelayLinkDbo
{
    public long Id { get; set; }
    public PeerId PeerId { get; set; }
    public PeerId RelayPeerId { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
