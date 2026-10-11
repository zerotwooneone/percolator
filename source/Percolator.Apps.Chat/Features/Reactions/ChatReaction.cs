using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Reactions;

public sealed record ChatReaction(
    PublicIdentityId ReactorId,
    Emoji Emoji,
    ulong ReactedAtUnixMs);
