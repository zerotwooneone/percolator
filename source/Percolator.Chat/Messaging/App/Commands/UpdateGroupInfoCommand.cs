using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Commands;

public sealed record UpdateGroupInfoCommand(
    ConversationId ConversationId,
    ParticipantId ActorParticipantId,
    string? NewName
) : IRequest;
