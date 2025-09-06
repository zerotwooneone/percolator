using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record PostDeliveredReceiptCommand(
    ConversationLookupKey LookupKey,
    MessageId MessageId,
    DateTimeOffset DeliveredAtUtc
) : IRequest;
