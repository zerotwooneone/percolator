using MediatR;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveReadReceiptCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId ReaderId,
    PublicMessageId PublicMessageId,
    DateTimeOffset SentTimestampUtc) : IRequest;
