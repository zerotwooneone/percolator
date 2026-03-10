using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedRelayQueuePanelViewModel : IDisposable
{
    private readonly Guid _relayHostPeerId;
    private readonly Func<Guid, string> _peerNameById;
    private readonly Func<Guid?> _mainIdentityId;
    private readonly Func<Task<SessionId?>> _getRelayHostToMainSessionId;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorRelayDeliveryService _delivery;
    private readonly ILogger<SimulatedRelayQueuePanelViewModel> _logger;

    private DisposableBag _bag;

    private readonly ObservableCollection<SimulatedRelayQueueItemViewModel> _items = new();
    public ReadOnlyObservableCollection<SimulatedRelayQueueItemViewModel> Items { get; }

    public SimulatedRelayQueuePanelViewModel(
        Guid relayHostPeerId,
        string relayHostName,
        Func<Guid, string> peerNameById,
        Func<Guid?> mainIdentityId,
        Func<Task<SessionId?>> getRelayHostToMainSessionId,
        ISimulatorStateService state,
        ISimulatorRelayDeliveryService delivery,
        ILogger<SimulatedRelayQueuePanelViewModel> logger)
    {
        _relayHostPeerId = relayHostPeerId;
        RelayHostName = relayHostName;
        _peerNameById = peerNameById;
        _mainIdentityId = mainIdentityId;
        _getRelayHostToMainSessionId = getRelayHostToMainSessionId;
        _state = state;
        _delivery = delivery;
        _logger = logger;

        Items = new ReadOnlyObservableCollection<SimulatedRelayQueueItemViewModel>(_items);

        QueueCount = new BindableReactiveProperty<int>(0).AddTo(ref _bag);
        AutoDeliver = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);

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
            var peer = _state.Peers.FirstOrDefault(p => p.PeerId == _relayHostPeerId);
            if (peer is null)
            {
                _items.Clear();
                QueueCount.Value = 0;
                return;
            }

            var mainId = _mainIdentityId();
            var ordered = peer.Relay.OpaqueQueue.Items.ToList();

            _items.Clear();
            foreach (var i in ordered)
            {
                _items.Add(new SimulatedRelayQueueItemViewModel(_relayHostPeerId, i, _peerNameById, mainId));
            }

            QueueCount.Value = ordered.Count;
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
            var mainId = _mainIdentityId();
            var routingKey = item.RecipientRoutingKey;

            // Deliver to main identity (Guid routing key)
            if (mainId.HasValue && routingKey is not null && routingKey.Length == 16 && new Guid(routingKey) == mainId.Value)
            {
                var sid = await _getRelayHostToMainSessionId().ConfigureAwait(false);
                if (sid is null)
                {
                    _logger.LogWarning("[simulator] Relay host {RelayHost} has no session to main; cannot deliver", _relayHostPeerId);
                    return;
                }

                await _delivery.DeliverToMainAsync(_relayHostPeerId, sid, item.Model, ct).ConfigureAwait(false);

                _ = await _state.DeleteRelayOpaqueByAckIdAsync(_relayHostPeerId, item.AckId, ct).ConfigureAwait(false);
                await RefreshAsync(ct).ConfigureAwait(false);
                return;
            }

            // Deliver to simulated peer (Guid routing key)
            if (routingKey is not null && routingKey.Length == 16)
            {
                var recipientPeerId = new Guid(routingKey);

                await _delivery.DeliverToPeerAsync(recipientPeerId, item.Model, ct).ConfigureAwait(false);

                _ = await _state.DeleteRelayOpaqueByAckIdAsync(_relayHostPeerId, item.AckId, ct).ConfigureAwait(false);
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
    }
}
