using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity; // PeerId
using Percolator.Application.Network;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat
{
    internal sealed class AdminOperationDispatcher : IAdminOperationDispatcher
    {
        private readonly IMediator _mediator;
        private readonly ILogger<AdminOperationDispatcher> _logger;
        private readonly IRemoteEnvelopeSender _sender;
        private readonly IPeerPublicSigningKeyStore _keyStore;

        public AdminOperationDispatcher(IMediator mediator, ILogger<AdminOperationDispatcher> logger, IRemoteEnvelopeSender sender, IPeerPublicSigningKeyStore keyStore)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        }

        public Task DispatchGrantAdminAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            byte[] granteePublicKeySpki,
            byte[] signature,
            IReadOnlyList<PeerId> recipientPeerIds,
            CancellationToken ct = default)
            => DispatchAsync(senderPeerId, groupConversationId, opId, sentUtc, adminSequenceNumber,
                buildPayload: p => p.GrantAdmin = new GrantAdmin { Version = 1, GranteePublicKey = ByteString.CopyFrom(granteePublicKeySpki) },
                signature: signature,
                recipients: recipientPeerIds,
                ct: ct);

        public Task DispatchRevokeAdminAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            byte[] granteePublicKeySpki,
            byte[] signature,
            IReadOnlyList<PeerId> recipientPeerIds,
            CancellationToken ct = default)
            => DispatchAsync(senderPeerId, groupConversationId, opId, sentUtc, adminSequenceNumber,
                buildPayload: p => p.RevokeAdmin = new RevokeAdmin { Version = 1, GranteePublicKey = ByteString.CopyFrom(granteePublicKeySpki) },
                signature: signature,
                recipients: recipientPeerIds,
                ct: ct);

        public Task DispatchUpdateGroupMembershipAsync(
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
            CancellationToken ct = default)
            => DispatchAsync(senderPeerId, groupConversationId, opId, sentUtc, adminSequenceNumber,
                buildPayload: p =>
                {
                    var ugm = new UpdateGroupMembershipPayload { Version = 1 };
                    if (membersToAdd != null) ugm.MembersToAdd.AddRange(membersToAdd.Select(g => ByteString.CopyFrom(g.ToByteArray())));
                    if (membersToRemove != null) ugm.MembersToRemove.AddRange(membersToRemove.Select(g => ByteString.CopyFrom(g.ToByteArray())));
                    if (leaveGroup.HasValue) ugm.LeaveGroup = leaveGroup.Value;
                    p.UpdateGroupMembership = ugm;
                },
                signature: signature,
                recipients: recipientPeerIds,
                ct: ct);

        public Task DispatchUpdateGroupInfoAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            string? newGroupName,
            byte[]? newGroupAvatar,
            byte[] signature,
            IReadOnlyList<PeerId> recipientPeerIds,
            CancellationToken ct = default)
            => DispatchAsync(senderPeerId, groupConversationId, opId, sentUtc, adminSequenceNumber,
                buildPayload: p => p.UpdateGroupInfo = new UpdateGroupInfoPayload
                {
                    Version = 1,
                    NewGroupName = newGroupName ?? string.Empty,
                    NewGroupAvatar = newGroupAvatar is null ? null : ByteString.CopyFrom(newGroupAvatar)
                },
                signature: signature,
                recipients: recipientPeerIds,
                ct: ct);

        private async Task DispatchAsync(
            PeerId senderPeerId,
            Guid groupConversationId,
            Guid opId,
            DateTimeOffset sentUtc,
            ulong? adminSequenceNumber,
            Action<AdminOperationPayload> buildPayload,
            byte[] signature,
            IReadOnlyList<PeerId> recipients,
            CancellationToken ct)
        {
            if (recipients == null || recipients.Count == 0)
            {
                _logger.LogInformation("No recipients for admin op {OpId}", opId);
                return;
            }

            var payload = new AdminOperationPayload
            {
                Version = 1,
                GroupConversationGuid = ByteString.CopyFrom(groupConversationId.ToByteArray()),
                OpId = ByteString.CopyFrom(opId.ToByteArray()),
                SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(sentUtc.UtcDateTime)
            };
            if (adminSequenceNumber.HasValue)
                payload.AdminSequenceNumber = adminSequenceNumber.Value;

            buildPayload(payload);

            var signed = new SignedAdminOperation
            {
                Version = 1,
                Payload = payload,
                Signature = ByteString.CopyFrom(signature)
            };

            var envelope = new InternalEnvelope
            {
                ChatEnvelope = new ChatEnvelope
                {
                    Version = 1,
                    SignedAdminOperation = signed
                }
            };

            // Send to each recipient via RemoteEnvelopeSender (direct session preferred; host-enqueue fallback if available)
            foreach (var pid in recipients.Where(pid => pid != senderPeerId))
            {
                try
                {
                    // Resolve latest active PKH for enqueue fallback
                    byte[]? pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(pid, ct);
                    await _sender.SendChatEnvelopeToPeerAsync(envelope.ChatEnvelope, new RecipientRoute(pid, pkh), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error dispatching admin op {OpId} to {RecipientId}", opId, pid);
                }
            }
        }
    }
}
