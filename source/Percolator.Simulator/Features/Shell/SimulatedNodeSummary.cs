using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Shell;

internal enum SimulatedNodeType
{
    Persona,
    Relay,
    Group,
    Swarm
}

internal sealed record SimulatedNodeSummary(
    SimulatedNodeId Id,
    string DisplayName,
    SimulatedNodeType NodeType,
    string Subtitle,
    bool IsPinned = false);
