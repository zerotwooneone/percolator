using Percolator.Network;

namespace Percolator.Infrastructure.Persistence;

public class GrpcEndPointRoutingDbo
{
    public long Id { get; set; }
    public PeerId PeerId { get; set; }
    public string Host { get; set; } = null!;
    public int Port { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
