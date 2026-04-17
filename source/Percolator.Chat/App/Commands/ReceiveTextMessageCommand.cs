using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record ReceiveTextMessageCommand(
    ConversationLookupKey LookupKey,
    ParticipantId SenderId,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc) : IRequest;
