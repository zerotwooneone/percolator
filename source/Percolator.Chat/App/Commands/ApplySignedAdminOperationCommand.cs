using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;
using System;
using System.Collections.Generic;

namespace Percolator.Chat.App.Commands
{
    public enum AdminOperationKind
    {
        GrantAdmin,
        RevokeAdmin,
        UpdateGroupMembership,
        UpdateGroupInfo
    }

    public readonly struct AdminSignature
    {
        public byte[] Bytes { get; }
        public AdminSignature(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("Signature cannot be null or empty", nameof(bytes));
            Bytes = bytes;
        }
    }

    // A domain-level command that carries a normalized admin operation without referencing protobuf types.
    public record ApplySignedAdminOperationCommand(
        ConversationLookupKey Lookup,
        Guid OpId,
        DateTimeOffset SentUtc,
        ulong? AdminSequenceNumber,
        AdminOperationKind Kind,
        AdminPublicKey? Grantee,
        IReadOnlyList<ParticipantId>? MembersToAdd,
        IReadOnlyList<ParticipantId>? MembersToRemove,
        bool? LeaveGroup,
        string? NewGroupName,
        ByteArrayRecord? NewGroupAvatar,
        AdminSignature Signature,
        byte[] PayloadBytes
    ) : IRequest;
}
