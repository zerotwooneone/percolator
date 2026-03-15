using Percolator.Network.ValueObjects;

namespace Percolator.Network;

public sealed class SimpleRoutePlanner : IProfileRoutePlanner
{
    public RouteSelection SelectRoute(PeerRoutingProfile profile)
    {
        // Default policies: use a 1-day prune cutoff; reachability policy default
        var freshness = new Percolator.Network.ValueObjects.FreshnessPolicy(TimeSpan.FromHours(12), TimeSpan.FromDays(1));
        var reachability = new Percolator.Network.ValueObjects.ReachabilityPolicy();
        return SelectRoute(profile, freshness, reachability);
    }

    public RouteSelection SelectRoute(PeerRoutingProfile profile, Percolator.Network.ValueObjects.FreshnessPolicy freshness, Percolator.Network.ValueObjects.ReachabilityPolicy reachability)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        var now = DateTimeOffset.UtcNow;
        var veryStaleCutoff = freshness.PruneAfter;

        var endpointsEligible = Enumerable.Empty<GrpcEndPoint>();
        if (profile.Reachability.Status != ReachabilityStatus.Offline)
        {
            endpointsEligible = profile.Endpoints
                .Where(e => (now - e.LastSeen) <= veryStaleCutoff);
        }

        if (endpointsEligible.Any())
        {
            var ep = endpointsEligible
                .OrderByDescending(e => e.LastSeen)
                .ThenBy(e => e.EndPoint.Host)
                .ThenBy(e => e.EndPoint.Port)
                .First();
            return new RouteSelection(ep, null);
        }

        if (profile.Relays.Count > 0)
        {
            var relay = profile.Relays
                .OrderByDescending(r => r.Freshness.LastSeenUtc)
                .ThenBy(r => r.RelayPeerId.Value.ToString(), StringComparer.Ordinal)
                .First();
            return new RouteSelection(default, relay);
        }

        throw new InvalidOperationException("No endpoints or relays available to route.");
    }
}

public readonly record struct RouteSelection(GrpcEndPoint Endpoint, RelayLink? Relay);
