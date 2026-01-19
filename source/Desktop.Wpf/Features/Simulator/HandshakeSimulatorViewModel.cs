using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Desktop.Wpf.Shared.Mvvm;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel : IDisposable
{
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorRuntimeStateService _runtime;
    private readonly ObservableCollection<SimulatedPeerRowViewModel> _peers = new();
    private DisposableBag _bag;

    public HandshakeSimulatorViewModel(
        ISimulatorStateService state,
        ISimulatorRuntimeStateService runtime)
    {
        _state = state;
        _runtime = runtime;

        Peers = new ReadOnlyObservableCollection<SimulatedPeerRowViewModel>(_peers);

        NewPeerDisplayName = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        Status = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        SelectedPeer = new BindableReactiveProperty<SimulatedPeerRowViewModel?>(null).AddTo(ref _bag);

        var addPeerCommand = NewPeerDisplayName
            .Select(name => !string.IsNullOrWhiteSpace(name))
            .ToReactiveCommand<Unit>(_ => { });
        addPeerCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteAddPeerAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        AddPeerCommand = addPeerCommand.AddTo(ref _bag);

        var removeSelectedPeerCommand = SelectedPeer
            .Select(peer => peer is not null)
            .ToReactiveCommand<Unit>(_ => { });
        removeSelectedPeerCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteRemoveSelectedPeerAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        RemoveSelectedPeerCommand = removeSelectedPeerCommand.AddTo(ref _bag);

        _ = InitializeAsync();
    }

    public BindableReactiveProperty<string?> NewPeerDisplayName { get; }

    public BindableReactiveProperty<string?> Status { get; }

    public ReadOnlyObservableCollection<SimulatedPeerRowViewModel> Peers { get; }

    public BindableReactiveProperty<SimulatedPeerRowViewModel?> SelectedPeer { get; }

    public ReactiveCommand<Unit> AddPeerCommand { get; }
    public ReactiveCommand<Unit> RemoveSelectedPeerCommand { get; }

    private async Task InitializeAsync()
    {
        try
        {
            await _state.InitializeAsync(CancellationToken.None);
            await RefreshPeersOnUiAsync();
            await SetStatusOnUiAsync($"Loaded {_peers.Count} peers");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    private void RefreshPeers()
    {
        foreach (var p in _peers)
        {
            p.Dispose();
        }

        _peers.Clear();
        foreach (var p in _state.Peers)
        {
            _peers.Add(new SimulatedPeerRowViewModel(_state, _runtime, p, RefreshPeers));
        }
    }

    private Task RefreshPeersOnUiAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshPeers();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(RefreshPeers).Task;
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
            await _state.AddPeerAsync(NewPeerDisplayName.Value, ct);
            await Application.Current.Dispatcher.InvokeAsync(() => NewPeerDisplayName.Value = null);
            await RefreshPeersOnUiAsync();
            await SetStatusOnUiAsync($"Peers: {_peers.Count}");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    private async Task ExecuteRemoveSelectedPeerAsync(CancellationToken ct)
    {
        try
        {
            var selected = SelectedPeer.Value;
            if (selected is null) return;
            var id = selected.PeerId;
            await _state.RemovePeerAsync(id, ct);
            await RefreshPeersOnUiAsync();
            await SetStatusOnUiAsync($"Peers: {_peers.Count}");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var p in _peers)
        {
            p.Dispose();
        }
        _peers.Clear();
        _bag.Dispose();
    }
}

public sealed class SimulatedPeerRowViewModel : IDisposable
{
    private readonly ISimulatorStateService _state;
    private readonly ISimulatorRuntimeStateService _runtime;
    private readonly SimulatedPeerDto _peer;
    private readonly Action _refresh;
    private DisposableBag _bag;

    public SimulatedPeerRowViewModel(ISimulatorStateService state, ISimulatorRuntimeStateService runtime, SimulatedPeerDto peer, Action refresh)
    {
        _state = state;
        _runtime = runtime;
        _peer = peer;
        _refresh = refresh;

        var toggleOnlineCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { });
        toggleOnlineCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteToggleOnlineAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        ToggleOnlineCommand = toggleOnlineCommand.AddTo(ref _bag);

        var toggleRelayCapableCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { });
        toggleRelayCapableCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteToggleRelayCapableAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        ToggleRelayCapableCommand = toggleRelayCapableCommand.AddTo(ref _bag);

        var removeCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => { });
        removeCommand.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteRemoveAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        RemoveCommand = removeCommand.AddTo(ref _bag);

        MarkOutboundPendingCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => ExecuteMarkOutboundPending());

        MarkInboundPendingCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => ExecuteMarkInboundPending());

        MarkEstablishedCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => ExecuteMarkEstablished());

        ClearRuntimeStateCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => ExecuteClearRuntimeState());

        MarkOutboundPendingCommand.AddTo(ref _bag);
        MarkInboundPendingCommand.AddTo(ref _bag);
        MarkEstablishedCommand.AddTo(ref _bag);
        ClearRuntimeStateCommand.AddTo(ref _bag);
    }

    public Guid PeerId => _peer.PeerId;
    public string DisplayText => string.IsNullOrWhiteSpace(_peer.DisplayName) ? _peer.PeerId.ToString()[..8] : _peer.DisplayName!;
    public bool IsOnline => _peer.IsOnline;
    public bool IsRelayCapable => _peer.Relay.IsRelayCapable;

    public string RuntimeStateText
    {
        get
        {
            var state = _runtime.Get(_peer.PeerId, _peer.IsOnline);
            return state.PendingCorrelationId is null
                ? state.UiState.ToString()
                : $"{state.UiState} ({state.PendingCorrelationId.Value.ToString()[..8]})";
        }
    }

    public ReactiveCommand<Unit> ToggleOnlineCommand { get; }
    public ReactiveCommand<Unit> ToggleRelayCapableCommand { get; }
    public ReactiveCommand<Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit> MarkOutboundPendingCommand { get; }
    public ReactiveCommand<Unit> MarkInboundPendingCommand { get; }
    public ReactiveCommand<Unit> MarkEstablishedCommand { get; }
    public ReactiveCommand<Unit> ClearRuntimeStateCommand { get; }

    private async Task ExecuteToggleOnlineAsync(CancellationToken ct)
    {
        await _state.ToggleOnlineAsync(_peer.PeerId, ct);
        await Application.Current.Dispatcher.InvokeAsync(_refresh);
    }

    private async Task ExecuteToggleRelayCapableAsync(CancellationToken ct)
    {
        await _state.ToggleRelayCapableAsync(_peer.PeerId, ct);
        await Application.Current.Dispatcher.InvokeAsync(_refresh);
    }

    private async Task ExecuteRemoveAsync(CancellationToken ct)
    {
        await _state.RemovePeerAsync(_peer.PeerId, ct);
        _runtime.Clear(_peer.PeerId);
        await Application.Current.Dispatcher.InvokeAsync(_refresh);
    }

    private Task ExecuteMarkOutboundPending()
    {
        _runtime.MarkOutboundPending(_peer.PeerId, Guid.NewGuid());
        return Application.Current.Dispatcher.InvokeAsync(_refresh).Task;
    }

    private Task ExecuteMarkInboundPending()
    {
        _runtime.MarkInboundPending(_peer.PeerId, Guid.NewGuid());
        return Application.Current.Dispatcher.InvokeAsync(_refresh).Task;
    }

    private Task ExecuteMarkEstablished()
    {
        _runtime.MarkEstablished(_peer.PeerId);
        return Application.Current.Dispatcher.InvokeAsync(_refresh).Task;
    }

    private Task ExecuteClearRuntimeState()
    {
        _runtime.Clear(_peer.PeerId);
        return Application.Current.Dispatcher.InvokeAsync(_refresh).Task;
    }

    public void Dispose()
    {
        _bag.Dispose();
    }
}

