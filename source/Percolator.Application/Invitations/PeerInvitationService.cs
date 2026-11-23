namespace Percolator.Application.Invitations;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;

public sealed class PeerInvitationService : IPeerInvitationService
{
    private readonly IIdentityProfileRepository _profileRepo;
    private readonly ILocalIdentityKeyStore _identityKeyStore;
    private readonly IRelayDirectory _relayDirectory;

    public PeerInvitationService(
        IIdentityProfileRepository profileRepo,
        ILocalIdentityKeyStore identityKeyStore,
        IRelayDirectory relayDirectory)
    {
        _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
        _identityKeyStore = identityKeyStore ?? throw new ArgumentNullException(nameof(identityKeyStore));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
    }

    public async Task<DomainResult<PeerInvitation>> CreatePeerInvitationAsync(
        PublicIdentityId ownerId,
        RelayEndpoint relayEndpoint,
        CancellationToken ct = default)
    {
        if (!ownerId.IsValid)
        {
            return DomainResult<PeerInvitation>.Failure(new DomainError("INVALID_OWNER_ID", "Owner identity is invalid."));
        }
        if (relayEndpoint == null)
        {
            return DomainResult<PeerInvitation>.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "RelayEndpoint cannot be null."));
        }

        var profile = await _profileRepo.GetByIdAsync(ownerId, ct).ConfigureAwait(false);
        if (profile == null)
        {
            return DomainResult<PeerInvitation>.Failure(new DomainError("IDENTITY_NOT_FOUND", $"Identity profile {ownerId} not found."));
        }

        var identityKey = await _identityKeyStore.GetIdentityPublicKeyAsync(ownerId, ct).ConfigureAwait(false);
        if (identityKey == null)
        {
            return DomainResult<PeerInvitation>.Failure(new DomainError("KEY_NOT_FOUND", $"Identity key for {ownerId} not found."));
        }

        var relays = await _relayDirectory.ListAsync(ct).ConfigureAwait(false);
        var matchingEntry = relays.FirstOrDefault(r => r.Endpoint == relayEndpoint);
        if (matchingEntry == null)
        {
            return DomainResult<PeerInvitation>.Failure(new DomainError("EXCHANGE_NOT_CONFIGURED", $"Relay endpoint {relayEndpoint.Value} is not configured in relay directory."));
        }

        var triadResult = await _relayDirectory.GetMailboxTokensAsync(ownerId, matchingEntry.RelayIdentityId, ct).ConfigureAwait(false);
        if (triadResult.IsFailure)
        {
            return DomainResult<PeerInvitation>.Failure(new DomainError("NO_RELAY_MAILBOX_LINKED", $"Owner has no active mailbox linked on relay {relayEndpoint.Value}: {triadResult.Error.Description}"));
        }

        return PeerInvitation.Create(ownerId, identityKey, relayEndpoint, triadResult.Value.RoutingToken);
    }
}
