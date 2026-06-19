using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record PostDeliveredReceiptCommand(
    ConversationLookupKey LookupKey,
    MessageId MessageId,
    DateTimeOffset DeliveredAtUtc
) : IRequest;
