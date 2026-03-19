using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorPeersTabViewModel : IDisposable
{
    private readonly ISimulatedPeerDirectory _directory;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatedPeerRuntimeService _runtime;
    private readonly ISimulatorDiagnosticsService _diagnostics;

    private readonly ObservableCollection<SimulatedPeerCardViewModel> _peerCards = new();
    private DisposableBag _bag;

    public SimulatorPeersTabViewModel(
        ISimulatedPeerDirectory directory,
        ISimulatorStateService state,
        ISimulatedPeerRuntimeService runtime,
        ISimulatorDiagnosticsService diagnostics)
    {
        _directory = directory;
        _state = state;
        _runtime = runtime;
        _diagnostics = diagnostics;

        PeerCards = new ReadOnlyObservableCollection<SimulatedPeerCardViewModel>(_peerCards);

        Status = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        var addPeer = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        addPeer.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteAddPeerAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        AddPeerCommand = addPeer.AddTo(ref _bag);

        _ = InitializeAsync();
    }

    public BindableReactiveProperty<string?> Status { get; }

    public ReactiveCommand<Unit> AddPeerCommand { get; }

    public ReadOnlyObservableCollection<SimulatedPeerCardViewModel> PeerCards { get; }

    public Task ResetAsync(CancellationToken ct = default)
        => InitializeAsync(ct);

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await _directory.InitializeAsync(ct);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ResetPeerCards();
                HookDirectory();
            }
            else
            {
                await dispatcher.InvokeAsync(() =>
                {
                    ResetPeerCards();
                    HookDirectory();
                });
            }

            await SetStatusOnUiAsync($"Loaded {_peerCards.Count} peers");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    private void ResetPeerCards()
    {
        foreach (var p in _peerCards)
        {
            p.Dispose();
        }

        _peerCards.Clear();
        foreach (var m in _directory.Peers)
        {
            _peerCards.Add(CreatePeerCardVm(m));
        }

        RefreshRelationships();
    }

    private SimulatedPeerCardViewModel CreatePeerCardVm(SimulatedPeerModel m)
    {
        return new SimulatedPeerCardViewModel(
            model: m,
            state: _state,
            runtime: _runtime,
            diagnostics: _diagnostics,
            resolvePeerName: ResolvePeerName,
            relationshipsChanged: RefreshRelationships);
    }

    private void HookDirectory()
    {
        var notify = (INotifyCollectionChanged)_directory.Peers;
        notify.CollectionChanged -= OnDirectoryPeersChanged;
        notify.CollectionChanged += OnDirectoryPeersChanged;
    }

    private void OnDirectoryPeersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(() => OnDirectoryPeersChanged(sender, e));
            return;
        }

        if (e.Action is NotifyCollectionChangedAction.Reset)
        {
            ResetPeerCards();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (var oldItem in e.OldItems.OfType<SimulatedPeerModel>())
            {
                var existing = _peerCards.FirstOrDefault(x => x.PeerId == oldItem.PeerId);
                if (existing is null) continue;
                _peerCards.Remove(existing);
                existing.Dispose();
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var newItem in e.NewItems.OfType<SimulatedPeerModel>())
            {
                _peerCards.Add(CreatePeerCardVm(newItem));
            }
        }

        RefreshRelationships();

        _ = SetStatusOnUiAsync($"Peers: {_peerCards.Count}");
    }

    private string ResolvePeerName(Guid peerId)
    {
        var m = _directory.Peers.FirstOrDefault(x => x.PeerId == peerId);
        var name = m?.DisplayName.CurrentValue;
        return string.IsNullOrWhiteSpace(name) ? peerId.ToString()[..8] : name;
    }

    private void RefreshRelationships()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(RefreshRelationships);
            return;
        }

        foreach (var card in _peerCards)
        {
            var dto = _state.Peers.FirstOrDefault(p => p.PeerId == card.PeerId);
            if (dto is null) continue;
            card.RebuildRelationshipTags(dto, _state.Peers);
        }
    }

    private Task SetStatusOnUiAsync(string? status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Status.Value = status;
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => Status.Value = status).Task;
    }

    private async Task ExecuteAddPeerAsync(CancellationToken ct)
    {
        try
        {
            _ = await _directory.AddPeerAsync(displayName: null, ct);
            await SetStatusOnUiAsync($"Peers: {_peerCards.Count}");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var p in _peerCards)
        {
            p.Dispose();
        }

        _peerCards.Clear();

        _bag.Dispose();

        try
        {
            var notify = (INotifyCollectionChanged)_directory.Peers;
            notify.CollectionChanged -= OnDirectoryPeersChanged;
        }
        catch
        {
            // Best-effort.
        }
    }
}
