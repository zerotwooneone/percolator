using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObservableCollections;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class RosterViewModel : ViewModelBase
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISelectionStateService _selectionState;
    private readonly IStatusToastService _toastService;

    public ObservableCollection<SimulatedNodeSummary> Personas { get; } = new();
    public ObservableCollection<SimulatedNodeSummary> Relays { get; } = new();
    public ObservableCollection<SimulatedNodeSummary> Groups { get; } = new();
    public ObservableCollection<SimulatedNodeSummary> Swarms { get; } = new();

    public RosterViewModel(
        ISimulatedWorldStateService worldState,
        ISelectionStateService selectionState,
        IStatusToastService toastService)
    {
        _worldState = worldState;
        _selectionState = selectionState;
        _toastService = toastService;

        // Bind personas, relays, groups, and swarms
        SyncCollection(_worldState.Personas, Personas);
        SyncCollection(_worldState.Relays, Relays);
        SyncCollection(_worldState.Groups, Groups);
        SyncCollection(_worldState.ActiveSwarms, Swarms);
    }

    private void SyncCollection(IReadOnlyObservableList<SimulatedNodeSummary> source, ObservableCollection<SimulatedNodeSummary> target)
    {
        foreach (var item in source)
        {
            target.Add(item);
        }

        source.ObserveChanged()
            .Subscribe(e =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    switch (e.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                            if (e.NewItem is not null) target.Add(e.NewItem);
                            break;
                        case NotifyCollectionChangedAction.Remove:
                            if (e.OldItem is not null) target.Remove(e.OldItem);
                            break;
                        case NotifyCollectionChangedAction.Reset:
                            target.Clear();
                            break;
                    }
                });
            })
            .AddTo(ref Disposables);
    }

    [RelayCommand]
    public void TogglePin(SimulatedNodeSummary node)
    {
        _worldState.TogglePinCard(node);
    }

    [RelayCommand]
    public void SelectPrimary(SimulatedNodeSummary node)
    {
        _selectionState.SetPrimary(node);
    }

    [RelayCommand]
    public void SelectSecondary(SimulatedNodeSummary node)
    {
        _selectionState.SetSecondary(node);
    }

    [RelayCommand]
    public void ToggleMute(SimulatedNodeSummary node)
    {
        var host = _worldState.GetPersonaHost(node.Id);
        if (host != null)
        {
            var newState = !host.IsListening.CurrentValue;
            UiTaskRunner.FireAndForget(() => host.SetListeningAsync(newState), "HostLifecycle");
            _toastService.ShowToast("Ingress Toggled", $"{node.DisplayName} is now {(newState ? "listening" : "muted")}.", ToastSeverity.Informational);
        }
    }

    [RelayCommand]
    private void SelectGroup()
    {
        var group = _worldState.Groups.FirstOrDefault();
        if (group != null)
        {
            _worldState.TogglePinCard(group);
            _toastService.ShowToast("Group Inspector", $"Pinned group '{group.DisplayName}' to workbench.", ToastSeverity.Informational);
        }
        else
        {
            _toastService.ShowToast("No Groups", "Create a group first using '👥 Create Group on Relay...' in the action bar.", ToastSeverity.Warning);
        }
    }

    [RelayCommand]
    private void SelectSwarm()
    {
        var swarm = _worldState.ActiveSwarms.FirstOrDefault();
        if (swarm != null)
        {
            _worldState.TogglePinCard(swarm);
            _toastService.ShowToast("Swarm Heatmap", $"Pinned swarm '{swarm.DisplayName}' to workbench.", ToastSeverity.Informational);
        }
        else
        {
            _toastService.ShowToast("No Active Swarm", "Initiate a file transfer preset from the action bar first.", ToastSeverity.Warning);
        }
    }
}
