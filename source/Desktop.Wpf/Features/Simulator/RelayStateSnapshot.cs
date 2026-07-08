using Percolator.Identity;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record RelayStateSnapshot(
    Percolator.Network.NetworkPeerId RelayHostNetworkPeerId,
    IReadOnlyList<OutboundRelayMessageSnapshot> UpstreamToMain,
    IReadOnlyList<InboundRelayMessageSnapshot> DownstreamToPeers);

public sealed record OutboundRelayMessageSnapshot(
    Guid AckId,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);

public sealed record InboundRelayMessageSnapshot(
    Guid AckId,
    IdentityPublicKeyHash TargetIdentityPublicKeyHash,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);
