using MediatR;

namespace Percolator.Chat.App.Commands;

public sealed record UpdateGroupInfoCommand(
    ConversationLookupKey LookupKey,
    string? NewName
) : IRequest;
