using MediatR;
using System;
using System.Collections.Generic;
using PeerId = Percolator.Identity.PeerId;
using AdminOperationKind = Percolator.Chat.Events.AdminOperationKind;

namespace Percolator.Application.Apps.Chat;

public sealed record DispatchSignedAdminOperationCommand(
    Guid GroupConversationId,
    Guid OpId,
    DateTime SentTimestampUtc,
    IReadOnlyList<PeerId> RecipientPeerIds,
    PeerId SenderPeerId,
    AdminOperationKind Kind,
    byte[]? GranteePublicKeySpki,
    IReadOnlyList<Guid>? MembersToAdd,
    IReadOnlyList<Guid>? MembersToRemove,
    bool? LeaveGroup,
    string? NewGroupName,
    byte[]? NewGroupAvatar,
    byte[] Signature,
    ulong? AdminSequenceNumber
) : IRequest;
