using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat.Commands;

public sealed record SendGroupMessageCommand(
    ConversationId ConversationId,
    int SelfIdentityId,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc
) : IRequest;
