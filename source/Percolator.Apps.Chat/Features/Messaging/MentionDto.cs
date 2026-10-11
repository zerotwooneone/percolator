using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record MentionDto
{
    public PublicIdentityId IdentityId { get; }
    public int StartOffset { get; }
    public int Length { get; }

    public MentionDto(PublicIdentityId identityId, int startOffset, int length)
    {
        if (!identityId.IsValid)
        {
            throw new ArgumentException("IdentityId must be valid.", nameof(identityId));
        }

        if (startOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), "StartOffset must be non-negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");
        }

        IdentityId = identityId;
        StartOffset = startOffset;
        Length = length;
    }
}
