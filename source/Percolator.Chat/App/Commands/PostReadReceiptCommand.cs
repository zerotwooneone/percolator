using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record PostReadReceiptCommand(
    ConversationLookupKey LookupKey,
    MessageId MessageId,
    DateTimeOffset SentTimestampUtc
) : IRequest;
