using System.Net;

namespace Percolator.Network
{
    public class Peer
    {
        public IPAddress Address { get; }
        public int GrpcPort { get; }
        public DateTime LastSeenUtc { get; set; }

        public Peer(IPAddress address, int grpcPort)
        {
            Address = address;
            GrpcPort = grpcPort;
            LastSeenUtc = DateTime.UtcNow;
        }

        public IPEndPoint GrpcEndpoint => new(Address, GrpcPort);

        public override bool Equals(object? obj)
        {
            return obj is Peer peer && GrpcEndpoint.Equals(peer.GrpcEndpoint);
        }

        public override int GetHashCode()
        {
            return GrpcEndpoint.GetHashCode();
        }

        public override string ToString()
        {
            return GrpcEndpoint.ToString();
        }
    }
}
