using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveReadReceiptCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId ReaderId,
    PublicMessageId PublicMessageId,
    DateTimeOffset SentTimestampUtc) : IRequest;
