using System;
using System.Net;

namespace Percolator.Network
{
    public class Peer : IEquatable<Peer>
    {
        public IPAddress IpAddress { get; }
        public int GrpcPort { get; }
        public DateTime LastSeenUtc { get; set; }

        public Peer(IPAddress ipAddress, int grpcPort)
        {
            IpAddress = ipAddress;
            GrpcPort = grpcPort;
            LastSeenUtc = DateTime.UtcNow;
        }

        public IPEndPoint GrpcEndpoint => new(IpAddress, GrpcPort);

        public bool Equals(Peer? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return GrpcEndpoint.Equals(other.GrpcEndpoint);
        }

        public override bool Equals(object? obj)
        {
            return obj is Peer other && Equals(other);
        }

        public override int GetHashCode()
        {
            return GrpcEndpoint.GetHashCode();
        }

        public override string ToString()
        {
            return GrpcEndpoint.ToString();
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
