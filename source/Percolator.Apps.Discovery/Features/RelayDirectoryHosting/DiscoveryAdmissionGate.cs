using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

public sealed class DiscoveryAdmissionGate : IDiscoveryAdmissionGate
{
    public DomainResult EvaluateAdmission(
        PublicIdentityId candidateId,
        RelayDiscoveryPolicy relayPolicy,
        PeerDiscoveryVisibility peerVisibility,
        bool hasAuthenticatedHandshake,
        bool hasActiveSignedPreKey)
    {
        if (!candidateId.IsValid)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_CANDIDATE_ID", "Candidate peer identity cannot be uninitialized or empty."));
        }

        if (relayPolicy == RelayDiscoveryPolicy.Disabled)
        {
            return DomainResult.Failure(new DomainError(
                "RELAY_DISCOVERY_DISABLED", "The relay has discovery services disabled."));
        }

        if (relayPolicy == RelayDiscoveryPolicy.BlindedContactsOnly)
        {
            return DomainResult.Failure(new DomainError(
                "OPEN_DISCOVERY_PROHIBITED",
                "Open directory listing is prohibited when relay policy is BlindedContactsOnly."));
        }

        if (peerVisibility == PeerDiscoveryVisibility.Hidden)
        {
            return DomainResult.Failure(new DomainError(
                "PEER_HIDDEN", "Candidate peer has set discoverability to Hidden."));
        }

        if (!hasAuthenticatedHandshake)
        {
            return DomainResult.Failure(new DomainError(
                "HANDSHAKE_MISSING", "Candidate peer has not established an authenticated handshake with the relay."));
        }

        if (!hasActiveSignedPreKey)
        {
            return DomainResult.Failure(new DomainError(
                "SIGNED_PREKEY_MISSING", "Candidate peer does not have an active, valid Signed PreKey available."));
        }

        return DomainResult.Success();
    }
}
