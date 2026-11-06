using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using ObservableCollections;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed class WorkbenchViewModel : ViewModelBase
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorStateBus _stateBus;
    private readonly DirectHandshakeOrchestrator _handshakeOrchestrator;
    private readonly DirectChatCoordinator _chatCoordinator;
    private readonly AutopilotPongResponder _pongResponder;
    private readonly ISelectionStateService _selectionState;
    private readonly ISimulatorLogSink _logSink;
    private readonly IFlightRecorderStateService? _flightRecorder;

    public ObservableCollection<object> PinnedCardViewModels { get; } = new();

    public WorkbenchViewModel(
        ISimulatedWorldStateService worldState,
        IStatusToastService toastService,
        ISimulatorStateBus stateBus,
        DirectHandshakeOrchestrator handshakeOrchestrator,
        DirectChatCoordinator chatCoordinator,
        AutopilotPongResponder pongResponder,
        ISelectionStateService selectionState,
        ISimulatorLogSink logSink,
        IFlightRecorderStateService? flightRecorder = null)
    {
        _worldState = worldState;
        _toastService = toastService;
        _stateBus = stateBus;
        _handshakeOrchestrator = handshakeOrchestrator;
        _chatCoordinator = chatCoordinator;
        _pongResponder = pongResponder;
        _selectionState = selectionState;
        _logSink = logSink;
        _flightRecorder = flightRecorder;

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
            _ => new PersonaCardViewModel(
                node,
                _toastService,
                _stateBus,
                _handshakeOrchestrator,
                _chatCoordinator,
                _pongResponder,
                _selectionState,
                _worldState,
                _worldState.GetPersonaHost(node.Id),
                _logSink,
                _flightRecorder)
        };
    }
}
