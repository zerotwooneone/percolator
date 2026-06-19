using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record PostTextMessageCommand(
    ConversationLookupKey LookupKey,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc
) : IRequest;
