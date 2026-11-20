using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Reactions;

public sealed record ChatReaction(
    PublicIdentityId ReactorId,
    Emoji Emoji,
    ulong ReactedAtUnixMs);
