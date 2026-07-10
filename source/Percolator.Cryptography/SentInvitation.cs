using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public enum InviteRouteKind
{
    Direct = 0,
    Relayed = 1
}

public sealed class SentInvitation
{
    public RequestCorrelationId RequestCorrelationId { get; }
    public Guid SignedPreKeyId { get; }
    public Guid? OneTimePreKeyId { get; }
    public CryptoPeerId? TargetPeerId { get; }
    public string? TargetDisplayName { get; }
    public string? TargetEndpointHost { get; }
    public int? TargetEndpointPort { get; }
    public InviteRouteKind InviteRouteKind { get; }
    public CryptoPeerId? InviteRelayHostPeerId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public SentInvitation(
        RequestCorrelationId requestCorrelationId,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        CryptoPeerId? targetPeerId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        string? targetDisplayName = null,
        string? targetEndpointHost = null,
        int? targetEndpointPort = null,
        InviteRouteKind inviteRouteKind = InviteRouteKind.Direct,
        CryptoPeerId? inviteRelayHostPeerId = null)
    {
        if (requestCorrelationId.Value == Guid.Empty)
        {
            throw new ArgumentException("requestCorrelationId must be a non-empty GUID.", nameof(requestCorrelationId));
        }
        if (signedPreKeyId == Guid.Empty)
        {
            throw new ArgumentException("signedPreKeyId must be a non-empty GUID.", nameof(signedPreKeyId));
        }
        if (oneTimePreKeyId.HasValue && oneTimePreKeyId.Value == Guid.Empty)
        {
            throw new ArgumentException("oneTimePreKeyId must be null or a non-empty GUID.", nameof(oneTimePreKeyId));
        }
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException("expiresAtUtc must be greater than createdAtUtc.", nameof(expiresAtUtc));
        }

        RequestCorrelationId = requestCorrelationId;
        SignedPreKeyId = signedPreKeyId;
        OneTimePreKeyId = oneTimePreKeyId;
        TargetPeerId = targetPeerId;
        TargetDisplayName = string.IsNullOrWhiteSpace(targetDisplayName) ? null : targetDisplayName.Trim();
        TargetEndpointHost = string.IsNullOrWhiteSpace(targetEndpointHost) ? null : targetEndpointHost.Trim();
        TargetEndpointPort = targetEndpointPort;
        InviteRouteKind = inviteRouteKind;
        InviteRelayHostPeerId = inviteRelayHostPeerId;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsExpiredAt(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;
}
