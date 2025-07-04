using System.Net;

namespace Percolator.Network;

/// <summary>
/// Represents a discovered peer on the network.
/// </summary>
public class Peer
{
    /// <summary>
    /// A unique identifier for the peer session.
    /// </summary>
    public PeerId Id { get; }

    /// <summary>
    /// The IP address where the peer's gRPC service is listening.
    /// </summary>
    public IPEndPoint GrpcEndpoint { get; }

    /// <summary>
    /// The unique, stable identifier for the peer, derived from its public key.
    /// </summary>
    public PublicKeyHash PublicKeyHash { get; }

    /// <summary>
    /// The last time a broadcast was received from this peer.
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    public Peer(PeerId id, IPAddress ipAddress, int port, PublicKeyHash publicKeyHash)
    {
        Id = id;
        GrpcEndpoint = new IPEndPoint(ipAddress, port);
        PublicKeyHash = publicKeyHash;
        LastSeenUtc = DateTime.UtcNow;
    }

    public override string ToString()
    {
        return GrpcEndpoint.ToString();
    }

    public override bool Equals(object? obj)
    {
        return obj is Peer other && PublicKeyHash.Equals(other.PublicKeyHash);
    }

    public override int GetHashCode()
    {
        return PublicKeyHash.GetHashCode();
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
