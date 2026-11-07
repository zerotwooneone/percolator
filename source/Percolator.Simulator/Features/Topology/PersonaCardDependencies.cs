using System;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.GroupMessaging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal sealed class PersonaCardDependencies
{
    public IStatusToastService ToastService { get; }
    public ISimulatorStateBus StateBus { get; }
    public DirectHandshakeOrchestrator HandshakeOrchestrator { get; }
    public DirectChatCoordinator ChatCoordinator { get; }
    public AutopilotPongResponder PongResponder { get; }
    public ISelectionStateService SelectionState { get; }
    public ISimulatedWorldStateService WorldState { get; }
    public ISimulatorLogSink LogSink { get; }
    public RelayProvisioningCoordinator RelayCoordinator { get; }
    public IFlightRecorderStateService? FlightRecorder { get; }
    public RichChatActionCoordinator RichChatCoordinator { get; }
    public ProfileSyncCoordinator ProfileCoordinator { get; }
    public GroupChatCoordinator GroupCoordinator { get; }

    public PersonaCardDependencies(
        IStatusToastService toastService,
        ISimulatorStateBus stateBus,
        DirectHandshakeOrchestrator handshakeOrchestrator,
        DirectChatCoordinator chatCoordinator,
        AutopilotPongResponder pongResponder,
        ISelectionStateService selectionState,
        ISimulatedWorldStateService worldState,
        ISimulatorLogSink logSink,
        RelayProvisioningCoordinator relayCoordinator,
        RichChatActionCoordinator richChatCoordinator,
        ProfileSyncCoordinator profileCoordinator,
        GroupChatCoordinator groupCoordinator,
        IFlightRecorderStateService? flightRecorder = null)
    {
        ToastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        StateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        HandshakeOrchestrator = handshakeOrchestrator ?? throw new ArgumentNullException(nameof(handshakeOrchestrator));
        ChatCoordinator = chatCoordinator ?? throw new ArgumentNullException(nameof(chatCoordinator));
        PongResponder = pongResponder ?? throw new ArgumentNullException(nameof(pongResponder));
        SelectionState = selectionState ?? throw new ArgumentNullException(nameof(selectionState));
        WorldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        LogSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        RelayCoordinator = relayCoordinator ?? throw new ArgumentNullException(nameof(relayCoordinator));
        RichChatCoordinator = richChatCoordinator ?? throw new ArgumentNullException(nameof(richChatCoordinator));
        ProfileCoordinator = profileCoordinator ?? throw new ArgumentNullException(nameof(profileCoordinator));
        GroupCoordinator = groupCoordinator ?? throw new ArgumentNullException(nameof(groupCoordinator));
        FlightRecorder = flightRecorder;
    }
}
