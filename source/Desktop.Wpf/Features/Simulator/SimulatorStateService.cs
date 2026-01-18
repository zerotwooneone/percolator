using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateService
{
    ReadOnlyObservableCollection<SimulatedPeerDto> Peers { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task AddPeerAsync(string? displayName, CancellationToken cancellationToken = default);
    Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default);
    Task ToggleOnlineAsync(Guid peerId, CancellationToken cancellationToken = default);
    Task ToggleRelayCapableAsync(Guid peerId, CancellationToken cancellationToken = default);
}

public sealed class SimulatorStateService : ISimulatorStateService
{
    private readonly ISimulatorStateStore _store;

    private readonly ObservableCollection<SimulatedPeerDto> _peers = new();
    public ReadOnlyObservableCollection<SimulatedPeerDto> Peers { get; }

    private SimulatorStateDto _state = new();

    public SimulatorStateService(ISimulatorStateStore store)
    {
        _store = store;
        Peers = new ReadOnlyObservableCollection<SimulatedPeerDto>(_peers);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _state = loaded ?? new SimulatorStateDto { Version = 1 };

        _peers.Clear();
        foreach (var p in _state.Peers)
        {
            NormalizePeer(p);
            _peers.Add(p);
        }

        if (loaded is null)
        {
            await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddPeerAsync(string? displayName, CancellationToken cancellationToken = default)
    {
        var peer = new SimulatedPeerDto
        {
            PeerId = Guid.NewGuid(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            IsOnline = true,
            Connection = new SimulatedPeerConnectionDto { Mode = ConnectionMode.Direct },
            Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false }
        };
        NormalizePeer(peer);

        _state.Peers.Add(peer);
        _peers.Add(peer);
        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        _state.Peers.Remove(peer);

        var inUi = _peers.FirstOrDefault(p => p.PeerId == peerId);
        if (inUi is not null) _peers.Remove(inUi);

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    public async Task ToggleOnlineAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.IsOnline = !peer.IsOnline;
        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    public async Task ToggleRelayCapableAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.Relay.IsRelayCapable = !peer.Relay.IsRelayCapable;
        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    private static void NormalizePeer(SimulatedPeerDto peer)
    {
        peer.Connection ??= new SimulatedPeerConnectionDto();
        peer.KnownPeerIds ??= new();
        peer.PreKeys ??= new();
        peer.PreKeys.OneTimePreKeys ??= new();
        peer.Relay ??= new();
        peer.Relay.OpaqueQueue ??= new();
        peer.Relay.OpaqueQueue.Items ??= new();
        peer.Relay.PreKeyStore ??= new();
        peer.Relay.PreKeyStore.PublishedBundles ??= new();
    }
}
