using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveReadReceiptCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId ReaderId,
    MessageId MessageId,
    DateTimeOffset SentTimestampUtc) : IRequest;
