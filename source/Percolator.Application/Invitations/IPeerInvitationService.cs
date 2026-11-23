namespace Percolator.Application.Invitations;

using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

public interface IPeerInvitationService
{
    Task<DomainResult<PeerInvitation>> CreatePeerInvitationAsync(
        PublicIdentityId ownerId,
        RelayEndpoint relayEndpoint,
        CancellationToken ct = default);
}
