using ObservableCollections;
using R3;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorPeersTabViewModel : IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorDiagnosticsService _diagnostics;

    private ISynchronizedView<SimulatedPeerModel, SimulatedPeerCardViewModel>? _peerCards;
    private NotifyCollectionChangedSynchronizedViewList<SimulatedPeerCardViewModel>? _peerCardsNotify;
    private DisposableBag _bag;

    public SimulatorPeersTabViewModel(
        IUiDispatcher ui,
        ISimulatorStateService state,
        ISimulatorDiagnosticsService diagnostics)
    {
        _ui = ui;
        _state = state;
        _diagnostics = diagnostics;

        Status = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        AddPeerCommand = new ReactiveCommand<Unit>().AddTo(ref _bag);
        AddPeerCommand
            .AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteAddPeerAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public BindableReactiveProperty<string?> Status { get; }

    public ReactiveCommand<Unit> AddPeerCommand { get; }

    public NotifyCollectionChangedSynchronizedViewList<SimulatedPeerCardViewModel> PeerCards
        => _peerCardsNotify ?? throw new InvalidOperationException("ViewModel not initialized.");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await _state.InitializeAsync(ct).ConfigureAwait(false);

            await _ui.InvokeAsync(InitializePeerCardsView, ct).ConfigureAwait(false);

            await SetStatusOnUiAsync($"Loaded {PeerCards.Count} peers");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    private void InitializePeerCardsView()
    {
        _peerCards?.Dispose();
        _peerCardsNotify?.Dispose();

        var peers = _state.Peers;

        _peerCards = peers
            .CreateView(CreatePeerCardVm)
            .AddTo(ref _bag);

        // IMPORTANT: Call once and keep it alive for the VM lifetime.
        // Do NOT call ToNotifyCollectionChanged() repeatedly from a getter.
        _peerCardsNotify = _peerCards.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);

        peers.ObserveCountChanged()
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ =>
            {
                Status.Value = $"Peers: {_peerCardsNotify.Count}";
            })
            .AddTo(ref _bag);
    }

    private SimulatedPeerCardViewModel CreatePeerCardVm(SimulatedPeerModel m)
    {
        return new SimulatedPeerCardViewModel(
            model: m,
            ui: _ui,
            state: _state,
            diagnostics: _diagnostics,
            resolvePeerName: ResolvePeerName);
    }

    private string ResolvePeerName(Guid peerId)
    {
        var m = _state.Peers.FirstOrDefault(x => x.PeerId == peerId);
        var name = m?.DisplayName.CurrentValue;
        return string.IsNullOrWhiteSpace(name) ? peerId.ToString()[..8] : name;
    }

    private Task SetStatusOnUiAsync(string? status)
        => _ui.InvokeAsync(() => Status.Value = status);

    private async Task ExecuteAddPeerAsync(CancellationToken ct)
    {
        try
        {
            _ = await _state.AddPeerAsync(displayName: null, ct).ConfigureAwait(false);
            await SetStatusOnUiAsync($"Peers: {_peerCardsNotify?.Count}");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _peerCardsNotify?.Dispose();
        _peerCardsNotify = null;
        _peerCards?.Dispose();
        _peerCards = null;
        _bag.Dispose();
    }
}
