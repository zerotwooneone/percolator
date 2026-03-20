using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;

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

    Task SetRuntimeStateAsync(Guid peerId, SimulatorPeerRuntimeState runtimeState, CancellationToken cancellationToken = default);

    Task<Guid?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default);

    Task EnqueueRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default);
    Task<RelayQueuedBlobDto?> PeekRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelayQueuedBlobDto>> DequeueRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, int max, CancellationToken cancellationToken = default);
    Task<bool> DeleteRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task<bool> MoveRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default);
    Task<bool> CorruptRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task PublishPreKeyBundleAsync(
        Guid relayHostPeerId,
        byte[] recipientPublicKeyHash,
        Guid logicalOwnerPeerId,
        byte[] bundleBytes,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default);

    Task<PublishedPreKeyBundleDto?> TryPopPreKeyBundleByRecipientPkhAsync(
        Guid relayHostPeerId,
        byte[] recipientPublicKeyHash,
        CancellationToken cancellationToken = default);

    Task<SimulatedPeerRuntimeStoreDto?> TryGetRuntimeStoreAsync(Guid peerId, CancellationToken cancellationToken = default);
    Task SaveRuntimeStoreAsync(Guid peerId, SimulatedPeerRuntimeStoreDto store, CancellationToken cancellationToken = default);

    Task AddPublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default);
    Task RemovePublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default);

    Task AddRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default);
    Task RemoveRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default);
}

public sealed class SimulatorStateService : ISimulatorStateService
{
    private const int SelfIdentityIdBase = 99000;

    private readonly ISimulatorStateStore _store;
    private readonly ISimulatedPeerKeyFactory _keys;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly ISimulatorDiagnosticsService _diagnostics;

    private readonly ObservableCollection<SimulatedPeerDto> _peers = new();
    public ReadOnlyObservableCollection<SimulatedPeerDto> Peers { get; }

    private SimulatorStateDto _state = new();

    private readonly object _initGate = new();
    private Task? _initializeTask;

    private int _nextSelfIdentityId = SelfIdentityIdBase - 1;

    public SimulatorStateService(
        ISimulatorStateStore store,
        ISimulatedPeerKeyFactory keys,
        IOptions<TransportOptions> transportOptions,
        ISimulatorDiagnosticsService diagnostics)
    {
        _store = store;
        _keys = keys;
        _transportOptions = transportOptions;
        _diagnostics = diagnostics;
        Peers = new ReadOnlyObservableCollection<SimulatedPeerDto>(_peers);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task? inFlight;
        lock (_initGate)
        {
            inFlight = _initializeTask;
            if (inFlight is null || inFlight.IsCompleted)
            {
                _initializeTask = InitializeCoreAsync(cancellationToken);
                inFlight = _initializeTask;
            }
        }

        await inFlight.ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken);
            _state = loaded ?? new SimulatorStateDto { Version = 1 };

            var changed = false;

            // Seed allocator from the maximum existing (valid) SelfIdentityId.
            foreach (var p in _state.Peers)
            {
                if (p.SelfIdentityId >= SelfIdentityIdBase && p.SelfIdentityId > _nextSelfIdentityId)
                {
                    _nextSelfIdentityId = p.SelfIdentityId;
                }
            }

            await InvokeOnUiAsync(() =>
            {
                _peers.Clear();
                foreach (var p in _state.Peers)
                {
                    if (p.SelfIdentityId < SelfIdentityIdBase)
                    {
                        p.SelfIdentityId = Interlocked.Increment(ref _nextSelfIdentityId);
                        changed = true;
                    }

                    NormalizePeer(p, _transportOptions.Value);
                    changed |= _keys.EnsureReverseSignalKeys(p.ReverseSignalKeys);
                    changed |= EnsureIdentityPublicKeyHash(p);
                    if (p.PublishedKeysToPeerIds is null)
                    {
                        p.PublishedKeysToPeerIds = new();
                        changed = true;
                    }
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
        finally
        {
            lock (_initGate)
            {
                _initializeTask = null;
            }
        }
    }

    public async Task<Guid> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default)
    {
        var peerId = Guid.NewGuid();
        var peer = new SimulatedPeerDto
        {
            PeerId = peerId,
            SelfIdentityId = Interlocked.Increment(ref _nextSelfIdentityId),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            IsOnline = true,
            Connection = new SimulatedPeerConnectionDto { Mode = ConnectionMode.Direct },
            Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false }
        };
        NormalizePeer(peer, _transportOptions.Value);
        _ = _keys.EnsureReverseSignalKeys(peer.ReverseSignalKeys);
        _ = EnsureIdentityPublicKeyHash(peer);

        _state.Peers.Add(peer);
        await InvokeOnUiAsync(() => _peers.Add(peer));
        await _store.SaveAsync(_state, cancellationToken);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerCreated,
            $"Peer created: {(string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.PeerId.ToString()[..8] : peer.DisplayName)}",
            peerId: peerId);

