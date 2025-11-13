using System;
using System.Linq;
using Percolator.Network.ValueObjects;

namespace Percolator.Network.Services;

public interface IRoutePlanner
{
    SelectedRoute SelectRoute(
        PeerRoutingProfile profile,
        RelayPolicy relayPolicy,
        FreshnessPolicy freshnessPolicy,
        ReachabilityPolicy reachabilityPolicy,
        BackoffState? backoff = null,
        RetryBudget? budget = null);
}

public sealed class RoutePlanner : IRoutePlanner
{
    public SelectedRoute SelectRoute(
        PeerRoutingProfile profile,
        RelayPolicy relayPolicy,
        FreshnessPolicy freshnessPolicy,
        ReachabilityPolicy reachabilityPolicy,
        BackoffState? backoff = null,
        RetryBudget? budget = null)
    {
        // Prefer freshest direct endpoint
        var direct = profile.Endpoints
            .OrderByDescending(e => e.LastSeen)
            .FirstOrDefault();

        if (direct is not null)
        {
            return new SelectedRoute(direct, null);
        }

        // Otherwise, prefer freshest relay
        var relay = profile.Relays
            .OrderByDescending(r => r.Freshness.LastSeenUtc)
            .FirstOrDefault();

        if (relay is not null)
        {
            return new SelectedRoute(null, relay);
        }

        return new SelectedRoute(null, null);
    }
}
