using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat;

public sealed record AcceptGroupInviteCommand(
    ConversationId ConversationId,
    int SelfIdentityId
) : IRequest;
