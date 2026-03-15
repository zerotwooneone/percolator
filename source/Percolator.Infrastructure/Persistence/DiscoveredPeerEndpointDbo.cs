namespace Percolator.Infrastructure.Persistence;

public class DiscoveredPeerEndpointDbo
{
    public long Id { get; set; }
    public string DiscoveryKey { get; set; } = null!;
    public string Host { get; set; } = null!;
    public int Port { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
