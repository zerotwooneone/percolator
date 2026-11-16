using Percolator.Application.Invitations;
using Percolator.Domain.Identities;

namespace Percolator.Desktop.Features.Transports.Models;

public enum OutboundConnectionStatus
{
    Connected = 0,
    PendingOffline = 1,
    RelayAdded = 2,
    Cancelled = 3
}

public sealed record AddConnectionDialogResult(
    OutboundConnectionStatus Status,
    PeerInvitationPayload? Invitation = null,
    PublicIdentityId? PeerId = null,
    string? RelayEndpoint = null);
