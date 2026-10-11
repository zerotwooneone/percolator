using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record MessageEditDto(
    PayloadId TargetPayloadId,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    string NewContent,
    IReadOnlyList<MentionDto> NewMentions,
    ulong EditedAtUnixMs);
