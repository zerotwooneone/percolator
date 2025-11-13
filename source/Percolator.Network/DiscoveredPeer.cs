using System.Net;
using Percolator.Network.ValueObjects;

namespace Percolator.Network;

/// <summary>
/// Represents a discovered peer on the network.
/// </summary>
public class DiscoveredPeer
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

    public DiscoveredPeer(PeerId id, IPAddress ipAddress, int port, PublicKeyHash publicKeyHash)
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
        return obj is DiscoveredPeer other && PublicKeyHash.Equals(other.PublicKeyHash);
    }

    public override int GetHashCode()
    {
        return PublicKeyHash.GetHashCode();
    }

    public static bool operator ==(DiscoveredPeer? left, DiscoveredPeer? right)
    {
        if (left is null)
        {
            return right is null;
        }
        return left.Equals(right);
    }

    public static bool operator !=(DiscoveredPeer? left, DiscoveredPeer? right) => !(left == right);

    public void RecordDiscovery(DiscoverySource source, DateTimeOffset now)
    {
        if (now.UtcDateTime > LastSeenUtc)
        {
            LastSeenUtc = now.UtcDateTime;
        }
    }

    public void ObserveEndpoint(GrpcEndPoint endpoint, DateTimeOffset now)
    {
        if (now.UtcDateTime > LastSeenUtc)
        {
            LastSeenUtc = now.UtcDateTime;
        }
    }
}
