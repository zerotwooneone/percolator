using Percolator.Network.ValueObjects;

namespace Percolator.Network;

public interface IProfileRoutePlanner
{
    RouteSelection SelectRoute(PeerRoutingProfile profile);
    RouteSelection SelectRoute(PeerRoutingProfile profile, FreshnessPolicy freshness, ReachabilityPolicy reachability);
}
