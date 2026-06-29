using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveTextMessageCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId SenderId,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc) : IRequest;
