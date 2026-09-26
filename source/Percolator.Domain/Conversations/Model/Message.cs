using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Model;

public sealed class Message : IEntity<MessageId>
{
    public MessageId Id { get; }
    public ConversationId ConversationId { get; }
    public PublicIdentityId AuthorId { get; }
    public DeviceId AuthorDeviceId { get; }
    public ReadOnlyMemory<byte> EncryptedPayload { get; }
    public DateTimeOffset SentAtUtc { get; }

    public Message(
        MessageId id,
        ConversationId conversationId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ReadOnlyMemory<byte> encryptedPayload,
        DateTimeOffset sentAtUtc)
    {
        Id = id;
        ConversationId = conversationId;
        AuthorId = authorId;
        AuthorDeviceId = authorDeviceId;
        EncryptedPayload = encryptedPayload;
        SentAtUtc = sentAtUtc;
    }
}
