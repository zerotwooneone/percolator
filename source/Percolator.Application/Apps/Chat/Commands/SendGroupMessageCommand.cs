using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Apps.Chat.Commands;

public sealed record SendGroupMessageCommand(
    ConversationId ConversationId,
    int SelfIdentityId,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc
) : IRequest;
