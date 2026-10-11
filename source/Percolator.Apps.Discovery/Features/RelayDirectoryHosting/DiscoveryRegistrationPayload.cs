using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Inbound application DTO for registering or updating discovery visibility on a hosting peer/relay.
/// Invariants:
/// - Target PeerId must be valid.
/// - Ingress pipeline ensures this payload was sent by the authenticated session owner (anti-impersonation).
/// </summary>
public sealed record DiscoveryRegistrationPayload
{
    public PublicIdentityId PeerId { get; }
    public PeerDiscoveryVisibility Visibility { get; }

    public DiscoveryRegistrationPayload(PublicIdentityId peerId, PeerDiscoveryVisibility visibility)
    {
        if (!peerId.IsValid)
        {
            throw new ArgumentException("PeerId must be valid and non-empty.", nameof(peerId));
        }

        PeerId = peerId;
        Visibility = visibility;
    }

    public static DomainResult<DiscoveryRegistrationPayload> Create(
        PublicIdentityId peerId,
        PeerDiscoveryVisibility visibility)
    {
        if (!peerId.IsValid)
        {
            return DomainResult<DiscoveryRegistrationPayload>.Failure(new DomainError(
                "INVALID_PEER_ID", "PeerId must be valid and non-empty."));
        }

        return DomainResult<DiscoveryRegistrationPayload>.Success(new DiscoveryRegistrationPayload(peerId, visibility));
    }
}
