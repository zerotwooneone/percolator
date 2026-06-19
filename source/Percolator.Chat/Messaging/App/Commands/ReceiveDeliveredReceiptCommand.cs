using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveDeliveredReceiptCommand(
    ConversationLookupKey LookupKey,
    ParticipantId RecipientId,
    MessageId MessageId,
    DateTimeOffset DeliveredTimestampUtc) : IRequest;
