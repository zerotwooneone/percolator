using Desktop.Wpf.Features.Simulator;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record SimulatorStateSnapshot(
    int Version,
    IReadOnlyList<PeerStateSnapshot> Peers,
    IReadOnlyList<PeerRelationshipSnapshot> Relationships,
    IReadOnlyList<RelayStateSnapshot> Relays,
    IReadOnlyList<GroupConversationDto> Groups);
