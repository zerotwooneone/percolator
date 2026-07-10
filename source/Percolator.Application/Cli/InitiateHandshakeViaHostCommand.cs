using MediatR;
using Percolator.Identity;

namespace Percolator.Application.Cli;

public sealed record InitiateHandshakeViaHostCommand(
    string HostPeerName,
    PublicIdentityId TargetPublicIdentityId,
    string? PeerName = null,
    byte[]? InitiatorPayload = null
) : IRequest<Unit>;
