using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Messaging;

public enum ConversationChannelKind
{
    Direct = 0,
    Group = 1
}

public sealed record ConversationSummaryReadModel(
    ChannelId ChannelId,
    ConversationChannelKind Kind,
    PublicIdentityId? RemotePeerId,
    string Title,
    string LastMessageSnippet,
    ulong LastActivityTimestampUnixMs,
    int UnreadCount,
    PublicIdentityId? LastAuthorId,
    bool IsPendingAcceptance,
    string? RelayEndpoint,
    PublicIdentityId? RelayIdentityId);
