using System.Net;

namespace Percolator.Dht;

public class DhtNode
{
    public NodeId Id { get; }
    public DnsEndPoint EndPoint { get; private set; }
    public DateTimeOffset LastSeenUtc { get; private set; }

    public DhtNode(NodeId id, DnsEndPoint endPoint, DateTimeOffset lastSeenUtc)
    {
        Id = id;
        EndPoint = endPoint;
        LastSeenUtc = lastSeenUtc;
    }

    public void Update(DnsEndPoint newEndPoint)
    {
        EndPoint = newEndPoint;
        LastSeenUtc = DateTimeOffset.UtcNow;
    }
}
