using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record ReceiveDeliveredReceiptCommand(
    ConversationLookupKey LookupKey,
    ParticipantId RecipientId,
    MessageId MessageId,
    DateTimeOffset DeliveredTimestampUtc) : IRequest;
