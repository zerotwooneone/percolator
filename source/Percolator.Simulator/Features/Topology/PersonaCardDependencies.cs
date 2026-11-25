using System;
using Percolator.Application.Invitations;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.DiscoverySimulation;
using Percolator.Simulator.Features.Pairing;
using Percolator.Simulator.Features.GroupMessaging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.SwarmSimulation;

namespace Percolator.Simulator.Features.Topology;

internal sealed class PersonaCardDependencies
{
    public IStatusToastService ToastService { get; }
    public ISimulatorStateBus StateBus { get; }
    public QuickPairCoordinator QuickPairCoordinator { get; }
    public RelayChatCoordinator ChatCoordinator { get; }
    public AutopilotPongResponder PongResponder { get; }
    public ISelectionStateService SelectionState { get; }
    public ISimulatedWorldStateService WorldState { get; }
    public ISimulatorLogSink LogSink { get; }
    public RelayProvisioningCoordinator RelayCoordinator { get; }
    public IFlightRecorderStateService FlightRecorder { get; }
    public RichChatActionCoordinator RichChatCoordinator { get; }
    public ProfileSyncCoordinator ProfileCoordinator { get; }
    public GroupChatCoordinator GroupCoordinator { get; }
    public DiscoverySimulationCoordinator DiscoveryCoordinator { get; }
    public IVirtualClockStateService VirtualClock { get; }
    public SwarmSimulationCoordinator SwarmCoordinator { get; }
    public IPeerInvitationCodec InvitationCodec { get; }

    public PersonaCardDependencies(
        IStatusToastService toastService,
        ISimulatorStateBus stateBus,
        QuickPairCoordinator quickPairCoordinator,
        RelayChatCoordinator chatCoordinator,
        AutopilotPongResponder pongResponder,
        ISelectionStateService selectionState,
        ISimulatedWorldStateService worldState,
        ISimulatorLogSink logSink,
        RelayProvisioningCoordinator relayCoordinator,
        RichChatActionCoordinator richChatCoordinator,
        ProfileSyncCoordinator profileCoordinator,
        GroupChatCoordinator groupCoordinator,
        DiscoverySimulationCoordinator discoveryCoordinator,
        IVirtualClockStateService virtualClock,
        SwarmSimulationCoordinator swarmCoordinator,
        IFlightRecorderStateService flightRecorder,
        IPeerInvitationCodec invitationCodec)
    {
        ToastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        StateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        QuickPairCoordinator = quickPairCoordinator ?? throw new ArgumentNullException(nameof(quickPairCoordinator));
        ChatCoordinator = chatCoordinator ?? throw new ArgumentNullException(nameof(chatCoordinator));
        PongResponder = pongResponder ?? throw new ArgumentNullException(nameof(pongResponder));
        SelectionState = selectionState ?? throw new ArgumentNullException(nameof(selectionState));
        WorldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        LogSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        RelayCoordinator = relayCoordinator ?? throw new ArgumentNullException(nameof(relayCoordinator));
        RichChatCoordinator = richChatCoordinator ?? throw new ArgumentNullException(nameof(richChatCoordinator));
        ProfileCoordinator = profileCoordinator ?? throw new ArgumentNullException(nameof(profileCoordinator));
        GroupCoordinator = groupCoordinator ?? throw new ArgumentNullException(nameof(groupCoordinator));
        DiscoveryCoordinator = discoveryCoordinator ?? throw new ArgumentNullException(nameof(discoveryCoordinator));
        VirtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
        SwarmCoordinator = swarmCoordinator ?? throw new ArgumentNullException(nameof(swarmCoordinator));
        FlightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
        InvitationCodec = invitationCodec ?? throw new ArgumentNullException(nameof(invitationCodec));
    }
}
