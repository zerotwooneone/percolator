using System;
using System.Linq;
using ObservableCollections;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal interface ISimulatedWorldStateService : IDisposable
{
    IReadOnlyObservableList<SimulatedNodeSummary> Personas { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> Relays { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> Groups { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> ActiveSwarms { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> PinnedCards { get; }

    void RegisterPersona(SimulatedNodeSummary persona);
    void RegisterRelay(SimulatedNodeSummary relay);
    void RegisterGroup(SimulatedNodeSummary group);
    void RegisterSwarm(SimulatedNodeSummary swarm);

    void TogglePinCard(SimulatedNodeSummary node);
    void UnpinCard(SimulatedNodeId nodeId);
    void ClearAll();
}

internal sealed class SimulatedWorldStateService : ISimulatedWorldStateService
{
    private const int MaxPinnedCards = 4;

    private readonly ObservableList<SimulatedNodeSummary> _personas = new();
    private readonly ObservableList<SimulatedNodeSummary> _relays = new();
    private readonly ObservableList<SimulatedNodeSummary> _groups = new();
    private readonly ObservableList<SimulatedNodeSummary> _swarms = new();
    private readonly ObservableList<SimulatedNodeSummary> _pinnedCards = new();

    public IReadOnlyObservableList<SimulatedNodeSummary> Personas => _personas;
    public IReadOnlyObservableList<SimulatedNodeSummary> Relays => _relays;
    public IReadOnlyObservableList<SimulatedNodeSummary> Groups => _groups;
    public IReadOnlyObservableList<SimulatedNodeSummary> ActiveSwarms => _swarms;
    public IReadOnlyObservableList<SimulatedNodeSummary> PinnedCards => _pinnedCards;

    public void RegisterPersona(SimulatedNodeSummary persona)
    {
        _personas.Add(persona);
    }

    public void RegisterRelay(SimulatedNodeSummary relay)
    {
        _relays.Add(relay);
    }

    public void RegisterGroup(SimulatedNodeSummary group)
    {
        _groups.Add(group);
    }

    public void RegisterSwarm(SimulatedNodeSummary swarm)
    {
        _swarms.Add(swarm);
    }

    public void TogglePinCard(SimulatedNodeSummary node)
    {
        var existing = _pinnedCards.FirstOrDefault(x => x.Id == node.Id);
        if (existing is not null)
        {
            _pinnedCards.Remove(existing);
            return;
        }

        if (_pinnedCards.Count >= MaxPinnedCards)
        {
            _pinnedCards.RemoveAt(0); // FIFO eviction
        }

        _pinnedCards.Add(node with { IsPinned = true });
    }

    public void UnpinCard(SimulatedNodeId nodeId)
    {
        var existing = _pinnedCards.FirstOrDefault(x => x.Id == nodeId);
        if (existing is not null)
        {
            _pinnedCards.Remove(existing);
        }
    }

    public void ClearAll()
    {
        _personas.Clear();
        _relays.Clear();
        _groups.Clear();
        _swarms.Clear();
        _pinnedCards.Clear();
    }

    public void Dispose()
    {
        ClearAll();
    }
}
