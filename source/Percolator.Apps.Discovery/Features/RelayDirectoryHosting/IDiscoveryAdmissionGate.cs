using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Gatekeeper evaluating whether a peer is admissible for discovery listing.
/// Enforces relay policy, peer visibility preference, authenticated handshake, and active Signed PreKey (SPK).
/// </summary>
public interface IDiscoveryAdmissionGate
{
    DomainResult EvaluateAdmission(
        PublicIdentityId candidateId,
        RelayDiscoveryPolicy relayPolicy,
        PeerDiscoveryVisibility peerVisibility,
        bool hasAuthenticatedHandshake,
        bool hasActiveSignedPreKey);
}
