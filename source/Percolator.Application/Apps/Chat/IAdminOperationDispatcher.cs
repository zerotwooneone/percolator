using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Identity; // PeerId

namespace Percolator.Application.Apps.Chat
{
    public interface IAdminOperationDispatcher
    {
        Task DispatchGrantAdminAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            byte[] granteePublicKeySpki,
            byte[] signature,
            IReadOnlyList<PeerId> recipientPeerIds,
            CancellationToken ct = default);

        Task DispatchRevokeAdminAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            byte[] granteePublicKeySpki,
            byte[] signature,
            IReadOnlyList<PeerId> recipientPeerIds,
            CancellationToken ct = default);

        Task DispatchUpdateGroupMembershipAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            IReadOnlyList<Guid>? membersToAdd,
            IReadOnlyList<Guid>? membersToRemove,
            bool? leaveGroup,
            byte[] signature,
            IReadOnlyList<PeerId> recipientPeerIds,
            CancellationToken ct = default);

        Task DispatchUpdateGroupInfoAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            string? newGroupName,
            byte[]? newGroupAvatar,
            byte[] signature,
            IReadOnlyList<PeerId> recipientPeerIds,
            CancellationToken ct = default);
    }
}
