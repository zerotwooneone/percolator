using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record ReceiveEmojiAnnotationCommand(
    ConversationLookupKey LookupKey,
    ParticipantId ReactorId,
    MessageId MessageId,
    string Emoji,
    DateTimeOffset SentTimestampUtc) : IRequest;
