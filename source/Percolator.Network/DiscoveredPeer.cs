using Percolator.Network.ValueObjects;

namespace Percolator.Network;

/// <summary>
/// Authoritative provisional peer discovered on the network before identity binding.
/// </summary>
public class DiscoveredPeer
{
    public DiscoveryKey DiscoveryKey { get; }
    public PublicKeyHash? IdentityPublicKeyHash { get; private set; }
    public DateTimeOffset FirstSeenUtc { get; private set; }
    public DateTimeOffset LastSeenUtc { get; private set; }
    public DiscoverySource Source { get; private set; }
    public double Confidence { get; private set; }

    public List<GrpcEndPoint> Endpoints { get; } = new();

    // Promotion state
    public bool IsPromoted { get; private set; }
    public PeerId? BoundPeerId { get; private set; }
    public DateTimeOffset? PromotedAtUtc { get; private set; }

    private DiscoveredPeer(DiscoveryKey key, PublicKeyHash? identityPublicKeyHash, DateTimeOffset now)
    {
        DiscoveryKey = key;
        IdentityPublicKeyHash = identityPublicKeyHash;
        FirstSeenUtc = now;
        LastSeenUtc = now;
        Source = DiscoverySource.Cache;
        Confidence = 0.0;
    }

    public static DiscoveredPeer Create(DiscoveryKey key, PublicKeyHash? identityPublicKeyHash, DateTimeOffset now)
        => new DiscoveredPeer(key, identityPublicKeyHash, now);

    public void RecordDiscovery(DiscoverySource source, DateTimeOffset now)
    {
        Source = source;
        if (now > LastSeenUtc)
        {
            LastSeenUtc = now;
        }
        // simple confidence bump for now
        Confidence = Math.Min(1.0, Confidence + 0.1);
    }

    public void ObserveEndpoint(GrpcEndPoint endpoint, DateTimeOffset now)
    {
        var idx = Endpoints.FindIndex(e => e.EndPoint.Host == endpoint.EndPoint.Host && e.EndPoint.Port == endpoint.EndPoint.Port);
        var observed = new GrpcEndPoint(endpoint.EndPoint, now);
        if (idx < 0)
        {
            Endpoints.Add(observed);
        }
        else
        {
            var existing = Endpoints[idx];
            var newer = now > existing.LastSeen ? observed : existing;
            Endpoints[idx] = newer;
        }

        if (now > LastSeenUtc)
        {
            LastSeenUtc = now;
        }
    }

    public PeerRoutingProfile PromoteToRoutingProfile(PeerId id, DateTimeOffset promotedAt)
    {
        var prp = new PeerRoutingProfile();
        prp.BindIdentity(id);
        foreach (var ep in Endpoints)
        {
            prp.AddGrpcEndPoint(ep, ep.LastSeen);
        }

        IsPromoted = true;
        BoundPeerId = id;
        PromotedAtUtc = promotedAt;
        return prp;
    }
}
