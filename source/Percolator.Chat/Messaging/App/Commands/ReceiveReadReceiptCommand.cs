using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveReadReceiptCommand(
    ConversationLookupKey LookupKey,
    ParticipantId ReaderId,
    MessageId MessageId,
    DateTimeOffset SentTimestampUtc) : IRequest;
