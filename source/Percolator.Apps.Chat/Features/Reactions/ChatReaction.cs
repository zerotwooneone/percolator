using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Reactions;

public sealed record ChatReaction(
    PublicIdentityId ReactorId,
    string Emoji,
    ulong ReactedAtUnixMs);
