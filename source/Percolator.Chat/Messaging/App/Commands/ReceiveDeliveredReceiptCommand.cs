using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveDeliveredReceiptCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId RecipientId,
    MessageId MessageId,
    DateTimeOffset DeliveredTimestampUtc) : IRequest;
