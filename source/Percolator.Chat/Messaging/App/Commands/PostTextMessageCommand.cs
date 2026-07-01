using MediatR;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record PostTextMessageCommand(
    ConversationLookupKey LookupKey,
    PublicMessageId PublicMessageId,
    string Content,
    DateTimeOffset SentTimestampUtc,
    ChatSelfId SelfIdentityId
) : IRequest;
