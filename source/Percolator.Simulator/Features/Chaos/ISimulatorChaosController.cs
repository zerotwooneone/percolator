using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Chaos;

public enum ChaosFaultAction
{
    Pass = 0,
    DropEnvelope = 1,
    SimulateTransientError = 2,
    InjectArtificialDelay = 3
}

internal interface ISimulatorChaosController
{
    ChaosFaultAction EvaluateDeliveryFault(SimulatedNodeId nodeId);
    void SetFaultAction(SimulatedNodeId nodeId, ChaosFaultAction action);
    void ResetFaults(SimulatedNodeId nodeId);
}
