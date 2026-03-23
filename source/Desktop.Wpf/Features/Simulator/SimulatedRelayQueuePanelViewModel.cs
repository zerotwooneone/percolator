using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedRelayQueuePanelViewModel : IDisposable
{
    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");

    private readonly Guid _relayHostPeerId;
    private readonly Func<Guid, string> _peerNameById;
    private readonly Func<byte[]?> _getMainIdentityPkh;
    private readonly Func<Task<SessionId?>> _getRelayHostToMainSessionId;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorRelayDeliveryService _delivery;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ILogger<SimulatedRelayQueuePanelViewModel> _logger;

    private DisposableBag _bag;

    private readonly ObservableCollection<SimulatedRelayQueueItemViewModel> _items = new();
    public ReadOnlyObservableCollection<SimulatedRelayQueueItemViewModel> Items { get; }

    public ObservableCollection<ActiveSessionTagViewModel> ActiveSessionTags { get; }

    public ObservableCollection<ActiveSessionTargetOption> AvailableActiveSessionTargets { get; }

    public BindableReactiveProperty<Guid?> SelectedActiveSessionPeerId { get; }

    public ReactiveCommand<Unit> AddActiveSessionCommand { get; }

    public SimulatedRelayQueuePanelViewModel(
        Guid relayHostPeerId,
        string relayHostName,
        Func<Guid, string> peerNameById,
        Func<byte[]?> getMainIdentityPkh,
        Func<Task<SessionId?>> getRelayHostToMainSessionId,
        ISimulatorStateService state,
        ISimulatorRelayDeliveryService delivery,
        ISimulatorDiagnosticsService diagnostics,
        ILogger<SimulatedRelayQueuePanelViewModel> logger)
    {
        _relayHostPeerId = relayHostPeerId;
        RelayHostName = relayHostName;
        _peerNameById = peerNameById;
        _getMainIdentityPkh = getMainIdentityPkh;
        _getRelayHostToMainSessionId = getRelayHostToMainSessionId;
        _state = state;
        _delivery = delivery;
        _diagnostics = diagnostics;
        _logger = logger;

        Items = new ReadOnlyObservableCollection<SimulatedRelayQueueItemViewModel>(_items);

        ActiveSessionTags = new ObservableCollection<ActiveSessionTagViewModel>();
        AvailableActiveSessionTargets = new ObservableCollection<ActiveSessionTargetOption>();
        SelectedActiveSessionPeerId = new BindableReactiveProperty<Guid?>(null).AddTo(ref _bag);

        QueueCount = new BindableReactiveProperty<int>(0).AddTo(ref _bag);
        AutoDeliver = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);

        var addSession = SelectedActiveSessionPeerId
            .Select(x => x.HasValue)
            .ToReactiveCommand<Unit>(_ => { });
        addSession
            .AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteAddActiveSessionAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        AddActiveSessionCommand = addSession.AddTo(ref _bag);

        var refresh = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        refresh.AsObservable().SubscribeAwait(async (_, ct) => await RefreshAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        RefreshCommand = refresh.AddTo(ref _bag);

        var next = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        next.AsObservable().SubscribeAwait(async (_, ct) => await DeliverNextAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        NextCommand = next.AddTo(ref _bag);

        var all = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        all.AsObservable().SubscribeAwait(async (_, ct) => await DeliverAllAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        AllCommand = all.AddTo(ref _bag);

        DeliverItemCommand = new ReactiveCommand<SimulatedRelayQueueItemViewModel>().AddTo(ref _bag);
        DeliverItemCommand
            .AsObservable()
            .SubscribeAwait(async (item, ct) =>
                {
                    if (item is null) return;
                    await DeliverItemAsync(item, ct).ConfigureAwait(false);
                },
                AwaitOperation.Drop)
            .AddTo(ref _bag);

        DropItemCommand = new ReactiveCommand<SimulatedRelayQueueItemViewModel>().AddTo(ref _bag);
        DropItemCommand
            .AsObservable()
            .SubscribeAwait(async (item, ct) =>
                {
                    if (item is null) return;
                    await DropItemAsync(item, ct).ConfigureAwait(false);
                },
                AwaitOperation.Drop)
            .AddTo(ref _bag);

        MoveUpCommand = new ReactiveCommand<SimulatedRelayQueueItemViewModel>().AddTo(ref _bag);
        MoveUpCommand
            .AsObservable()
            .SubscribeAwait(async (item, ct) =>
                {
                    if (item is null) return;
                    await MoveItemAsync(item, delta: -1, ct).ConfigureAwait(false);
                },
                AwaitOperation.Drop)
            .AddTo(ref _bag);

        MoveDownCommand = new ReactiveCommand<SimulatedRelayQueueItemViewModel>().AddTo(ref _bag);
        MoveDownCommand
            .AsObservable()
            .SubscribeAwait(async (item, ct) =>
                {
                    if (item is null) return;
                    await MoveItemAsync(item, delta: 1, ct).ConfigureAwait(false);
                },
                AwaitOperation.Drop)
            .AddTo(ref _bag);

        CorruptCommand = new ReactiveCommand<SimulatedRelayQueueItemViewModel>().AddTo(ref _bag);
        CorruptCommand
            .AsObservable()
            .SubscribeAwait(async (item, ct) =>
                {
                    if (item is null) return;
                    await CorruptItemAsync(item, ct).ConfigureAwait(false);
                },
                AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public Guid RelayHostPeerId => _relayHostPeerId;

    public string RelayHostName { get; }

    public BindableReactiveProperty<int> QueueCount { get; }

    public BindableReactiveProperty<bool> AutoDeliver { get; }

    private async Task ExecuteAddActiveSessionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var peerId = SelectedActiveSessionPeerId.Value;
        if (!peerId.HasValue) return;

        await _state.AddRelayActiveSessionAsync(_relayHostPeerId, peerId.Value, ct).ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public ReactiveCommand<Unit> RefreshCommand { get; }

    public ReactiveCommand<Unit> NextCommand { get; }

    public ReactiveCommand<Unit> AllCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> DeliverItemCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> DropItemCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> MoveUpCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> MoveDownCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> CorruptCommand { get; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshOnUi();
            return;
        }

        await dispatcher.InvokeAsync(RefreshOnUi);

        void RefreshOnUi()
        {
            ct.ThrowIfCancellationRequested();

            // State service is initialized by parent VM.
            var peer = _state.TryGetPeerSnapshot(_relayHostPeerId);
            if (peer is null)
            {
                _items.Clear();
                QueueCount.Value = 0;
                return;
            }

            var mainPkh = _getMainIdentityPkh();
            var ordered = peer.RelayOpaqueQueueItems.ToList();

            _items.Clear();
            foreach (var i in ordered)
            {
                _items.Add(new SimulatedRelayQueueItemViewModel(_relayHostPeerId, i, _peerNameById, mainPkh));
            }

            QueueCount.Value = ordered.Count;

            ActiveSessionTags.Clear();
            AvailableActiveSessionTargets.Clear();

            var active = peer.RelayActiveSessionsPeerIds.Distinct().ToHashSet();
            foreach (var other in _state.SnapshotPeers().Where(p => p.PeerId != _relayHostPeerId))
            {
                AvailableActiveSessionTargets.Add(new ActiveSessionTargetOption(other.PeerId, _peerNameById(other.PeerId)));
            }

            foreach (var peerId in active)
            {
                ActiveSessionTags.Add(new ActiveSessionTagViewModel(
                    peerId: peerId,
                    display: _peerNameById(peerId),
                    onRemove: async removeCt =>
                    {
                        await _state.RemoveRelayActiveSessionAsync(_relayHostPeerId, peerId, removeCt).ConfigureAwait(false);
                        await RefreshAsync(removeCt).ConfigureAwait(false);
                    }));
            }
        }
    }

    public async Task DeliverNextAsync(CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);

        var next = await GetNextItemOnUiAsync(ct).ConfigureAwait(false);
        if (next is null) return;
        await DeliverItemAsync(next, ct).ConfigureAwait(false);
    }

    public async Task DeliverAllAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await RefreshAsync(ct).ConfigureAwait(false);
            var next = await GetNextItemOnUiAsync(ct).ConfigureAwait(false);
            if (next is null) return;
            await DeliverItemAsync(next, ct).ConfigureAwait(false);
        }
    }

    private Task<SimulatedRelayQueueItemViewModel?> GetNextItemOnUiAsync(CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_items.FirstOrDefault());
        }

        return dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _items.FirstOrDefault();
        }).Task;
    }

    private async Task DeliverItemAsync(SimulatedRelayQueueItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var mainPkh = _getMainIdentityPkh();
            var routingKey = item.RecipientRoutingKey;

            // Deliver to main identity (PKH routing key)
            if (mainPkh is not null && mainPkh.Length == 32 && routingKey is not null && routingKey.Length == 32
                && routingKey.AsSpan().SequenceEqual(mainPkh))
            {
                var sid = await _getRelayHostToMainSessionId().ConfigureAwait(false);
                if (sid is null)
                {
                    _logger.LogWarning("[simulator] Relay host {RelayHost} has no session to main; cannot deliver", _relayHostPeerId);
                    return;
                }

                await _delivery.DeliverToMainAsync(_relayHostPeerId, sid, item.Model, ct).ConfigureAwait(false);

                _ = await _state.DeleteRelayOpaqueByAckIdAsync(_relayHostPeerId, item.AckId, ct).ConfigureAwait(false);

                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.RelayDelivered,
                    $"Relay deliver -> main: {(item.Model.DebugType ?? "opaque")}",
                    relayHostPeerId: _relayHostPeerId,
                    ackId: item.AckId);

                await RefreshAsync(ct).ConfigureAwait(false);
                return;
            }

            // Deliver to simulated peer (PKH routing key)
            if (routingKey is not null && routingKey.Length == 32)
            {
                var recipientPeerId = await _state.TryGetPeerIdByIdentityPkhAsync(routingKey, ct).ConfigureAwait(false);
                if (!recipientPeerId.HasValue)
                {
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.RelayRoutingFailure,
                        $"Relay routing failure (no peer for PKH): {(item.Model.DebugType ?? "opaque")}",
                        relayHostPeerId: _relayHostPeerId,
                        ackId: item.AckId);
                    return;
                }

                await _delivery.DeliverToPeerAsync(_relayHostPeerId, recipientPeerId.Value, item.Model, ct).ConfigureAwait(false);

                _ = await _state.DeleteRelayOpaqueByAckIdAsync(_relayHostPeerId, item.AckId, ct).ConfigureAwait(false);

                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.RelayDelivered,
                    $"Relay deliver -> {recipientPeerId.Value.ToString()[..8]}: {(item.Model.DebugType ?? "opaque")}",
                    peerId: recipientPeerId.Value,
                    relayHostPeerId: _relayHostPeerId,
                    ackId: item.AckId);

                await RefreshAsync(ct).ConfigureAwait(false);
                return;
            }

            // Deliver to simulated peer (Guid routing key)
            if (routingKey is not null && routingKey.Length == 16)
            {
                var recipientPeerId = new Guid(routingKey);

                if (recipientPeerId == MainNodeSentinelPeerId)
                {
                    var sid = await _getRelayHostToMainSessionId().ConfigureAwait(false);
                    if (sid is null)
                    {
                        _logger.LogWarning("[simulator] Relay host {RelayHost} has no session to main; cannot deliver", _relayHostPeerId);
                        return;
                    }

                    await _delivery.DeliverToMainAsync(_relayHostPeerId, sid, item.Model, ct).ConfigureAwait(false);

                    _ = await _state.DeleteRelayOpaqueByAckIdAsync(_relayHostPeerId, item.AckId, ct).ConfigureAwait(false);

                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.RelayDelivered,
                        $"Relay deliver -> main: {(item.Model.DebugType ?? "opaque")}",
                        relayHostPeerId: _relayHostPeerId,
                        ackId: item.AckId);

                    await RefreshAsync(ct).ConfigureAwait(false);
                    return;
                }

                await _delivery.DeliverToPeerAsync(_relayHostPeerId, recipientPeerId, item.Model, ct).ConfigureAwait(false);

                _ = await _state.DeleteRelayOpaqueByAckIdAsync(_relayHostPeerId, item.AckId, ct).ConfigureAwait(false);

                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.RelayDelivered,
                    $"Relay deliver -> {recipientPeerId.ToString()[..8]}: {(item.Model.DebugType ?? "opaque")}",
                    peerId: recipientPeerId,
                    relayHostPeerId: _relayHostPeerId,
                    ackId: item.AckId);

                await RefreshAsync(ct).ConfigureAwait(false);
                return;
            }

            // Unknown routing key; can't safely deliver.
            _logger.LogWarning("[simulator] Cannot deliver relay item {AckId} with routingKeyLen={Len}", item.AckId, routingKey?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[simulator] Error delivering relay item {AckId}", item.AckId);
        }
    }

    private async Task DropItemAsync(SimulatedRelayQueueItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _state.DeleteRelayOpaqueByAckIdAsync(relayHostPeerId: _relayHostPeerId, ackId: item.AckId, cancellationToken: ct).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayDropped,
            $"Relay drop: {(item.Model.DebugType ?? "opaque")}",
            relayHostPeerId: _relayHostPeerId,
            ackId: item.AckId);

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    private async Task MoveItemAsync(SimulatedRelayQueueItemViewModel item, int delta, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _state.MoveRelayOpaqueByAckIdAsync(relayHostPeerId: _relayHostPeerId, ackId: item.AckId, delta: delta, cancellationToken: ct).ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    private async Task CorruptItemAsync(SimulatedRelayQueueItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _state.CorruptRelayOpaqueByAckIdAsync(relayHostPeerId: _relayHostPeerId, ackId: item.AckId, cancellationToken: ct).ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _bag.Dispose();
        QueueCount.Dispose();
        AutoDeliver.Dispose();
        SelectedActiveSessionPeerId.Dispose();
    }

    public sealed class ActiveSessionTagViewModel
    {
        private readonly Func<CancellationToken, Task> _remove;

        public ActiveSessionTagViewModel(Guid peerId, string display, Func<CancellationToken, Task> onRemove)
        {
            PeerId = peerId;
            Display = display;
            _remove = onRemove;

            var cmd = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
            cmd.AsObservable().SubscribeAwait(async (_, ct) => await _remove(ct), AwaitOperation.Drop);
            RemoveCommand = cmd;
        }

        public Guid PeerId { get; }

        public string Display { get; }

        public ReactiveCommand<Unit> RemoveCommand { get; }
    }

    public sealed class ActiveSessionTargetOption
    {
        public ActiveSessionTargetOption(Guid peerId, string display)
        {
            PeerId = peerId;
            Display = display;
        }

        public Guid PeerId { get; }

        public string Display { get; }
    }
}
