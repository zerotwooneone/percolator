using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Inbound application DTO for registering or updating discovery visibility on a hosting peer/relay.
/// Invariants:
/// - Target PeerId must be valid.
/// - Ingress pipeline ensures this payload was sent by the authenticated session owner (anti-impersonation).
/// - When discoverable, the peer re-advertises its mailbox tokens so the relay can check
///   hosted pre-key availability and include the tokens in query responses.
/// </summary>
public sealed record DiscoveryRegistrationPayload
{
    public PublicIdentityId PeerId { get; }
    public PeerDiscoveryVisibility Visibility { get; }
    public BlindedRoutingToken? RoutingToken { get; }
    public DeliveryToken? DeliveryToken { get; }

    public DiscoveryRegistrationPayload(
        PublicIdentityId peerId,
        PeerDiscoveryVisibility visibility,
        BlindedRoutingToken? routingToken = null,
        DeliveryToken? deliveryToken = null)
    {
        if (!peerId.IsValid)
        {
            throw new ArgumentException("PeerId must be valid and non-empty.", nameof(peerId));
        }

        PeerId = peerId;
        Visibility = visibility;
        RoutingToken = routingToken;
        DeliveryToken = deliveryToken;
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
