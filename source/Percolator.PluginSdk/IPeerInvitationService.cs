namespace Percolator.PluginSdk;

using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Host service enabling plugins to create single-use or reusable peer invitations
/// pinned to a local persona and designated relay endpoint.
/// </summary>
public interface IPeerInvitationService
{
    Task<DomainResult<PeerInvitation>> CreatePeerInvitationAsync(
        PublicIdentityId ownerId,
        RelayEndpoint relayEndpoint,
        CancellationToken ct = default);
}
