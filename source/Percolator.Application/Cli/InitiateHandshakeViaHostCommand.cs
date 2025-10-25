using MediatR;

namespace Percolator.Application.Cli;

public sealed record InitiateHandshakeViaHostCommand(
    string HostPeerName,
    byte[] TargetPublicKeyHash,
    byte[]? InitiatorPayload = null
) : IRequest<Unit>;
