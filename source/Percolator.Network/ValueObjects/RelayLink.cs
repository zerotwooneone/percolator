using System;
using Percolator.Network;

namespace Percolator.Network.ValueObjects;

public sealed class RelayLink
{
    public PeerId RelayPeerId { get; }
    public EndpointFreshness Freshness { get; }

    public RelayLink(PeerId relayPeerId, EndpointFreshness freshness)
    {
        RelayPeerId = relayPeerId;
        Freshness = freshness;
    }

    public void Refresh(DateTimeOffset now)
    {
        Freshness.Observe(now);
    }
}
