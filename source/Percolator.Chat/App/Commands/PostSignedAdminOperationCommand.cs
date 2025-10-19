using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed record PostSignedAdminOperationCommand(
    ConversationLookupKey Lookup,
    Guid OpId,
    DateTimeOffset SentUtc,
    Percolator.Chat.Events.AdminOperationKind Kind,
    byte[]? GranteePublicKeySpki,
    IReadOnlyList<Guid>? MembersToAdd,
    IReadOnlyList<Guid>? MembersToRemove,
    bool? LeaveGroup,
    string? NewGroupName,
    byte[]? NewGroupAvatar,
    byte[] Signature,
    ulong? AdminSequenceNumber
) : IRequest;
