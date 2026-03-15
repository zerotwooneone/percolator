namespace Percolator.Infrastructure.Persistence;

public class DiscoveredPeerDbo
{
    public string DiscoveryKey { get; set; } = null!;
    public byte[]? PublicKeyHash { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int Source { get; set; }
    public double Confidence { get; set; }
    public Guid? BoundPeerId { get; set; }
}
