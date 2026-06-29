using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveEmojiAnnotationCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId ReactorId,
    MessageId MessageId,
    string Emoji,
    DateTimeOffset SentTimestampUtc) : IRequest;
