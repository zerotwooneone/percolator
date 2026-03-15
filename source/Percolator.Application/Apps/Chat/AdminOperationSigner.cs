using Google.Protobuf;
using Percolator.Network;
using Percolator.Contracts;

namespace Percolator.Application.Apps.Chat
{
    internal sealed class AdminOperationSigner : IAdminOperationSigner
    {
        private readonly ISigningService _signingService;

        public AdminOperationSigner(ISigningService signingService)
        {
            _signingService = signingService;
        }

        public Task<(byte[] payload, byte[] signature)> SignGrantAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, byte[] granteeSpki, ulong? adminSequenceNumber = null, CancellationToken ct = default)
            => SignAsync(groupConversationId, opId, sentUtc, adminSequenceNumber, p =>
            {
                p.GrantAdmin = new GrantAdmin { Version = 1, GranteePublicKey = ByteString.CopyFrom(granteeSpki) };
            });

        public Task<(byte[] payload, byte[] signature)> SignRevokeAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, byte[] granteeSpki, ulong? adminSequenceNumber = null, CancellationToken ct = default)
            => SignAsync(groupConversationId, opId, sentUtc, adminSequenceNumber, p =>
            {
                p.RevokeAdmin = new RevokeAdmin { Version = 1, GranteePublicKey = ByteString.CopyFrom(granteeSpki) };
            });

        public Task<(byte[] payload, byte[] signature)> SignUpdateMembershipAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, IReadOnlyList<Guid>? membersToAdd, IReadOnlyList<Guid>? membersToRemove, bool? leaveGroup, ulong? adminSequenceNumber = null, CancellationToken ct = default)
            => SignAsync(groupConversationId, opId, sentUtc, adminSequenceNumber, p =>
            {
                var payload = new UpdateGroupMembershipPayload { Version = 1 };
                if (membersToAdd != null) payload.MembersToAdd.AddRange(membersToAdd.Select(g => ByteString.CopyFrom(g.ToByteArray())));
                if (membersToRemove != null) payload.MembersToRemove.AddRange(membersToRemove.Select(g => ByteString.CopyFrom(g.ToByteArray())));
                if (leaveGroup.HasValue) payload.LeaveGroup = leaveGroup.Value;
                p.UpdateGroupMembership = payload;
            });

        public Task<(byte[] payload, byte[] signature)> SignUpdateInfoAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, string? newGroupName, byte[]? newGroupAvatar, ulong? adminSequenceNumber = null, CancellationToken ct = default)
            => SignAsync(groupConversationId, opId, sentUtc, adminSequenceNumber, p =>
            {
                p.UpdateGroupInfo = new UpdateGroupInfoPayload
                {
                    Version = 1,
                    NewGroupName = newGroupName ?? string.Empty,
                    NewGroupAvatar = newGroupAvatar is null ? null : ByteString.CopyFrom(newGroupAvatar)
                };
            });

        private Task<(byte[] payload, byte[] signature)> SignAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, ulong? adminSequenceNumber, Action<AdminOperationPayload> build)
        {
            var payload = new AdminOperationPayload
            {
                Version = 1,
                GroupConversationGuid = ByteString.CopyFrom(groupConversationId.ToByteArray()),
                OpId = ByteString.CopyFrom(opId.ToByteArray()),
                SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(sentUtc.UtcDateTime)
            };
            if (adminSequenceNumber.HasValue)
                payload.AdminSequenceNumber = adminSequenceNumber.Value;

            build(payload);

            var canonicalBytes = CanonicalPayload.ForAdminOperation(payload);
            var sig = _signingService.Sign(new Payload(canonicalBytes));
            return Task.FromResult<(byte[] payload, byte[] signature)>((canonicalBytes, sig.Value));
        }
    }
}
