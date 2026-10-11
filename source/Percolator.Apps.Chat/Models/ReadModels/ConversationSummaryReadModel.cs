using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models.ReadModels;

public sealed record ConversationSummaryReadModel(
    ChannelId ChannelId,
    string Title,
    string LastMessageSnippet,
    ulong LastMessageTimestampUnixMs,
    int UnreadCount,
    PublicIdentityId? LastAuthorId);
