using System;
using System.Linq;
using System.Net;
using Percolator.Network.ValueObjects;

namespace Percolator.Network;

public sealed class SimpleRoutePlanner
{
    public RouteSelection SelectRoute(PeerRoutingProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        var now = DateTimeOffset.UtcNow;
        var veryStaleCutoff = TimeSpan.FromDays(1);

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
