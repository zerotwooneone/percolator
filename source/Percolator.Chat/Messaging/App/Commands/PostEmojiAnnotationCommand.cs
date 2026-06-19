using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record PostEmojiAnnotationCommand(
    ConversationLookupKey LookupKey,
    MessageId MessageId,
    string Emoji,
    DateTimeOffset SentTimestampUtc
) : IRequest;
