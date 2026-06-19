using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record PostReadReceiptCommand(
    ConversationLookupKey LookupKey,
    MessageId MessageId,
    DateTimeOffset SentTimestampUtc
) : IRequest;
