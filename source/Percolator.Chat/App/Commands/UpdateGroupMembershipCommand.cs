using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record UpdateGroupMembershipCommand(
    ConversationLookupKey LookupKey,
    IEnumerable<ParticipantId> Add,
    IEnumerable<ParticipantId> Remove,
    bool Leave
) : IRequest;