        return peerId;
    }

    public Task<Guid?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
        if (recipientPublicKeyHash.Length == 0) return Task.FromResult<Guid?>(null);

        var matches = _state.Peers
            .Where(p => p.IdentityPublicKeyHash is not null && p.IdentityPublicKeyHash.Length != 0)
            .Where(p => p.IdentityPublicKeyHash.SequenceEqual(recipientPublicKeyHash))
            .Select(p => p.PeerId)
            .Take(2)
            .ToList();

        if (matches.Count != 1) return Task.FromResult<Guid?>(null);
        return Task.FromResult<Guid?>(matches[0]);
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        var name = string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.PeerId.ToString()[..8] : peer.DisplayName;

        _state.Peers.Remove(peer);

        foreach (var p in _state.Peers)
        {
            if (p.PublishedKeysToPeerIds.Remove(peerId))
            {
                // best-effort cleanup
            }
        }

        await InvokeOnUiAsync(() =>
        {
            var inUi = _peers.FirstOrDefault(p => p.PeerId == peerId);
            if (inUi is not null) _peers.Remove(inUi);
        });

        await _store.SaveAsync(_state, cancellationToken);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerRemoved,
            $"Peer removed: {name}",
            peerId: peerId);
    }

    public async Task SetRuntimeStateAsync(Guid peerId, SimulatorPeerRuntimeState runtimeState, CancellationToken cancellationToken = default)
    {
        if (runtimeState is null) throw new ArgumentNullException(nameof(runtimeState));
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        peer.RuntimeState = runtimeState;
        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default)
    {
        if (publisherPeerId == hostPeerId) return;

        var publisher = _state.Peers.FirstOrDefault(p => p.PeerId == publisherPeerId);
        if (publisher is null) return;

        publisher.PublishedKeysToPeerIds ??= new();

        var added = false;
        await InvokeOnUiAsync(() =>
        {
            if (!publisher.PublishedKeysToPeerIds.Contains(hostPeerId))
            {
                publisher.PublishedKeysToPeerIds.Add(hostPeerId);
                added = true;
            }
        }).ConfigureAwait(false);

        if (!added) return;

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyPublishRelationshipAdded,
            $"Pre-keys relationship added: {publisherPeerId.ToString()[..8]} -> {hostPeerId.ToString()[..8]}",
            peerId: publisherPeerId);
    }

    public async Task RemovePublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default)
    {
        var publisher = _state.Peers.FirstOrDefault(p => p.PeerId == publisherPeerId);
        if (publisher is null) return;

        publisher.PublishedKeysToPeerIds ??= new();

        var removed = false;
        await InvokeOnUiAsync(() => removed = publisher.PublishedKeysToPeerIds.Remove(hostPeerId)).ConfigureAwait(false);
        if (!removed) return;

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PreKeyPublishRelationshipRemoved,
            $"Pre-keys relationship removed: {publisherPeerId.ToString()[..8]} -> {hostPeerId.ToString()[..8]}",
            peerId: publisherPeerId);
    }

    public async Task AddRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relayHostPeerId == peerId) return;

        var relayHost = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (relayHost is null) return;
        if (relayHost.Relay is null) return;
        if (!relayHost.Relay.IsRelayCapable) return;

        relayHost.Relay.ActiveSessionsPeerIds ??= new();

        var added = false;
        await InvokeOnUiAsync(() =>
        {
            if (!relayHost.Relay.ActiveSessionsPeerIds.Contains(peerId))
            {
                relayHost.Relay.ActiveSessionsPeerIds.Add(peerId);
                added = true;
            }
        }).ConfigureAwait(false);

        if (!added) return;

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayActiveSessionAdded,
            $"Relay active session added: relay={relayHostPeerId.ToString()[..8]} peer={peerId.ToString()[..8]}",
            peerId: peerId,
            relayHostPeerId: relayHostPeerId);
    }

    public async Task RemoveRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relayHost = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (relayHost is null) return;
        if (relayHost.Relay is null) return;
        if (relayHost.Relay.ActiveSessionsPeerIds is null) return;

        var removed = false;
        await InvokeOnUiAsync(() => removed = relayHost.Relay.ActiveSessionsPeerIds.Remove(peerId)).ConfigureAwait(false);
        if (!removed) return;

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayActiveSessionRemoved,
            $"Relay active session removed: relay={relayHostPeerId.ToString()[..8]} peer={peerId.ToString()[..8]}",
            peerId: peerId,
            relayHostPeerId: relayHostPeerId);
    }

    public async Task ToggleOnlineAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        await InvokeOnUiAsync(() => peer.IsOnline = !peer.IsOnline).ConfigureAwait(false);
        await _store.SaveAsync(_state, cancellationToken);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerOnlineChanged,
            $"Peer {(peer.IsOnline ? "online" : "offline")}: {(string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.PeerId.ToString()[..8] : peer.DisplayName)}",
            peerId: peerId);
    }

    public async Task ToggleRelayCapableAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        await InvokeOnUiAsync(() => peer.Relay.IsRelayCapable = !peer.Relay.IsRelayCapable).ConfigureAwait(false);
        await _store.SaveAsync(_state, cancellationToken);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerRelayCapableChanged,
            $"Peer relay {(peer.Relay.IsRelayCapable ? "enabled" : "disabled")}: {(string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.PeerId.ToString()[..8] : peer.DisplayName)}",
            peerId: peerId);
    }

    public async Task UpdateDisplayNameAsync(Guid peerId, string? displayName, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        await InvokeOnUiAsync(() => peer.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim())
            .ConfigureAwait(false);
        await _store.SaveAsync(_state, cancellationToken);
    }

    public async Task SetOnlineAsync(Guid peerId, bool isOnline, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        await InvokeOnUiAsync(() => peer.IsOnline = isOnline).ConfigureAwait(false);
        await _store.SaveAsync(_state, cancellationToken);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerOnlineChanged,
            $"Peer {(peer.IsOnline ? "online" : "offline")}: {(string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.PeerId.ToString()[..8] : peer.DisplayName)}",
            peerId: peerId);
    }

    public async Task SetRelayCapableAsync(Guid peerId, bool isRelayCapable, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        await InvokeOnUiAsync(() => peer.Relay.IsRelayCapable = isRelayCapable).ConfigureAwait(false);
        await _store.SaveAsync(_state, cancellationToken);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.PeerRelayCapableChanged,
            $"Peer relay {(peer.Relay.IsRelayCapable ? "enabled" : "disabled")}: {(string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.PeerId.ToString()[..8] : peer.DisplayName)}",
            peerId: peerId);
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

        var queued = new RelayQueuedBlobDto
        {
            AckId = Guid.NewGuid(),
            RecipientRoutingKey = recipientRoutingKey,
            OpaqueBytes = opaqueBytes,
            EnqueuedUtc = DateTimeOffset.UtcNow,
            DebugType = debugType
        };

        await InvokeOnUiAsync(() => peer.Relay.OpaqueQueue.Items.Add(queued)).ConfigureAwait(false);

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayEnqueued,
            $"Relay enqueue: {(debugType ?? "opaque")}",
            relayHostPeerId: relayHostPeerId,
            ackId: queued.AckId);
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
            .Take(max)
            .ToList();

        if (matches.Count == 0) return Array.Empty<RelayQueuedBlobDto>();

        await InvokeOnUiAsync(() =>
        {
            foreach (var item in matches)
            {
                peer.Relay.OpaqueQueue.Items.Remove(item);
            }
        }).ConfigureAwait(false);

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
        return matches;
    }

    public Task<RelayQueuedBlobDto?> PeekRelayOpaqueAsync(
        Guid relayHostPeerId,
        byte[] recipientRoutingKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientRoutingKey is null) throw new ArgumentNullException(nameof(recipientRoutingKey));

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return Task.FromResult<RelayQueuedBlobDto?>(null);

        var match = peer.Relay.OpaqueQueue.Items
            .FirstOrDefault(i => i.RecipientRoutingKey.SequenceEqual(recipientRoutingKey));

        return Task.FromResult<RelayQueuedBlobDto?>(match);
    }

    public async Task<bool> MoveRelayOpaqueByAckIdAsync(
        Guid relayHostPeerId,
        Guid ackId,
        int delta,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delta == 0) return false;

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return false;

        var list = peer.Relay.OpaqueQueue.Items;
        var idx = list.FindIndex(i => i.AckId == ackId);
        if (idx < 0) return false;

        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= list.Count) return false;

        await InvokeOnUiAsync(() =>
        {
            var item = list[idx];
            list.RemoveAt(idx);
            list.Insert(newIdx, item);
        }).ConfigureAwait(false);

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayReordered,
            $"Relay reorder: delta={delta}",
            relayHostPeerId: relayHostPeerId,
            ackId: ackId);

        return true;
    }

    public async Task<bool> CorruptRelayOpaqueByAckIdAsync(
        Guid relayHostPeerId,
        Guid ackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return false;

        var item = peer.Relay.OpaqueQueue.Items.FirstOrDefault(i => i.AckId == ackId);
        if (item is null) return false;
        if (item.OpaqueBytes is null || item.OpaqueBytes.Length == 0) return false;

        // Flip one bit in first byte for MAC failure / tamper testing.
        var bytes = item.OpaqueBytes.ToArray();
        bytes[0] = (byte)(bytes[0] ^ 0x01);

        await InvokeOnUiAsync(() => item.OpaqueBytes = bytes).ConfigureAwait(false);

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayCorrupted,
            $"Relay corrupt: {(item.DebugType ?? "opaque")}",
            relayHostPeerId: relayHostPeerId,
            ackId: ackId);

        return true;
    }

    public async Task<bool> DeleteRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return false;

        var changed = false;
        await InvokeOnUiAsync(() =>
        {
            var before = peer.Relay.OpaqueQueue.Items.Count;
            peer.Relay.OpaqueQueue.Items.RemoveAll(i => i.AckId == ackId);
            changed = peer.Relay.OpaqueQueue.Items.Count != before;
        }).ConfigureAwait(false);

        if (changed)
        {
            await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    public async Task PublishPreKeyBundleAsync(
        Guid relayHostPeerId,
        byte[] recipientPublicKeyHash,
        Guid logicalOwnerPeerId,
        byte[] bundleBytes,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
        if (bundleBytes is null) throw new ArgumentNullException(nameof(bundleBytes));

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return;

        // Allow publishing even if not relay-capable; caller/UI should prevent it but we keep storage permissive.
        await InvokeOnUiAsync(() =>
        {
            peer.Relay.PreKeyStore.PublishedBundles.Add(new PublishedPreKeyBundleDto
            {
                RecipientPublicKeyHash = recipientPublicKeyHash,
                LogicalOwnerPeerId = logicalOwnerPeerId,
                BundleBytes = bundleBytes,
                ExpiresUtc = expiresUtc
            });
        }).ConfigureAwait(false);

        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayEnqueued,
            "Pre-key bundle published",
            peerId: logicalOwnerPeerId,
            relayHostPeerId: relayHostPeerId,
            contextTag: Convert.ToBase64String(recipientPublicKeyHash));
    }

    public async Task<PublishedPreKeyBundleDto?> TryPopPreKeyBundleByRecipientPkhAsync(
        Guid relayHostPeerId,
        byte[] recipientPublicKeyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return null;

        var now = DateTimeOffset.UtcNow;
        // Remove expired bundles opportunistically.
        peer.Relay.PreKeyStore.PublishedBundles.RemoveAll(b => b.ExpiresUtc <= now);

        var match = peer.Relay.PreKeyStore.PublishedBundles
            .FirstOrDefault(b => b.RecipientPublicKeyHash.SequenceEqual(recipientPublicKeyHash));

        if (match is null)
        {
            return null;
        }

        peer.Relay.PreKeyStore.PublishedBundles.Remove(match);
        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
        return match;
    }

    public Task<SimulatedPeerRuntimeStoreDto?> TryGetRuntimeStoreAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        return Task.FromResult(peer?.RuntimeStore);
    }

    public async Task SaveRuntimeStoreAsync(Guid peerId, SimulatedPeerRuntimeStoreDto store, CancellationToken cancellationToken = default)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null) return;

        await InvokeOnUiAsync(() => peer.RuntimeStore = store).ConfigureAwait(false);
        await _store.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
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

    private static void NormalizePeer(SimulatedPeerDto peer, TransportOptions transportOptions)
    {
        peer.Connection ??= new SimulatedPeerConnectionDto();
        peer.KnownPeerIds ??= new();
        peer.PreKeys ??= new();
        peer.PreKeys.OneTimePreKeys ??= new();
        peer.RuntimeStore ??= new();
        peer.RuntimeStore.Sessions ??= new();
        peer.RuntimeStore.SignedPreKeys ??= new();
        peer.RuntimeState ??= new SimulatorPeerRuntimeState();
        peer.Relay ??= new();
        peer.Relay.ActiveSessionsPeerIds ??= new();
        peer.Relay.OpaqueQueue ??= new();
        peer.Relay.OpaqueQueue.Items ??= new();
        peer.Relay.PreKeyStore ??= new();
        peer.Relay.PreKeyStore.PublishedBundles ??= new();
        peer.ReverseSignalKeys ??= new();
        peer.IdentityPublicKeyHash ??= Array.Empty<byte>();

        // Ensure runtime state is consistent with online/offline.
        if (!peer.IsOnline)
        {
            peer.RuntimeState = peer.RuntimeState with { UiState = SimulatorPeerUiState.Offline };
        }
        else if (peer.RuntimeState.UiState == SimulatorPeerUiState.Offline)
        {
            peer.RuntimeState = peer.RuntimeState with { UiState = SimulatorPeerUiState.Ready, PendingCorrelationId = null };
        }

        peer.RuntimeState = peer.RuntimeState with
        {
            HandshakeAttempts = peer.RuntimeState.HandshakeAttempts ?? new()
        };

        // Assign stable simulator endpoint if not set. This is a routing key only; no socket bind.
        if (string.IsNullOrWhiteSpace(peer.Connection.Host) || string.Equals(peer.Connection.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            peer.Connection.Host = AllocateSimulatorLoopbackHost(peer.PeerId);
        }
        if (peer.Connection.Port == 0)
        {
            var port = transportOptions.SimulatorPort;
            if (port == 0) port = 5002;
            peer.Connection.Port = port;
        }
    }

    private static bool EnsureIdentityPublicKeyHash(SimulatedPeerDto peer)
    {
        if (peer is null) throw new ArgumentNullException(nameof(peer));

        var spki = peer.ReverseSignalKeys?.IdentitySigningKeySpki;
        if (spki is null || spki.Length == 0)
        {
            return false;
        }

        var computed = SHA256.HashData(spki);
        if (peer.IdentityPublicKeyHash is not null && peer.IdentityPublicKeyHash.AsSpan().SequenceEqual(computed))
        {
            return false;
        }

        peer.IdentityPublicKeyHash = computed;
        return true;
    }

    private static string AllocateSimulatorLoopbackHost(Guid peerId)
    {
        // Stable mapping of Guid -> 127.77.X.Y. Keep within 1..254 to avoid network/broadcast edge cases.
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(peerId.ToByteArray());
        var x = (byte)((hash[0] % 254) + 1);
        var y = (byte)((hash[1] % 254) + 1);
        return $"127.77.{x}.{y}";
    }

    
}
