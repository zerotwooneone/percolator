using ObservableCollections;
using Percolator.Network;
using R3;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorDiagnosticsTabViewModel : IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ISimulatorStateService _state;

    private readonly ISynchronizedView<SimulatorDiagnosticEvent, SimulatorDiagnosticEvent> _filteredView;
    private readonly NotifyCollectionChangedSynchronizedViewList<SimulatorDiagnosticEvent> _filteredNotify;
    private DisposableBag _bag;

    public SimulatorDiagnosticsTabViewModel(IUiDispatcher ui, ISimulatorDiagnosticsService diagnostics, ISimulatorStateService state)
    {
        _ui = ui;
        _diagnostics = diagnostics;
        _state = state;

        _filteredView = _diagnostics.Events
            .CreateView(static ev => ev)
            .AddTo(ref _bag);
        _filteredNotify = _filteredView.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);

        SelectedPeerId = new BindableReactiveProperty<NetworkPeerId?>(null).AddTo(ref _bag);
        SelectedRelayHostPeerId = new BindableReactiveProperty<NetworkPeerId?>(null).AddTo(ref _bag);
        SelectedEventType = new BindableReactiveProperty<SimulatorDiagnosticEventType?>(null).AddTo(ref _bag);

        PeerFilterOptions = new BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<NetworkPeerId?>>>(Array.Empty<SimulatorFilterOption<NetworkPeerId?>>()).AddTo(ref _bag);
        RelayFilterOptions = new BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<NetworkPeerId?>>>(Array.Empty<SimulatorFilterOption<NetworkPeerId?>>()).AddTo(ref _bag);
        EventTypeFilterOptions = new BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<SimulatorDiagnosticEventType?>>>(Array.Empty<SimulatorFilterOption<SimulatorDiagnosticEventType?>>()).AddTo(ref _bag);

        ClearCommand = new ReactiveCommand<Unit>().AddTo(ref _bag);
        ClearCommand
            .AsObservable()
            .Subscribe(_ => Clear())
            .AddTo(ref _bag);

        Observable
            .Merge(
                SelectedPeerId.Skip(1).Select(static _ => Unit.Default),
                SelectedRelayHostPeerId.Skip(1).Select(static _ => Unit.Default),
                SelectedEventType.Skip(1).Select(static _ => Unit.Default))
            .Debounce(TimeSpan.FromMilliseconds(50), TimeProvider.System)
            .SubscribeAwait(async (_, ct) => await _ui.InvokeAsync(ApplyFilterOnUi, ct), AwaitOperation.Drop)
            .AddTo(ref _bag);

        var peers = _state.Peers;
        var peersChanged = peers.ObserveChanged();

        peersChanged
            .SubscribeAwait(async (_, ct) =>
            {
                var peerOptions = BuildPeerFilterOptions();
                var relayOptions = BuildRelayFilterOptions();
                var typeOptions = BuildEventTypeFilterOptions();

                await _ui.InvokeAsync(() =>
                {
                    PeerFilterOptions.Value = peerOptions;
                    RelayFilterOptions.Value = relayOptions;
                    EventTypeFilterOptions.Value = typeOptions;
                }, ct).ConfigureAwait(false);

                await _ui.InvokeAsync(ApplyFilterOnUi, ct).ConfigureAwait(false);
            }, AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public NotifyCollectionChangedSynchronizedViewList<SimulatorDiagnosticEvent> Events => _filteredNotify;

    public BindableReactiveProperty<NetworkPeerId?> SelectedPeerId { get; }
    public BindableReactiveProperty<NetworkPeerId?> SelectedRelayHostPeerId { get; }
    public BindableReactiveProperty<SimulatorDiagnosticEventType?> SelectedEventType { get; }

    public BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<NetworkPeerId?>>> PeerFilterOptions { get; }
    public BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<NetworkPeerId?>>> RelayFilterOptions { get; }
    public BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<SimulatorDiagnosticEventType?>>> EventTypeFilterOptions { get; }

    public ReactiveCommand<Unit> ClearCommand { get; }

    private void Clear()
    {
        _diagnostics.Clear();
    }

    private IReadOnlyList<SimulatorFilterOption<NetworkPeerId?>> BuildPeerFilterOptions()
    {
        var peers = _state.Peers
            .Select(p => new SimulatorFilterOption<NetworkPeerId?>(
                p.NetworkPeerId,
                string.IsNullOrWhiteSpace(p.DisplayName.CurrentValue)
                    ? $"Peer{p.NetworkPeerId.Value:D2}"
                    : p.DisplayName.CurrentValue!))
            .OrderBy(p => p.Display)
            .ToList();

        peers.Insert(0, new SimulatorFilterOption<NetworkPeerId?>(null, "All"));
        return peers;
    }

    private IReadOnlyList<SimulatorFilterOption<NetworkPeerId?>> BuildRelayFilterOptions()
    {
        var relays = _state.Peers
            .Where(p => p.IsRelayCapable.CurrentValue)
            .Select(p => new SimulatorFilterOption<NetworkPeerId?>(
                p.NetworkPeerId,
                string.IsNullOrWhiteSpace(p.DisplayName.CurrentValue)
                    ? $"Peer{p.NetworkPeerId.Value:D2}"
                    : p.DisplayName.CurrentValue!))
            .OrderBy(p => p.Display)
            .ToList();

        relays.Insert(0, new SimulatorFilterOption<NetworkPeerId?>(null, "All"));
        return relays;
    }

    private static IReadOnlyList<SimulatorFilterOption<SimulatorDiagnosticEventType?>> BuildEventTypeFilterOptions()
    {
        var types = Enum.GetValues(typeof(SimulatorDiagnosticEventType))
            .Cast<SimulatorDiagnosticEventType>()
            .Select(t => new SimulatorFilterOption<SimulatorDiagnosticEventType?>(t, t.ToString()))
            .OrderBy(t => t.Display)
            .ToList();

        types.Insert(0, new SimulatorFilterOption<SimulatorDiagnosticEventType?>(null, "All"));
        return types;
    }

    private void ApplyFilterOnUi()
    {
        if (_filteredView is null) return;

        var peerId = SelectedPeerId.Value;
        var relayHostId = SelectedRelayHostPeerId.Value;
        var eventType = SelectedEventType.Value;

        if (peerId is null && relayHostId is null && eventType is null)
        {
            _filteredView.ResetFilter();
            return;
        }

        _filteredView.AttachFilter(new DiagnosticsFilter(peerId, relayHostId, eventType));
    }

    private sealed class DiagnosticsFilter : ISynchronizedViewFilter<SimulatorDiagnosticEvent, SimulatorDiagnosticEvent>
    {
        private readonly NetworkPeerId? _peerId;
        private readonly NetworkPeerId? _relayHostPeerId;
        private readonly SimulatorDiagnosticEventType? _eventType;

        public DiagnosticsFilter(NetworkPeerId? peerId, NetworkPeerId? relayHostPeerId, SimulatorDiagnosticEventType? eventType)
        {
            _peerId = peerId;
            _relayHostPeerId = relayHostPeerId;
            _eventType = eventType;
        }

        public bool IsMatch(SimulatorDiagnosticEvent value, SimulatorDiagnosticEvent view)
        {
            if (_peerId is not null && value.PeerId?.Value != _peerId?.Value) return false;
            if (_relayHostPeerId is not null && value.RelayHostPeerId?.Value != _relayHostPeerId?.Value) return false;
            if (_eventType is not null && value.EventType != _eventType) return false;
            return true;
        }
    }

    public void Dispose()
    {
        _filteredNotify.Dispose();
        _filteredView.Dispose();

        _bag.Dispose();
    }
}