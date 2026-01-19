using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel : INotifyPropertyChanged
{
    private readonly ISimulatorStateService _state;
    private readonly ObservableCollection<SimulatedPeerRowViewModel> _peers = new();
    private readonly AsyncRelayCommand _removeSelectedPeerCommand;

    private string? _newPeerDisplayName;
    private string? _status;
    private SimulatedPeerRowViewModel? _selectedPeer;

    public HandshakeSimulatorViewModel(
        ISimulatorStateService state)
    {
        _state = state;

        Peers = new ReadOnlyObservableCollection<SimulatedPeerRowViewModel>(_peers);

        AddPeerCommand = new AsyncRelayCommand(ExecuteAddPeerAsync);
        _removeSelectedPeerCommand = new AsyncRelayCommand(ExecuteRemoveSelectedPeerAsync, _ => SelectedPeer is not null);
        RemoveSelectedPeerCommand = _removeSelectedPeerCommand;

        _ = InitializeAsync();
    }

    public string? NewPeerDisplayName
    {
        get => _newPeerDisplayName;
        set
        {
            if (string.Equals(_newPeerDisplayName, value, StringComparison.Ordinal)) return;
            _newPeerDisplayName = value;
            OnPropertyChanged(nameof(NewPeerDisplayName));
        }
    }

    public string? Status
    {
        get => _status;
        private set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal)) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
        }
    }

    public ReadOnlyObservableCollection<SimulatedPeerRowViewModel> Peers { get; }

    public SimulatedPeerRowViewModel? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (ReferenceEquals(_selectedPeer, value)) return;
            _selectedPeer = value;
            OnPropertyChanged(nameof(SelectedPeer));
            _removeSelectedPeerCommand.RaiseCanExecuteChanged();
        }
    }

    public ICommand AddPeerCommand { get; }
    public ICommand RemoveSelectedPeerCommand { get; }

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
        _peers.Clear();
        foreach (var p in _state.Peers)
        {
            _peers.Add(new SimulatedPeerRowViewModel(_state, p, RefreshPeers));
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
            Status = status;
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => Status = status).Task;
    }

    private async Task ExecuteAddPeerAsync(object? _)
    {
        try
        {
            await _state.AddPeerAsync(NewPeerDisplayName, CancellationToken.None);
            await Application.Current.Dispatcher.InvokeAsync(() => NewPeerDisplayName = null);
            await RefreshPeersOnUiAsync();
            await SetStatusOnUiAsync($"Peers: {_peers.Count}");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    private async Task ExecuteRemoveSelectedPeerAsync(object? _)
    {
        try
        {
            if (SelectedPeer is null) return;
            var id = SelectedPeer.PeerId;
            await _state.RemovePeerAsync(id, CancellationToken.None);
            await RefreshPeersOnUiAsync();
            await SetStatusOnUiAsync($"Peers: {_peers.Count}");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SimulatedPeerRowViewModel
{
    private readonly ISimulatorStateService _state;
    private readonly SimulatedPeerDto _peer;
    private readonly Action _refresh;

    public SimulatedPeerRowViewModel(ISimulatorStateService state, SimulatedPeerDto peer, Action refresh)
    {
        _state = state;
        _peer = peer;
        _refresh = refresh;
        ToggleOnlineCommand = new AsyncRelayCommand(ExecuteToggleOnlineAsync);
        ToggleRelayCapableCommand = new AsyncRelayCommand(ExecuteToggleRelayCapableAsync);
        RemoveCommand = new AsyncRelayCommand(ExecuteRemoveAsync);
    }

    public Guid PeerId => _peer.PeerId;
    public string DisplayText => string.IsNullOrWhiteSpace(_peer.DisplayName) ? _peer.PeerId.ToString()[..8] : _peer.DisplayName!;
    public bool IsOnline => _peer.IsOnline;
    public bool IsRelayCapable => _peer.Relay.IsRelayCapable;

    public ICommand ToggleOnlineCommand { get; }
    public ICommand ToggleRelayCapableCommand { get; }
    public ICommand RemoveCommand { get; }

    private async Task ExecuteToggleOnlineAsync(object? _)
    {
        await _state.ToggleOnlineAsync(_peer.PeerId, CancellationToken.None);
        await Application.Current.Dispatcher.InvokeAsync(_refresh);
    }

    private async Task ExecuteToggleRelayCapableAsync(object? _)
    {
        await _state.ToggleRelayCapableAsync(_peer.PeerId, CancellationToken.None);
        await Application.Current.Dispatcher.InvokeAsync(_refresh);
    }

    private async Task ExecuteRemoveAsync(object? _)
    {
        await _state.RemovePeerAsync(_peer.PeerId, CancellationToken.None);
        await Application.Current.Dispatcher.InvokeAsync(_refresh);
    }
}

