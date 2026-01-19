using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel : IDisposable
{
    private readonly ISimulatedPeerDirectory _directory;
    private readonly ObservableCollection<SimulatedPeerItemViewModel> _peers = new();
    private DisposableBag _bag;

    public HandshakeSimulatorViewModel(
        ISimulatedPeerDirectory directory)
    {
        _directory = directory;

        Peers = new ReadOnlyObservableCollection<SimulatedPeerItemViewModel>(_peers);

        NewPeerDisplayName = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        Status = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        SelectedPeer = new BindableReactiveProperty<SimulatedPeerItemViewModel?>(null).AddTo(ref _bag);

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

    public ReadOnlyObservableCollection<SimulatedPeerItemViewModel> Peers { get; }

    public BindableReactiveProperty<SimulatedPeerItemViewModel?> SelectedPeer { get; }

    public ReactiveCommand<Unit> AddPeerCommand { get; }
    public ReactiveCommand<Unit> RemoveSelectedPeerCommand { get; }

    private async Task InitializeAsync()
    {
        try
        {
            await _directory.InitializeAsync(CancellationToken.None);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ResetPeers();
                HookDirectory();
            }
            else
            {
                await dispatcher.InvokeAsync(() =>
                {
                    ResetPeers();
                    HookDirectory();
                });
            }

            await SetStatusOnUiAsync($"Loaded {_peers.Count} peers");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    private void ResetPeers()
    {
        foreach (var p in _peers)
        {
            p.Dispose();
        }

        _peers.Clear();
        foreach (var m in _directory.Peers)
        {
            _peers.Add(new SimulatedPeerItemViewModel(_directory, m));
        }
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
            ResetPeers();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (var oldItem in e.OldItems.OfType<SimulatedPeerModel>())
            {
                var existing = _peers.FirstOrDefault(x => x.PeerId == oldItem.PeerId);
                if (existing is null) continue;
                if (ReferenceEquals(SelectedPeer.Value, existing)) SelectedPeer.Value = null;
                _peers.Remove(existing);
                existing.Dispose();
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var newItem in e.NewItems.OfType<SimulatedPeerModel>())
            {
                _peers.Add(new SimulatedPeerItemViewModel(_directory, newItem));
            }
        }

        _ = SetStatusOnUiAsync($"Peers: {_peers.Count}");
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
            _ = await _directory.AddPeerAsync(NewPeerDisplayName.Value, ct);
            await Application.Current.Dispatcher.InvokeAsync(() => NewPeerDisplayName.Value = null);
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
            await _directory.RemovePeerAsync(id, ct);
            await SetStatusOnUiAsync($"Peers: {_peers.Count}");
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        var notify = (INotifyCollectionChanged)_directory.Peers;
        notify.CollectionChanged -= OnDirectoryPeersChanged;

        foreach (var p in _peers) p.Dispose();
        _peers.Clear();

        _bag.Dispose();
    }
}

