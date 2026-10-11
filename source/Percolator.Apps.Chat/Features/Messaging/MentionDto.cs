using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record MentionDto(
    PublicIdentityId IdentityId,
    int StartOffset,
    int Length);
