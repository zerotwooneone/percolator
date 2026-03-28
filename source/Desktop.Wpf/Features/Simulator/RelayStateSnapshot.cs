namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record RelayStateSnapshot(
    Guid RelayHostPeerId,
    IReadOnlyList<OutboundRelayMessageSnapshot> UpstreamToMain,
    IReadOnlyList<InboundRelayMessageSnapshot> DownstreamToPeers);

public sealed record OutboundRelayMessageSnapshot(
    Guid AckId,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);

public sealed record InboundRelayMessageSnapshot(
    Guid AckId,
    byte[] TargetPkh,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);
