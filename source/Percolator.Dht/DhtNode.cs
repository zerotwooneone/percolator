using System.Net;

namespace Percolator.Dht;

public class DhtNode
{
    public NodeId Id { get; }
    public IPEndPoint EndPoint { get; private set; }
    public DateTimeOffset LastSeenUtc { get; private set; }

    public DhtNode(NodeId id, IPEndPoint endPoint, DateTimeOffset lastSeenUtc)
    {
        Id = id;
        EndPoint = endPoint;
        LastSeenUtc = lastSeenUtc;
    }

    public void Update(IPEndPoint newEndPoint)
    {
        EndPoint = newEndPoint;
        LastSeenUtc = DateTimeOffset.UtcNow;
    }
}
