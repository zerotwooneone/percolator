using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObservableCollections;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class WorkbenchViewModel : ViewModelBase
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IStatusToastService _toastService;

    public ObservableCollection<object> PinnedCardViewModels { get; } = new();

    public WorkbenchViewModel(
        ISimulatedWorldStateService worldState,
        IStatusToastService toastService)
    {
        _worldState = worldState;
        _toastService = toastService;

        // Initialize pinned cards from world state
        foreach (var card in _worldState.PinnedCards)
        {
            PinnedCardViewModels.Add(CreateCardViewModel(card));
        }

        _worldState.PinnedCards.ObserveChanged()
            .Subscribe(e =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    switch (e.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                            if (e.NewItem is not null)
                                PinnedCardViewModels.Add(CreateCardViewModel(e.NewItem));
                            break;
                        case NotifyCollectionChangedAction.Remove:
                            if (e.OldItem is not null)
                            {
                                var existing = PinnedCardViewModels.FirstOrDefault(vm =>
                                    (vm is PersonaCardViewModel p && p.DisplayName == e.OldItem.DisplayName) ||
                                    (vm is RelayCardViewModel r && r.DisplayName == e.OldItem.DisplayName));
                                if (existing is not null)
                                    PinnedCardViewModels.Remove(existing);
                            }
                            break;
                        case NotifyCollectionChangedAction.Reset:
                            PinnedCardViewModels.Clear();
                            break;
                    }
                });
            })
            .AddTo(ref Disposables);
    }

    private object CreateCardViewModel(SimulatedNodeSummary node)
    {
        return node.NodeType switch
        {
            SimulatedNodeType.Relay => new RelayCardViewModel(node, _toastService),
            _ => new PersonaCardViewModel(node, _toastService)
        };
    }
}
