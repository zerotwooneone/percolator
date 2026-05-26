using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record UpdateGroupInfoCommand(
    ConversationId ConversationId,
    int SelfIdentityId,
    string? NewName
) : IRequest;
