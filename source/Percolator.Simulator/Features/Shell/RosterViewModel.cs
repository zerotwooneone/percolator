using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    public RosterViewModel(
        ISimulatedWorldStateService worldState,
        ISelectionStateService selectionState,
        IStatusToastService toastService)
    {
        _worldState = worldState;
        _selectionState = selectionState;
        _toastService = toastService;

        // Bind personas and relays
        SyncCollection(_worldState.Personas, Personas);
        SyncCollection(_worldState.Relays, Relays);
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
        _toastService.ShowPlaceholder("Listening/Muted Toggle", "Phase 3A");
    }

    [RelayCommand]
    private void SelectGroup()
    {
        _toastService.ShowPlaceholder("Group Inspector", "Phase 7");
    }

    [RelayCommand]
    private void SelectSwarm()
    {
        _toastService.ShowPlaceholder("Swarm Heatmap", "Phase 9");
    }
}
