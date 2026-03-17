using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorHandshakesTabViewModel : IDisposable
{
    private readonly ISimulatedPeerDirectory _directory;
    private readonly ISimulatedPeerRuntimeService _runtime;
    private readonly ISimulatorRelayEmulator _relay;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ISimulatorMainIngressService _mainIngress;

    private readonly ObservableCollection<SimulatedHandshakeStateMachineCardViewModel> _cards = new();
    private readonly ObservableCollection<RelayHostOption> _relayHosts = new();

    private readonly Dictionary<Guid, IDisposable> _relayCapableSubscriptions = new();

    public SimulatorHandshakesTabViewModel(
        ISimulatedPeerDirectory directory,
        ISimulatedPeerRuntimeService runtime,
        ISimulatorRelayEmulator relay,
        ISimulatorMainIngressService mainIngress,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        ISimulatorStateService state,
        ISimulatorDiagnosticsService diagnostics)
    {
        _directory = directory;
        _runtime = runtime;
        _relay = relay;
        _mainIngress = mainIngress;
        _transportOptions = transportOptions;
        _active = active;
        _state = state;
        _diagnostics = diagnostics;

        Cards = new ReadOnlyObservableCollection<SimulatedHandshakeStateMachineCardViewModel>(_cards);
        RelayHosts = new ReadOnlyObservableCollection<RelayHostOption>(_relayHosts);
        SelectedRelayHostPeerId = new BindableReactiveProperty<Guid?>(null);

        _ = InitializeAsync();
    }

    public sealed record RelayHostOption(Guid PeerId, string DisplayName);

    public ReadOnlyObservableCollection<SimulatedHandshakeStateMachineCardViewModel> Cards { get; }

    public ReadOnlyObservableCollection<RelayHostOption> RelayHosts { get; }

    public BindableReactiveProperty<Guid?> SelectedRelayHostPeerId { get; }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        await _state.InitializeAsync(ct).ConfigureAwait(false);
        await _directory.InitializeAsync(ct).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ResetCards();
            RefreshRelayHosts();
            HookDirectory();
        }
        else
        {
            await dispatcher.InvokeAsync(() =>
            {
                ResetCards();
                RefreshRelayHosts();
                HookDirectory();
            });
        }
    }

    private void ResetCards()
    {
        foreach (var c in _cards)
        {
            c.Dispose();
        }
        _cards.Clear();

        foreach (var p in _directory.Peers)
        {
            _cards.Add(CreateCard(p));
        }
    }

    private void RefreshRelayHosts()
    {
        _relayHosts.Clear();

        foreach (var p in _directory.Peers.Where(x => x.IsRelayCapable.CurrentValue))
        {
            var name = p.DisplayName.CurrentValue;
            name = string.IsNullOrWhiteSpace(name) ? p.PeerId.ToString()[..8] : name;
            _relayHosts.Add(new RelayHostOption(p.PeerId, name!));
        }

        if (SelectedRelayHostPeerId.Value is not null
            && _relayHosts.All(x => x.PeerId != SelectedRelayHostPeerId.Value.Value))
        {
            SelectedRelayHostPeerId.Value = null;
        }
    }

    private void HookDirectory()
    {
        var notify = (INotifyCollectionChanged)_directory.Peers;
        notify.CollectionChanged -= OnPeersChanged;
        notify.CollectionChanged += OnPeersChanged;

        WireRelayCapabilitySubscriptions();
    }

    private void WireRelayCapabilitySubscriptions()
    {
        foreach (var d in _relayCapableSubscriptions.Values)
        {
            d.Dispose();
        }
        _relayCapableSubscriptions.Clear();

        foreach (var peer in _directory.Peers)
        {
            var sub = peer.IsRelayCapable
                .DistinctUntilChanged()
                .Subscribe(_ => OnRelayCapabilityChanged());
            _relayCapableSubscriptions[peer.PeerId] = sub;
        }
    }

    private void OnRelayCapabilityChanged()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(OnRelayCapabilityChanged);
            return;
        }

        RefreshRelayHosts();
    }

    private void OnPeersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(() => OnPeersChanged(sender, e));
            return;
        }

        WireRelayCapabilitySubscriptions();
        RefreshRelayHosts();

        if (e.Action is NotifyCollectionChangedAction.Reset)
        {
            ResetCards();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (var oldItem in e.OldItems.OfType<SimulatedPeerModel>())
            {
                var existing = _cards.FirstOrDefault(x => x.PeerId == oldItem.PeerId);
                if (existing is null) continue;
                _cards.Remove(existing);
                existing.Dispose();
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var newItem in e.NewItems.OfType<SimulatedPeerModel>())
            {
                _cards.Add(CreateCard(newItem));
            }
        }
    }

    private SimulatedHandshakeStateMachineCardViewModel CreateCard(SimulatedPeerModel model)
    {
        return new SimulatedHandshakeStateMachineCardViewModel(
            model: model,
            runtime: _runtime,
            relay: _relay,
            mainIngress: _mainIngress,
            diagnostics: _diagnostics,
            transportOptions: _transportOptions,
            active: _active,
            state: _state,
            selectedRelayHostPeerId: () => SelectedRelayHostPeerId.Value);
    }

    public void Dispose()
    {
        foreach (var c in _cards)
        {
            c.Dispose();
        }
        _cards.Clear();

        try
        {
            var notify = (INotifyCollectionChanged)_directory.Peers;
            notify.CollectionChanged -= OnPeersChanged;
        }
        catch
        {
        }

        foreach (var d in _relayCapableSubscriptions.Values)
        {
            d.Dispose();
        }
        _relayCapableSubscriptions.Clear();
    }
}
