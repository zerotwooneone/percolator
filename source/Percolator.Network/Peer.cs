using System.Net;

namespace Percolator.Network
{
    public class Peer : IEquatable<Peer>
    {
        public PeerId Id { get; }
        public IPAddress IpAddress { get; }
        public int GrpcPort { get; }
        public string Thumbprint { get; }
        public DateTime LastSeenUtc { get; set; }

        public Peer(PeerId id, IPAddress ipAddress, int grpcPort, string thumbprint)
        {
            Id = id;
            IpAddress = ipAddress;
            GrpcPort = grpcPort;
            Thumbprint = thumbprint;
            LastSeenUtc = DateTime.UtcNow;
        }

        public IPEndPoint GrpcEndpoint => new(IpAddress, GrpcPort);

        public override string ToString()
        {
            return GrpcEndpoint.ToString();
        }

        public bool Equals(Peer? other)
        {
            if (other is null)
            {
                return false;
            }

            return Id.Equals(other.Id);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public override bool Equals(object? obj)
        {
            return obj is Peer other && Equals(other);
        }

        public static bool operator ==(Peer? left, Peer? right)
        {
            if (left is null)
            {
                return right is null;
            }
            return left.Equals(right);
        }

        public static bool operator !=(Peer? left, Peer? right) => !(left == right);
    }
}
