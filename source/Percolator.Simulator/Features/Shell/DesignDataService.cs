using System;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Shell;

internal sealed class DesignDataService
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISelectionStateService _selectionState;

    public DesignDataService(
        ISimulatedWorldStateService worldState,
        ISelectionStateService selectionState)
    {
        _worldState = worldState;
        _selectionState = selectionState;
    }

    public void SeedPreviewData()
    {
        var crimson = new SimulatedNodeSummary(
            SimulatedNodeId.CreateNew(),
            "Crimson Falcon",
            SimulatedNodeType.Persona,
            "127.0.0.1:5200 (Direct Loopback)",
            IsPinned: true);

        var otter = new SimulatedNodeSummary(
            SimulatedNodeId.CreateNew(),
            "Silent Otter",
            SimulatedNodeType.Persona,
            "127.0.0.1:5201 (Direct Loopback)",
            IsPinned: true);

        var relay = new SimulatedNodeSummary(
            SimulatedNodeId.CreateNew(),
            "Relay-Alpha",
            SimulatedNodeType.Relay,
            "127.0.0.1:5202 (Mailbox & ZK Fanout)",
            IsPinned: true);

        _worldState.RegisterPersona(crimson);
        _worldState.RegisterPersona(otter);
        _worldState.RegisterRelay(relay);

        _worldState.TogglePinCard(crimson);
        _worldState.TogglePinCard(otter);
        _worldState.TogglePinCard(relay);

        _selectionState.SetPrimary(crimson);
        _selectionState.SetSecondary(otter);
    }

    public void ClearPreviewData()
    {
        _worldState.ClearAll();
        _selectionState.SetPrimary(null);
        _selectionState.SetSecondary(null);
        _selectionState.ClearMultiSelection();
    }
}
