using Percolator.Network;

namespace Percolator.Network.ValueObjects;

public sealed class SelectedRoute
{
    public GrpcEndPoint? SelectedEndpoint { get; }
    public RelayLink? SelectedRelay { get; }

    public SelectedRoute(GrpcEndPoint? selectedEndpoint, RelayLink? selectedRelay)
    {
        SelectedEndpoint = selectedEndpoint;
        SelectedRelay = selectedRelay;
    }
}
