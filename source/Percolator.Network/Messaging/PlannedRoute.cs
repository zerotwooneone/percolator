namespace Percolator.Network.Messaging;

public abstract record PlannedRoute
{
    private PlannedRoute() { }

    public sealed record Direct : PlannedRoute;
    public sealed record Relay(PeerId RelayHostPeerId) : PlannedRoute;
}
