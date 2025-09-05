using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record PostEmojiAnnotationCommand(
    ConversationLookupKey LookupKey,
    MessageId MessageId,
    string Emoji,
    DateTimeOffset SentTimestampUtc
) : IRequest;
