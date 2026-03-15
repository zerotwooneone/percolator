using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public sealed class SentInvitation
{
    public RequestCorrelationId RequestCorrelationId { get; }
    public Guid SignedPreKeyId { get; }
    public Guid? OneTimePreKeyId { get; }
    public PeerId? TargetPeerId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public SentInvitation(
        RequestCorrelationId requestCorrelationId,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        PeerId? targetPeerId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
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
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsExpiredAt(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;
}
