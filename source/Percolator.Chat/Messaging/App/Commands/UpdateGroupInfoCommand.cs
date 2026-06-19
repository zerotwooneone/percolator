using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record UpdateGroupInfoCommand(
    ConversationId ConversationId,
    int SelfIdentityId,
    string? NewName
) : IRequest;
