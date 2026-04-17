using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record ReceiveReadReceiptCommand(
    ConversationLookupKey LookupKey,
    ParticipantId ReaderId,
    MessageId MessageId,
    DateTimeOffset SentTimestampUtc) : IRequest;
