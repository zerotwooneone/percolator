using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Apps.Chat.Commands;

public sealed record SendGroupMessageCommand(
    ConversationId ConversationId,
    ChatSelfId SelfIdentityId,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc
) : IRequest;
