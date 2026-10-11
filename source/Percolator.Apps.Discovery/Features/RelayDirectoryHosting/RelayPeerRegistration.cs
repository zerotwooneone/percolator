using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Internal record stored on the hosting relay/node tracking a peer's discoverability state.
/// </summary>
public sealed record RelayPeerRegistration(
    PublicIdentityId PeerId,
    PeerDiscoveryVisibility Visibility,
    bool HasAuthenticatedHandshake,
    bool HasActiveSignedPreKey);
