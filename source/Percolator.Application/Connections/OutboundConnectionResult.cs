using System;
using Percolator.Application.Invitations;
using Percolator.Domain.Identities;

namespace Percolator.Application.Connections;

public sealed record OutboundConnectionResult(
    OutboundConnectionStatus Status,
    PublicIdentityId PeerId,
    PeerInvitationPayload Invitation,
    Uri? ResolvedEndpoint = null);
