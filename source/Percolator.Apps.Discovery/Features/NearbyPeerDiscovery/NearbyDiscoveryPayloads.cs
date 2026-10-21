using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;

public sealed record NearbyDiscoveryQueryPayload
{
    public PublicIdentityId RequesterId { get; }
    public DiscoveryResultLimit Limit { get; }
    public int RequestedLimit => Limit.Value;

    public NearbyDiscoveryQueryPayload(PublicIdentityId requesterId, DiscoveryResultLimit limit)
    {
        if (!requesterId.IsValid)
        {
            throw new ArgumentException("RequesterId must be valid and non-empty.", nameof(requesterId));
        }

        RequesterId = requesterId;
        Limit = limit.Value is >= DiscoveryResultLimit.Min and <= DiscoveryResultLimit.Max
            ? limit
            : DiscoveryResultLimit.Default;
    }

    public NearbyDiscoveryQueryPayload(PublicIdentityId requesterId, int requestedLimit)
        : this(requesterId, DiscoveryResultLimit.Clamp(requestedLimit))
    {
    }

    public static DomainResult<NearbyDiscoveryQueryPayload> Create(
        PublicIdentityId requesterId,
        DiscoveryResultLimit limit)
    {
        if (!requesterId.IsValid)
        {
            return DomainResult<NearbyDiscoveryQueryPayload>.Failure(new DomainError(
                "INVALID_REQUESTER_ID", "RequesterId must be valid and non-empty."));
        }

        var limitCheck = DiscoveryResultLimit.Create(limit.Value);
        if (limitCheck.IsFailure)
        {
            return DomainResult<NearbyDiscoveryQueryPayload>.Failure(limitCheck.Error);
        }

        return DomainResult<NearbyDiscoveryQueryPayload>.Success(new NearbyDiscoveryQueryPayload(requesterId, limit));
    }

    public static DomainResult<NearbyDiscoveryQueryPayload> Create(PublicIdentityId requesterId, int requestedLimit)
    {
        if (!requesterId.IsValid)
        {
            return DomainResult<NearbyDiscoveryQueryPayload>.Failure(new DomainError(
                "INVALID_REQUESTER_ID", "RequesterId must be valid and non-empty."));
        }

        var limitResult = DiscoveryResultLimit.Create(requestedLimit);
        if (limitResult.IsFailure)
        {
            return DomainResult<NearbyDiscoveryQueryPayload>.Failure(limitResult.Error);
        }

        return DomainResult<NearbyDiscoveryQueryPayload>.Success(
            new NearbyDiscoveryQueryPayload(requesterId, limitResult.Value));
    }
}

public sealed record NearbyDiscoveryResponsePayload(
    IReadOnlyList<PeerDiscoveryDescriptor> Descriptors);
