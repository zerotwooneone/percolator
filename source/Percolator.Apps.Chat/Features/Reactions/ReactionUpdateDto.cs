using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Reactions;

public sealed record ReactionUpdateDto(
    PayloadId TargetPayloadId,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    string Emoji,
    ReactionAction Action,
    ulong TimestampUnixMs);
