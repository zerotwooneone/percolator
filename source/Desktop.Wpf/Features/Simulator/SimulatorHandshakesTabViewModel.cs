using Desktop.Wpf.Shared.Mvvm;
using Microsoft.Extensions.Options;
using ObservableCollections;
using Percolator.Application.Configuration;
using Percolator.Network;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorHandshakesTabViewModel : IDisposable
{
    private readonly IUiDispatcher _ui;

    private readonly ISimulatorInitializer _simulatorInitializer;
    private readonly ISimulatorToMainTransportService _mainIngress;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorDiagnosticsService _diagnostics;

    private ISynchronizedView<SimulatedPeerModel, SimulatedHandshakeStateMachineCardViewModel>? _cards;
    private readonly NotifyCollectionChangedSynchronizedViewList<SimulatedHandshakeStateMachineCardViewModel> _cardsNotify;

    private readonly ObservableList<RelayHostOption> _relayHosts = new();
    private readonly NotifyCollectionChangedSynchronizedViewList<RelayHostOption> _relayHostsNotify;

    private readonly object _peerRelaySubGate = new();
    private IReadOnlyDictionary<NetworkPeerId, IDisposable> _peerRelaySubs = new Dictionary<NetworkPeerId, IDisposable>();

    private DisposableBag _bag;

    public SimulatorHandshakesTabViewModel(
        ISimulatorInitializer simulatorInitializer,
        ISimulatorToMainTransportService mainIngress,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        ISimulatorStateService state,
        ISimulatorDiagnosticsService diagnostics,
        IUiDispatcher ui)
    {
        _ui = ui;
        _simulatorInitializer = simulatorInitializer;
        _mainIngress = mainIngress;
        _transportOptions = transportOptions;
        _active = active;
        _state = state;
        _diagnostics = diagnostics;

        SelectedRelayHostPeerId = new BindableReactiveProperty<NetworkPeerId?>(null).AddTo(ref _bag);

        _relayHostsNotify = _relayHosts.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);

        _cards = _state.Peers
            .CreateView(CreateCard)
            .AddTo(ref _bag);
        _cardsNotify = _cards.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);

        HookRelayHosts();
    }

    public sealed record RelayHostOption(NetworkPeerId NetworkPeerId, string DisplayName);

    public NotifyCollectionChangedSynchronizedViewList<SimulatedHandshakeStateMachineCardViewModel> Cards => _cardsNotify;

    public NotifyCollectionChangedSynchronizedViewList<RelayHostOption> RelayHosts => _relayHostsNotify;

    public BindableReactiveProperty<NetworkPeerId?> SelectedRelayHostPeerId { get; }

    private void HookRelayHosts()
    {
        var peers = _state.Peers;
        var peersChanged = peers.ObserveChanged();

        peersChanged
            .SubscribeAwait(async (_, ct) =>
            {
                await _ui.InvokeAsync(RewirePeerRelaySubscriptionsOnUi, ct).ConfigureAwait(false);
                await _ui.InvokeAsync(RebuildRelayHostsOnUi, ct).ConfigureAwait(false);
            }, AwaitOperation.Drop)
            .AddTo(ref _bag);

        _ = _ui.InvokeAsync(() =>
        {
            RewirePeerRelaySubscriptionsOnUi();
            RebuildRelayHostsOnUi();
        }, CancellationToken.None);
    }

    private void RewirePeerRelaySubscriptionsOnUi()
    {
        IReadOnlyDictionary<NetworkPeerId, IDisposable> prev;
        lock (_peerRelaySubGate)
        {
            prev = _peerRelaySubs;
            _peerRelaySubs = new Dictionary<NetworkPeerId, IDisposable>();
        }

        foreach (var d in prev.Values)
        {
            try { d.Dispose(); } catch { }
        }

        var next = new Dictionary<NetworkPeerId, IDisposable>();
        foreach (var peer in _state.Peers)
        {
            // RelayHosts needs to update when IsRelayCapable toggles or display name changes.
            var relayChanged = peer.IsRelayCapable.DistinctUntilChanged().Select(static _ => Unit.Default);
            var nameChanged = peer.DisplayName.DistinctUntilChanged().Select(static _ => Unit.Default);

            var sub = Observable.Merge(relayChanged, nameChanged)
                .SubscribeAwait(async (_, __) => await _ui.InvokeAsync(RebuildRelayHostsOnUi, CancellationToken.None));

            next[peer.NetworkPeerId] = sub;
        }

        lock (_peerRelaySubGate)
        {
            foreach (var d in _peerRelaySubs.Values)
            {
                try { d.Dispose(); } catch { }
            }
            _peerRelaySubs = next;
        }
    }

    private void RebuildRelayHostsOnUi()
    {
        _relayHosts.Clear();

        foreach (var p in _state.Peers.Where(static x => x.IsRelayCapable.CurrentValue))
        {
            var name = p.DisplayName.CurrentValue;
            name = string.IsNullOrWhiteSpace(name) ? p.NetworkPeerId.ToString()[..8] : name;
            _relayHosts.Add(new RelayHostOption(p.NetworkPeerId, name!));
        }

        if (SelectedRelayHostPeerId.Value is not null
            && _relayHosts.All(x => x.NetworkPeerId.Value != SelectedRelayHostPeerId.Value.Value.Value))
        {
            SelectedRelayHostPeerId.Value = null;
        }
    }

    private SimulatedHandshakeStateMachineCardViewModel CreateCard(SimulatedPeerModel model)
    {
        return new SimulatedHandshakeStateMachineCardViewModel(
            model: model,
            state: _state,
            mainIngress: _mainIngress,
            diagnostics: _diagnostics,
            transportOptions: _transportOptions,
            active: _active,
            selectedRelayHostPeerId: () => SelectedRelayHostPeerId.Value);
    }

    public void Dispose()
    {
        _cards.Dispose();
        _cardsNotify.Dispose();
        _relayHostsNotify.Dispose();

        lock (_peerRelaySubGate)
        {
            foreach (var d in _peerRelaySubs.Values)
            {
                try { d.Dispose(); } catch { }
            }
            _peerRelaySubs = new Dictionary<NetworkPeerId, IDisposable>();
        }

        _bag.Dispose();
    }
}
