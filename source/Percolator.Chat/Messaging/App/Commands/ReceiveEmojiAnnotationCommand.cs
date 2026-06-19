using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record ReceiveEmojiAnnotationCommand(
    ConversationLookupKey LookupKey,
    ParticipantId ReactorId,
    MessageId MessageId,
    string Emoji,
    DateTimeOffset SentTimestampUtc) : IRequest;
