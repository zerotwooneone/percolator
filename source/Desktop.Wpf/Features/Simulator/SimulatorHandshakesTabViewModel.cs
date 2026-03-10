using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorHandshakesTabViewModel : IDisposable
{
    private readonly ISimulatedPeerDirectory _directory;
    private readonly ISimulatedPeerRuntimeService _runtime;
    private readonly ISimulatorRelayEmulator _relay;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly ISimulatorStateService _state;

    private readonly ObservableCollection<SimulatedHandshakeStateMachineCardViewModel> _cards = new();

    public SimulatorHandshakesTabViewModel(
        ISimulatedPeerDirectory directory,
        ISimulatedPeerRuntimeService runtime,
        ISimulatorRelayEmulator relay,
        IOptions<TransportOptions> transportOptions,
        Percolator.Application.Identity.ActiveIdentityContext active,
        ISimulatorStateService state)
    {
        _directory = directory;
        _runtime = runtime;
        _relay = relay;
        _transportOptions = transportOptions;
        _active = active;
        _state = state;

        Cards = new ReadOnlyObservableCollection<SimulatedHandshakeStateMachineCardViewModel>(_cards);

        _ = InitializeAsync();
    }

    public ReadOnlyObservableCollection<SimulatedHandshakeStateMachineCardViewModel> Cards { get; }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        await _directory.InitializeAsync(ct).ConfigureAwait(false);
        await _state.InitializeAsync(ct).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ResetCards();
            HookDirectory();
        }
        else
        {
            await dispatcher.InvokeAsync(() =>
            {
                ResetCards();
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

    private void HookDirectory()
    {
        var notify = (INotifyCollectionChanged)_directory.Peers;
        notify.CollectionChanged -= OnPeersChanged;
        notify.CollectionChanged += OnPeersChanged;
    }

    private void OnPeersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(() => OnPeersChanged(sender, e));
            return;
        }

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
            transportOptions: _transportOptions,
            active: _active,
            state: _state);
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
    }
}
