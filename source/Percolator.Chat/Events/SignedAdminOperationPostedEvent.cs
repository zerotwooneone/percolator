using MediatR;

namespace Percolator.Chat.Events
{
    public enum AdminOperationKind
    {
        GrantAdmin,
        RevokeAdmin,
        UpdateGroupMembership,
        UpdateGroupInfo
    }

    public sealed class SignedAdminOperationPostedEvent : INotification
    {
        public Guid GroupConversationId { get; }
        public Guid OpId { get; }
        public DateTime SentTimestampUtc { get; }
        public long SenderId { get; }
        public IReadOnlyList<long> RecipientIds { get; }
        public AdminOperationKind Kind { get; }
        public byte[]? GranteePublicKeySpki { get; }
        public IReadOnlyList<Guid>? MembersToAdd { get; }
        public IReadOnlyList<Guid>? MembersToRemove { get; }
        public bool? LeaveGroup { get; }
        public string? NewGroupName { get; }
        public byte[]? NewGroupAvatar { get; }
        public byte[] Signature { get; }
        public ulong? AdminSequenceNumber { get; }

        public SignedAdminOperationPostedEvent(
            Guid groupConversationId,
            Guid opId,
            DateTime sentTimestampUtc,
            long senderId,
            IReadOnlyList<long> recipientIds,
            AdminOperationKind kind,
            byte[]? granteePublicKeySpki,
            IReadOnlyList<Guid>? membersToAdd,
            IReadOnlyList<Guid>? membersToRemove,
            bool? leaveGroup,
            string? newGroupName,
            byte[]? newGroupAvatar,
            byte[] signature,
            ulong? adminSequenceNumber)
        {
            GroupConversationId = groupConversationId;
            OpId = opId;
            SentTimestampUtc = sentTimestampUtc;
            SenderId = senderId;
            RecipientIds = recipientIds ?? throw new ArgumentNullException(nameof(recipientIds));
            Kind = kind;
            GranteePublicKeySpki = granteePublicKeySpki;
            MembersToAdd = membersToAdd;
            MembersToRemove = membersToRemove;
            LeaveGroup = leaveGroup;
            NewGroupName = newGroupName;
            NewGroupAvatar = newGroupAvatar;
            Signature = signature ?? throw new ArgumentNullException(nameof(signature));
            AdminSequenceNumber = adminSequenceNumber;
        }
    }
}
