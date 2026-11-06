using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ObservableCollections;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal interface ISimulatedWorldStateService : IDisposable
{
    IReadOnlyObservableList<SimulatedNodeSummary> Personas { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> Relays { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> Groups { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> ActiveSwarms { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> PinnedCards { get; }

    void RegisterPersona(SimulatedNodeSummary persona, ISimulatedPersonaHost? host = null);
    void RegisterRelay(SimulatedNodeSummary relay, ISimulatedRelayHost? host = null);
    void RegisterGroup(SimulatedNodeSummary group);
    void RegisterSwarm(SimulatedNodeSummary swarm);

    ISimulatedPersonaHost? GetPersonaHost(SimulatedNodeId id);
    ISimulatedPersonaHost? FindPersonaHostByIdentity(PublicIdentityId identityId);

    ISimulatedRelayHost? GetRelayHost(SimulatedNodeId id);
    ISimulatedRelayHost? FindRelayHostByName(string name);
    IReadOnlyCollection<ISimulatedRelayHost> GetAllRelayHosts();

    void TogglePinCard(SimulatedNodeSummary node);
    void UnpinCard(SimulatedNodeId nodeId);
    Task ClearAllAsync();
    void ClearAll();
}

internal sealed class SimulatedWorldStateService : ISimulatedWorldStateService
{
    private const int MaxPinnedCards = 4;

    private readonly ISelectionStateService? _selectionState;
    private readonly ObservableList<SimulatedNodeSummary> _personas = new();
    private readonly ObservableList<SimulatedNodeSummary> _relays = new();
    private readonly ObservableList<SimulatedNodeSummary> _groups = new();
    private readonly ObservableList<SimulatedNodeSummary> _swarms = new();
    private readonly ObservableList<SimulatedNodeSummary> _pinnedCards = new();
    private readonly ConcurrentDictionary<SimulatedNodeId, ISimulatedPersonaHost> _personaHosts = new();
    private readonly ConcurrentDictionary<SimulatedNodeId, ISimulatedRelayHost> _relayHosts = new();

    public IReadOnlyObservableList<SimulatedNodeSummary> Personas => _personas;
    public IReadOnlyObservableList<SimulatedNodeSummary> Relays => _relays;
    public IReadOnlyObservableList<SimulatedNodeSummary> Groups => _groups;
    public IReadOnlyObservableList<SimulatedNodeSummary> ActiveSwarms => _swarms;
    public IReadOnlyObservableList<SimulatedNodeSummary> PinnedCards => _pinnedCards;

    public SimulatedWorldStateService(ISelectionStateService? selectionState = null)
    {
        _selectionState = selectionState;
    }

    public void RegisterPersona(SimulatedNodeSummary persona, ISimulatedPersonaHost? host = null)
    {
        if (host is not null)
        {
            _personaHosts[persona.Id] = host;
        }
        UiTaskRunner.PostToUi(() => _personas.Add(persona));
    }

    public void RegisterRelay(SimulatedNodeSummary relay, ISimulatedRelayHost? host = null)
    {
        if (host is not null)
        {
            _relayHosts[relay.Id] = host;
        }
        UiTaskRunner.PostToUi(() => _relays.Add(relay));
    }

    public void RegisterGroup(SimulatedNodeSummary group)
    {
        UiTaskRunner.PostToUi(() => _groups.Add(group));
    }

    public void RegisterSwarm(SimulatedNodeSummary swarm)
    {
        UiTaskRunner.PostToUi(() => _swarms.Add(swarm));
    }

    public ISimulatedPersonaHost? GetPersonaHost(SimulatedNodeId id)
    {
        _personaHosts.TryGetValue(id, out var host);
        return host;
    }

    public ISimulatedPersonaHost? FindPersonaHostByIdentity(PublicIdentityId identityId)
    {
        return _personaHosts.Values.FirstOrDefault(h => h.PublicIdentityId == identityId);
    }

    public ISimulatedRelayHost? GetRelayHost(SimulatedNodeId id)
    {
        _relayHosts.TryGetValue(id, out var host);
        return host;
    }

    public ISimulatedRelayHost? FindRelayHostByName(string name)
    {
        return _relayHosts.Values.FirstOrDefault(h => string.Equals(h.DisplayName, name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyCollection<ISimulatedRelayHost> GetAllRelayHosts()
    {
        return _relayHosts.Values.ToArray();
    }

    public void TogglePinCard(SimulatedNodeSummary node)
    {
        UiTaskRunner.PostToUi(() =>
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
        });
    }

    public void UnpinCard(SimulatedNodeId nodeId)
    {
        UiTaskRunner.PostToUi(() =>
        {
            var existing = _pinnedCards.FirstOrDefault(x => x.Id == nodeId);
            if (existing is not null)
            {
                _pinnedCards.Remove(existing);
            }
        });
    }

    public async Task ClearAllAsync()
    {
        var personaHosts = _personaHosts.Values.ToArray();
        _personaHosts.Clear();

        var relayHosts = _relayHosts.Values.ToArray();
        _relayHosts.Clear();

        UiTaskRunner.PostToUi(() =>
        {
            _personas.Clear();
            _relays.Clear();
            _groups.Clear();
            _swarms.Clear();
            _pinnedCards.Clear();
            _selectionState?.SetPrimary(null);
            _selectionState?.SetSecondary(null);
            _selectionState?.ClearMultiSelection();
        });

        await Task.Run(async () =>
        {
            foreach (var host in personaHosts)
            {
                try
                {
                    await host.DisposeAsync();
                }
                catch { }
            }

            foreach (var relay in relayHosts)
            {
                try
                {
                    await relay.DisposeAsync();
                }
                catch { }
            }
        });
    }

    public void ClearAll()
    {
        Task.Run(ClearAllAsync).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        ClearAll();
    }
}
