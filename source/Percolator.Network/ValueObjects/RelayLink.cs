namespace Percolator.Network.ValueObjects;

public sealed class RelayLink
{
    public NetworkPeerId RelayNetworkPeerId { get; }
    public EndpointFreshness Freshness { get; }

    public RelayLink(NetworkPeerId relayNetworkPeerId, EndpointFreshness freshness)
    {
        RelayNetworkPeerId = relayNetworkPeerId;
        Freshness = freshness;
    }

    public void Refresh(DateTimeOffset now)
    {
        Freshness.Observe(now);
    }
}
