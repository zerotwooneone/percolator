using MediatR;

namespace Percolator.Chat.Events
{
    public sealed class SignedAdminCommitOperationPostedEvent : INotification
    {
        public Guid GroupConversationId { get; }
        public Guid OpId { get; }
        public uint CommittedKeyVersion { get; }
        public DateTime SentTimestampUtc { get; }
        public long SenderId { get; }
        public IReadOnlyList<long> RecipientIds { get; }
        public byte[] Signature { get; }
        public ulong? AdminSequenceNumber { get; }

        public SignedAdminCommitOperationPostedEvent(
            Guid groupConversationId,
            Guid opId,
            uint committedKeyVersion,
            DateTime sentTimestampUtc,
            long senderId,
            IReadOnlyList<long> recipientIds,
            byte[] signature,
            ulong? adminSequenceNumber)
        {
            GroupConversationId = groupConversationId;
            OpId = opId;
            CommittedKeyVersion = committedKeyVersion;
            SentTimestampUtc = sentTimestampUtc;
            SenderId = senderId;
            RecipientIds = recipientIds ?? throw new ArgumentNullException(nameof(recipientIds));
            Signature = signature ?? throw new ArgumentNullException(nameof(signature));
            AdminSequenceNumber = adminSequenceNumber;
        }
    }
}
