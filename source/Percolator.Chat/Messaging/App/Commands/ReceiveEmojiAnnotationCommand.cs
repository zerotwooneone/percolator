using MediatR;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveEmojiAnnotationCommand(
    ConversationLookupKey LookupKey,
    ChatPeerId ReactorId,
    PublicMessageId PublicMessageId,
    string Emoji,
    DateTimeOffset SentTimestampUtc) : IRequest;
