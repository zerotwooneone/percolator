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

    Task EnqueueRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelayQueuedBlobDto>> DequeueRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, int max, CancellationToken cancellationToken = default);
    Task<bool> DeleteRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);
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

    public async Task EnqueueRelayOpaqueAsync(
        Guid relayHostPeerId,
        byte[] recipientRoutingKey,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default)
    {
        if (recipientRoutingKey is null) throw new ArgumentNullException(nameof(recipientRoutingKey));
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return;

        peer.Relay.OpaqueQueue.Items.Add(new RelayQueuedBlobDto
        {
            AckId = Guid.NewGuid(),
            RecipientRoutingKey = recipientRoutingKey,
            OpaqueBytes = opaqueBytes,
            EnqueuedUtc = DateTimeOffset.UtcNow,
            DebugType = debugType
        });

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelayQueuedBlobDto>> DequeueRelayOpaqueAsync(
        Guid relayHostPeerId,
        byte[] recipientRoutingKey,
        int max,
        CancellationToken cancellationToken = default)
    {
        if (recipientRoutingKey is null) throw new ArgumentNullException(nameof(recipientRoutingKey));
        if (max <= 0) return Array.Empty<RelayQueuedBlobDto>();

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return Array.Empty<RelayQueuedBlobDto>();

        var matches = peer.Relay.OpaqueQueue.Items
            .Where(i => i.RecipientRoutingKey.SequenceEqual(recipientRoutingKey))
            .OrderBy(i => i.EnqueuedUtc)
            .Take(max)
            .ToList();

        if (matches.Count == 0) return Array.Empty<RelayQueuedBlobDto>();

        foreach (var item in matches)
        {
            peer.Relay.OpaqueQueue.Items.Remove(item);
        }

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
        return matches;
    }

    public async Task<bool> DeleteRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return false;

        var before = peer.Relay.OpaqueQueue.Items.Count;
        peer.Relay.OpaqueQueue.Items.RemoveAll(i => i.AckId == ackId);
        var changed = peer.Relay.OpaqueQueue.Items.Count != before;

        if (changed)
        {
            await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
        }

        return changed;
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
