using System.Diagnostics;

namespace Percolator.Network.Messaging;

public sealed class DefaultRoutePlanner : IRoutePlanner
{
    private readonly IPeerConnectionRepository _peerConnections;
    private readonly IRelayTopology _relayTopology;

    public DefaultRoutePlanner(IPeerConnectionRepository peerConnections, IRelayTopology relayTopology)
    {
        _peerConnections = peerConnections;
        _relayTopology = relayTopology;
    }

    public async Task<IReadOnlyList<string>> PlanAsync(PeerId target, SendStrategy strategy, CancellationToken ct = default)
    {
        var plan = new List<string>(capacity: 2);

        // Direct route available?
        try
        {
            var conn = await _peerConnections.GetByIdAsync(target).ConfigureAwait(false);
            if (conn?.GrpcEndPoints.Count > 0)
            {
                plan.Add("Direct");
            }
        }
        catch
        {
            // Swallow and let executor surface reasons during execution
        }

        if (strategy == SendStrategy.DirectThenRelay)
        {
            var relay = await _relayTopology.GetRelayForAsync(target, ct).ConfigureAwait(false);
            if (relay is not null)
            {
                plan.Add($"Relay:{relay.Value}");
            }
        }

        return plan;
    }
}
