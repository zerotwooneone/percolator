using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat;

public sealed record DeclineGroupInviteCommand(
    ConversationId ConversationId,
    int SelfIdentityId
) : IRequest;
