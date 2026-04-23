using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record RelayStateSnapshot(
    PeerId RelayHostPeerId,
    IReadOnlyList<OutboundRelayMessageSnapshot> UpstreamToMain,
    IReadOnlyList<InboundRelayMessageSnapshot> DownstreamToPeers);

public sealed record OutboundRelayMessageSnapshot(
    Guid AckId,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);

public sealed record InboundRelayMessageSnapshot(
    Guid AckId,
    byte[] TargetIdentityPublicKeyHash,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);
