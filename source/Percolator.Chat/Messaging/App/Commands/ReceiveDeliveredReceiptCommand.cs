using MediatR;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveDeliveredReceiptCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId RecipientId,
    PublicMessageId PublicMessageId,
    DateTimeOffset DeliveredTimestampUtc) : IRequest;
