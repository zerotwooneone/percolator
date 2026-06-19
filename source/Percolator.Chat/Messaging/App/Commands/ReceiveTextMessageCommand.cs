using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveTextMessageCommand(
    ConversationLookupKey LookupKey,
    ParticipantId SenderId,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc) : IRequest;
