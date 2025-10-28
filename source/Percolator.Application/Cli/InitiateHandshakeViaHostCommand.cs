using MediatR;

namespace Percolator.Application.Cli;

public sealed record InitiateHandshakeViaHostCommand(
    string HostPeerName,
    byte[] TargetPublicKeyHash,
    string? PeerName = null,
    byte[]? InitiatorPayload = null
) : IRequest<Unit>;
