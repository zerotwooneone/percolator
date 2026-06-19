using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Apps.Chat;

public sealed record AcceptGroupInviteCommand(
    ConversationId ConversationId,
    int SelfIdentityId
) : IRequest;
