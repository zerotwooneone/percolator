using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateService
{
    ReadOnlyObservableCollection<SimulatedPeerDto> Peers { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<Guid> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default);
    Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default);
    Task ToggleOnlineAsync(Guid peerId, CancellationToken cancellationToken = default);
    Task ToggleRelayCapableAsync(Guid peerId, CancellationToken cancellationToken = default);

    Task UpdateDisplayNameAsync(Guid peerId, string? displayName, CancellationToken cancellationToken = default);
    Task SetOnlineAsync(Guid peerId, bool isOnline, CancellationToken cancellationToken = default);
    Task SetRelayCapableAsync(Guid peerId, bool isRelayCapable, CancellationToken cancellationToken = default);
}

public sealed class SimulatorStateService : ISimulatorStateService
{
    private readonly ISimulatorStateStore _store;
    private readonly ISimulatedPeerKeyFactory _keys;

    private readonly ObservableCollection<SimulatedPeerDto> _peers = new();
    public ReadOnlyObservableCollection<SimulatedPeerDto> Peers { get; }

    private SimulatorStateDto _state = new();

    public SimulatorStateService(ISimulatorStateStore store, ISimulatedPeerKeyFactory keys)
    {
        _store = store;
        _keys = keys;
        Peers = new ReadOnlyObservableCollection<SimulatedPeerDto>(_peers);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAsync(cancellationToken);
        _state = loaded ?? new SimulatorStateDto { Version = 1 };

        var changed = false;

        await InvokeOnUiAsync(() =>
        {
            _peers.Clear();
            foreach (var p in _state.Peers)
            {
                NormalizePeer(p);
                changed |= _keys.EnsureReverseSignalKeys(p.ReverseSignalKeys);
                _peers.Add(p);
            }
        });

        if (loaded is null)
        {
            await _store.SaveAsync(_state, cancellationToken);
        }
        else if (changed)
        {
            await _store.SaveAsync(_state, cancellationToken);
        }
    }

    public async Task<Guid> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default)
    {
        var peerId = Guid.NewGuid();
        var peer = new SimulatedPeerDto
        {
            PeerId = peerId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            IsOnline = true,
            Connection = new SimulatedPeerConnectionDto { Mode = ConnectionMode.Direct },
            Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false }
        };
        NormalizePeer(peer);
        _ = _keys.EnsureReverseSignalKeys(peer.ReverseSignalKeys);

        _state.Peers.Add(peer);
        await InvokeOnUiAsync(() => _peers.Add(peer));
        await _store.SaveAsync(_state, cancellationToken);
        return peerId;
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        _state.Peers.Remove(peer);

        await InvokeOnUiAsync(() =>
        {
            var inUi = _peers.FirstOrDefault(p => p.PeerId == peerId);
            if (inUi is not null) _peers.Remove(inUi);
        });

        await _store.SaveAsync(_state, cancellationToken);
    }

    public async Task ToggleOnlineAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.IsOnline = !peer.IsOnline;
        await _store.SaveAsync(_state, cancellationToken);
    }

    public async Task ToggleRelayCapableAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.Relay.IsRelayCapable = !peer.Relay.IsRelayCapable;
        await _store.SaveAsync(_state, cancellationToken);
    }

    public async Task UpdateDisplayNameAsync(Guid peerId, string? displayName, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        await _store.SaveAsync(_state, cancellationToken);
    }

    public async Task SetOnlineAsync(Guid peerId, bool isOnline, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.IsOnline = isOnline;
        await _store.SaveAsync(_state, cancellationToken);
    }

    public async Task SetRelayCapableAsync(Guid peerId, bool isRelayCapable, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.Relay.IsRelayCapable = isRelayCapable;
        await _store.SaveAsync(_state, cancellationToken);
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
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
        peer.ReverseSignalKeys ??= new();
    }

    
}
