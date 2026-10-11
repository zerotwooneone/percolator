using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed record MentionDto(
    PublicIdentityId IdentityId,
    int StartOffset,
    int Length);
