using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;

public sealed record NearbyDiscoveryQueryPayload
{
    public PublicIdentityId RequesterId { get; }
    public int RequestedLimit { get; }

    public NearbyDiscoveryQueryPayload(PublicIdentityId requesterId, int requestedLimit)
    {
        if (!requesterId.IsValid)
        {
            throw new ArgumentException("RequesterId must be valid and non-empty.", nameof(requesterId));
        }

        RequesterId = requesterId;
        RequestedLimit = requestedLimit;
    }

    public static DomainResult<NearbyDiscoveryQueryPayload> Create(PublicIdentityId requesterId, int requestedLimit)
    {
        if (!requesterId.IsValid)
        {
            return DomainResult<NearbyDiscoveryQueryPayload>.Failure(new DomainError(
                "INVALID_REQUESTER_ID", "RequesterId must be valid and non-empty."));
        }

        return DomainResult<NearbyDiscoveryQueryPayload>.Success(new NearbyDiscoveryQueryPayload(requesterId, requestedLimit));
    }
}

public sealed record NearbyDiscoveryResponsePayload(
    IReadOnlyList<PeerDiscoveryDescriptor> Descriptors);
