using Percolator.Network.ValueObjects;

namespace Percolator.Network;

public sealed class PeerRoutingProfile
{
    public NetworkPeerId? Id { get; private set; }
    public List<GrpcEndPoint> Endpoints { get; } = new();
    public Reachability Reachability { get; private set; } = new();
    public List<RelayLink> Relays { get; } = new();
    public IdentityPublicKey? IdentityPublicKey { get; private set; }

    public PeerRoutingProfile()
    {
    }

    public void AddGrpcEndPoint(GrpcEndPoint ep, DateTimeOffset now)
    {
        var existingIndex = Endpoints.FindIndex(e => e.EndPoint.Host == ep.EndPoint.Host && e.EndPoint.Port == ep.EndPoint.Port);
        if (existingIndex < 0)
        {
            Endpoints.Add(ep);
        }
        else
        {
            var existing = Endpoints[existingIndex];
            var newer = ep.LastSeen > existing.LastSeen ? ep : existing;
            Endpoints[existingIndex] = newer;
        }
    }

    public void UpdateLastSeen(GrpcEndPoint ep, DateTimeOffset now)
    {
        var idx = Endpoints.FindIndex(e => e.EndPoint.Host == ep.EndPoint.Host && e.EndPoint.Port == ep.EndPoint.Port);
        if (idx >= 0)
        {
            var updated = Endpoints[idx] with { LastSeen = now };
            Endpoints[idx] = updated;
        }
    }

    public void RecordReachability(ReachabilityStatus status, DateTimeOffset now)
    {
        Reachability.TransitionTo(status, now);
    }

    public void BindIdentity(NetworkPeerId id)
    {
        if (Id is null)
        {
            Id = id;
        }
    }

    public void SetIdentityPublicKey(IdentityPublicKey key)
    {
        IdentityPublicKey = key;
    }

    public void MergeDiscovered(DiscoveredPeer provisional)
    {
        foreach (var ep in provisional.Endpoints)
        {
            AddGrpcEndPoint(ep, ep.LastSeen);
        }
    }

    public void AddOrRefreshRelay(NetworkPeerId relayNetworkPeerId, DateTimeOffset now)
    {
        var idx = Relays.FindIndex(r => r.RelayNetworkPeerId == relayNetworkPeerId);
        if (idx < 0)
        {
            Relays.Add(new RelayLink(relayNetworkPeerId, new EndpointFreshness(now)));
        }
        else
        {
            Relays[idx].Refresh(now);
        }
    }

    public void RemoveRelay(NetworkPeerId relayNetworkPeerId)
    {
        Relays.RemoveAll(r => r.RelayNetworkPeerId == relayNetworkPeerId);
    }

    public void PruneStaleRelays(DateTimeOffset cutoff)
    {
        Relays.RemoveAll(r => r.Freshness.LastSeenUtc <= cutoff);
    }
}
