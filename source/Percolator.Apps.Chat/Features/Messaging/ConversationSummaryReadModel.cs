using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record ConversationSummaryReadModel(
    ChannelId ChannelId,
    string Title,
    string LastMessageSnippet,
    ulong LastMessageTimestampUnixMs,
    int UnreadCount,
    PublicIdentityId? LastAuthorId);
