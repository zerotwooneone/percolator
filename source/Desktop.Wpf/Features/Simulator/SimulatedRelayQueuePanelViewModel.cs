using Desktop.Wpf.Features.Simulator.Models;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Cryptography;
using R3;
using System.Linq;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedRelayQueuePanelViewModel : IDisposable
{
    private readonly Guid _relayHostPeerId;
    private readonly Func<Guid, string> _peerNameById;
    private readonly Func<Task<SessionId?>> _getRelayHostToMainSessionId;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorRelayDeliveryService _delivery;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ILogger<SimulatedRelayQueuePanelViewModel> _logger;

    private DisposableBag _bag;

    private readonly Desktop.Wpf.Features.Simulator.Models.SimulatedRelayModel _relay;

    private readonly ISynchronizedView<SimulatedPeerModel, ActiveSessionTargetOption> _availableTargets;
    private readonly NotifyCollectionChangedSynchronizedViewList<ActiveSessionTargetOption> _availableTargetsNotify;

    private readonly ISynchronizedView<PeerRelationship, ActiveSessionTagViewModel> _activeSessionTags;
    private readonly NotifyCollectionChangedSynchronizedViewList<ActiveSessionTagViewModel> _activeSessionTagsNotify;

    public BindableReactiveProperty<Guid?> SelectedActiveSessionPeerId { get; }

    public ReactiveCommand<Unit> AddActiveSessionCommand { get; }

    public NotifyCollectionChangedSynchronizedViewList<ActiveSessionTagViewModel> ActiveSessionTags => _activeSessionTagsNotify;

    public NotifyCollectionChangedSynchronizedViewList<ActiveSessionTargetOption> AvailableActiveSessionTargets => _availableTargetsNotify;

    public SimulatedRelayQueuePanelViewModel(
        Guid relayHostPeerId,
        string relayHostName,
        Func<Guid, string> peerNameById,
        Func<Task<SessionId?>> getRelayHostToMainSessionId,
        ISimulatorStateService state,
        ISimulatorRelayDeliveryService delivery,
        ISimulatorDiagnosticsService diagnostics,
        ILogger<SimulatedRelayQueuePanelViewModel> logger)
    {
        _relayHostPeerId = relayHostPeerId;
        RelayHostName = relayHostName;
        _peerNameById = peerNameById;
        _getRelayHostToMainSessionId = getRelayHostToMainSessionId;
        _state = state;
        _delivery = delivery;
        _diagnostics = diagnostics;
        _logger = logger;

        _relay = _state.Relays.FirstOrDefault(r => r.RelayHostPeerId == _relayHostPeerId)
            ?? throw new InvalidOperationException($"No relay exists for host peer id {_relayHostPeerId}");

        _availableTargets = _state.Peers
            .CreateView(p => new ActiveSessionTargetOption(p.PeerId, _peerNameById(p.PeerId)))
            .AddTo(ref _bag);
        _availableTargets.AttachFilter((p, _) => p.PeerId != _relayHostPeerId);
        _availableTargetsNotify = _availableTargets.ToNotifyCollectionChanged().AddTo(ref _bag);

        _activeSessionTags = _state.Relationships
            .CreateView(rel => CreateActiveSessionTag(rel.TargetPeerId))
            .AddTo(ref _bag);
        _activeSessionTags.AttachFilter((rel, _) =>
            rel.SourcePeerId == _relayHostPeerId
            && rel.Type == RelationshipType.RelayActiveSession);
        _activeSessionTagsNotify = _activeSessionTags.ToNotifyCollectionChanged().AddTo(ref _bag);
        SelectedActiveSessionPeerId = new BindableReactiveProperty<Guid?>(null).AddTo(ref _bag);

        var synchronizedQueueView = _relay.MessageQueue
            .CreateView(kvp =>
            {
                var m = kvp.Value;
                return new SimulatedRelayQueueItemViewModel(
                    relayHostPeerId: _relayHostPeerId,
                    ackId: m.AckId,
                    enqueuedUtc: m.EnqueuedUtc,
                    debugType: m.DebugType,
                    targetPkh: m is InboundRelayMessage inbound ? inbound.TargetPkh : null,
                    opaqueBytes: m.OpaqueBytes,
                    peerNameById: _peerNameById);
            })
            .AddTo(ref _bag);

        QueueItems = synchronizedQueueView.ToNotifyCollectionChanged().AddTo(ref _bag);

        QueueCount = synchronizedQueueView
            .ObserveCountChanged()
            .ToBindableReactiveProperty(synchronizedQueueView.Count)
            .AddTo(ref _bag);

        AutoDeliver = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);

        AddActiveSessionCommand = new ReactiveCommand<Unit>().AddTo(ref _bag);
        AddActiveSessionCommand
            .AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteAddActiveSessionAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        NextCommand = new ReactiveCommand<Unit>().AddTo(ref _bag);
        NextCommand
            .AsObservable()
            .SubscribeAwait(async (_, ct) => await DeliverNextAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        AllCommand = new ReactiveCommand<Unit>().AddTo(ref _bag);
        AllCommand
            .AsObservable()
            .SubscribeAwait(async (_, ct) => await DeliverAllAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

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

    public NotifyCollectionChangedSynchronizedViewList<SimulatedRelayQueueItemViewModel> QueueItems { get; }

    public BindableReactiveProperty<int> QueueCount { get; }

    public BindableReactiveProperty<bool> AutoDeliver { get; }

    private async Task ExecuteAddActiveSessionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var peerId = SelectedActiveSessionPeerId.Value;
        if (!peerId.HasValue) return;

        await _state.AddRelayActiveSessionAsync(_relayHostPeerId, peerId.Value, ct).ConfigureAwait(false);
    }

    public ReactiveCommand<Unit> NextCommand { get; }

    public ReactiveCommand<Unit> AllCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> DeliverItemCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> DropItemCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> MoveUpCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> MoveDownCommand { get; }

    public ReactiveCommand<SimulatedRelayQueueItemViewModel> CorruptCommand { get; }

    public async Task DeliverNextAsync(CancellationToken ct)
    {
        // Snapshot domain state so this can be safely invoked from background loops.
        var next = _relay.MessageQueue
            .Select(kvp => kvp.Value)
            .OrderBy(x => x.EnqueuedUtc)
            .FirstOrDefault();

        if (next is null) return;

        await DeliverItemAsync(next, ct).ConfigureAwait(false);
    }

    public async Task DeliverAllAsync(CancellationToken ct)
    {
        // Snapshot domain state so this can be safely invoked from background loops.
        var itemsToDeliver = _relay.MessageQueue
            .Select(kvp => kvp.Value)
            .OrderBy(x => x.EnqueuedUtc)
            .ToList();

        foreach (var item in itemsToDeliver)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverItemAsync(item, ct).ConfigureAwait(false);
        }
    }

    private async Task DeliverItemAsync(SimulatedRelayQueueItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await DeliverItemAsync(
                    ackId: item.AckId,
                    enqueuedUtc: item.EnqueuedUtc,
                    debugType: item.DebugType,
                    targetPkh: item.TargetPkh,
                    opaqueBytes: item.OpaqueBytes,
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[simulator] Error delivering relay item {AckId}", item.AckId);
        }
    }

    private Task DeliverItemAsync(RelayMessage message, CancellationToken ct)
    {
        byte[]? targetPkh = message is InboundRelayMessage inbound ? inbound.TargetPkh : null;
        return DeliverItemAsync(
            ackId: message.AckId,
            enqueuedUtc: message.EnqueuedUtc,
            debugType: message.DebugType,
            targetPkh: targetPkh,
            opaqueBytes: message.OpaqueBytes,
            ct: ct);
    }

    private async Task DeliverItemAsync(
        Guid ackId,
        DateTimeOffset enqueuedUtc,
        string? debugType,
        byte[]? targetPkh,
        byte[] opaqueBytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (targetPkh is null || targetPkh.Length == 0)
        {
            var sid = await _getRelayHostToMainSessionId().ConfigureAwait(false);
            if (sid is null)
            {
                _logger.LogWarning("[simulator] Relay host {RelayHost} has no session to main; cannot deliver", _relayHostPeerId);
                return;
            }

            var delivered = await _state.DeliverRelayUpstreamToMainByAckIdAsync(_relayHostPeerId, sid, ackId, ct).ConfigureAwait(false);
            if (!delivered)
            {
                return;
            }

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayDelivered,
                $"Relay deliver -> main: {(debugType ?? "opaque")}",
                relayHostPeerId: _relayHostPeerId,
                ackId: ackId);
            return;
        }

        // Deliver to simulated peer (PKH)
        if (targetPkh.Length == 32)
        {
            var recipientPeerId = await _state.TryGetPeerIdByIdentityPkhAsync(targetPkh, ct).ConfigureAwait(false);
            if (!recipientPeerId.HasValue)
            {
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.RelayRoutingFailure,
                    $"Relay routing failure (no peer for PKH): {(debugType ?? "opaque")}",
                    relayHostPeerId: _relayHostPeerId,
                    ackId: ackId);
                return;
            }

            await _delivery.DeliverToPeerAsync(
                    relayHostPeerId: _relayHostPeerId,
                    recipientPeerId: recipientPeerId.Value,
                    ackId: ackId,
                    opaqueBytes: opaqueBytes,
                    debugType: debugType,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            _ = await _state.DeleteRelayMessageByAckIdAsync(_relayHostPeerId, ackId, ct).ConfigureAwait(false);

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.RelayDelivered,
                $"Relay deliver -> {recipientPeerId.Value.ToString()[..8]}: {(debugType ?? "opaque")}",
                peerId: recipientPeerId.Value,
                relayHostPeerId: _relayHostPeerId,
                ackId: ackId);
            return;
        }

        _logger.LogWarning("[simulator] Cannot deliver relay item {AckId} with unknown target", ackId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task DropItemAsync(SimulatedRelayQueueItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _state.DeleteRelayMessageByAckIdAsync(relayHostPeerId: _relayHostPeerId, ackId: item.AckId, cancellationToken: ct).ConfigureAwait(false);

        _diagnostics.Emit(
            SimulatorDiagnosticEventType.RelayDropped,
            $"Relay drop: {(item.DebugType ?? "opaque")}",
            relayHostPeerId: _relayHostPeerId,
            ackId: item.AckId);
    }

    private async Task MoveItemAsync(SimulatedRelayQueueItemViewModel item, int delta, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _state.MoveRelayMessageByAckIdAsync(relayHostPeerId: _relayHostPeerId, ackId: item.AckId, delta: delta, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task CorruptItemAsync(SimulatedRelayQueueItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _state.CorruptRelayMessageByAckIdAsync(relayHostPeerId: _relayHostPeerId, ackId: item.AckId, cancellationToken: ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var tag in ActiveSessionTags)
        {
            tag.Dispose();
        }
        _bag.Dispose();
    }

    private ActiveSessionTagViewModel CreateActiveSessionTag(Guid peerId)
    {
        return new ActiveSessionTagViewModel(
            peerId: peerId,
            display: _peerNameById(peerId),
            onRemove: removeCt => _state.RemoveRelayActiveSessionAsync(_relayHostPeerId, peerId, removeCt));
    }

    public sealed class ActiveSessionTagViewModel : IDisposable
    {
        private readonly Func<CancellationToken, Task> _remove;
        private readonly IDisposable _removeSubscription;

        public ActiveSessionTagViewModel(Guid peerId, string display, Func<CancellationToken, Task> onRemove)
        {
            PeerId = peerId;
            Display = display;
            _remove = onRemove;

            RemoveCommand = new ReactiveCommand<Unit>();
            _removeSubscription = RemoveCommand
                .AsObservable()
                .SubscribeAwait(async (_, ct) => await _remove(ct), AwaitOperation.Drop);
        }

        public Guid PeerId { get; }

        public string Display { get; }

        public ReactiveCommand<Unit> RemoveCommand { get; }

        public void Dispose()
        {
            _removeSubscription.Dispose();
            RemoveCommand.Dispose();
        }
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
