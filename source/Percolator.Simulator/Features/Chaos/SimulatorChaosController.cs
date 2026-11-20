using System;
using System.Collections.Concurrent;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Chaos;

internal sealed class SimulatorChaosController : ISimulatorChaosController
{
    private readonly ConcurrentDictionary<SimulatedNodeId, ChaosFaultAction> _faults = new();

    public ChaosFaultAction EvaluateDeliveryFault(SimulatedNodeId nodeId)
    {
        return _faults.TryGetValue(nodeId, out var action) ? action : ChaosFaultAction.Pass;
    }

    public void SetFaultAction(SimulatedNodeId nodeId, ChaosFaultAction action)
    {
        _faults[nodeId] = action;
    }

    public void ResetFaults(SimulatedNodeId nodeId)
    {
        _faults.TryRemove(nodeId, out _);
    }
}
