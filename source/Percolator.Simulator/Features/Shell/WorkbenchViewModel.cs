using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using ObservableCollections;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed class WorkbenchViewModel : ViewModelBase
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly PersonaCardDependencies _cardDependencies;

    public ObservableCollection<object> PinnedCardViewModels { get; } = new();

    public WorkbenchViewModel(
        ISimulatedWorldStateService worldState,
        PersonaCardDependencies cardDependencies)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _cardDependencies = cardDependencies ?? throw new ArgumentNullException(nameof(cardDependencies));

        // Initialize pinned cards from world state
        foreach (var card in _worldState.PinnedCards)
        {
            PinnedCardViewModels.Add(CreateCardViewModel(card));
        }

        _worldState.PinnedCards.ObserveChanged()
            .Subscribe(e =>
            {
                UiTaskRunner.PostToUi(() =>
                {
                    switch (e.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                            if (e.NewItem is not null)
                            {
                                var vm = CreateCardViewModel(e.NewItem);
                                PinnedCardViewModels.Add(vm);
                            }
                            break;
                        case NotifyCollectionChangedAction.Remove:
                            if (e.OldItem is not null)
                            {
                                var existing = PinnedCardViewModels.FirstOrDefault(vm =>
                                    (vm is PersonaCardViewModel p && p.NodeId == e.OldItem.Id) ||
                                    (vm is RelayCardViewModel r && r.DisplayName == e.OldItem.DisplayName));
                                if (existing is not null)
                                {
                                    PinnedCardViewModels.Remove(existing);
                                    (existing as IDisposable)?.Dispose();
                                }
                            }
                            break;
                        case NotifyCollectionChangedAction.Reset:
                            foreach (var card in PinnedCardViewModels)
                            {
                                (card as IDisposable)?.Dispose();
                            }
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
            SimulatedNodeType.Relay => new RelayCardViewModel(
                node,
                _cardDependencies.ToastService,
                _cardDependencies.StateBus,
                _worldState.GetRelayHost(node.Id),
                _cardDependencies.LogSink),
            _ => new PersonaCardViewModel(
                node,
                _cardDependencies,
                _worldState.GetPersonaHost(node.Id))
        };
    }

    public override void Dispose()
    {
        foreach (var card in PinnedCardViewModels)
        {
            (card as IDisposable)?.Dispose();
        }
        PinnedCardViewModels.Clear();
        base.Dispose();
    }
}
