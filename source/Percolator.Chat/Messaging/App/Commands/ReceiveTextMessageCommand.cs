using MediatR;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveTextMessageCommand(
    ConversationLookupKey LookupKey,
    ParticipantId SenderId,
    PublicMessageId PublicMessageId,
    string Content,
    DateTimeOffset SentTimestampUtc) : IRequest;
