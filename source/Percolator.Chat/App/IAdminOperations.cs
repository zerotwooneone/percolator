using Percolator.Chat.ValueObjects;
using Percolator.Chat.Primitives;

namespace Percolator.Chat.App
{
    public interface IAdminOperations
    {
        Task GrantAdminAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            AdminPublicKey grantee,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default);

        Task RevokeAdminAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            AdminPublicKey grantee,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default);

        Task UpdateGroupMembershipAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            IReadOnlyList<ParticipantId>? membersToAdd,
            IReadOnlyList<ParticipantId>? membersToRemove,
            bool? leaveGroup,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default);

        Task UpdateGroupInfoAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            string? newGroupName,
            ByteArrayRecord? newGroupAvatar,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default);
    }
}
