using Percolator.Domain.Common;
using Percolator.Domain.Conversations.Events;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Model;

public sealed class DirectConversation : AggregateRoot<ConversationId>
{
    public override ConversationId Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }
    public MessageId? LastReadMessageId { get; private set; }

    private readonly List<Message> _messages = [];
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    private DirectConversation(
        ConversationId id,
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OwnerIdentityId = ownerIdentityId;
        RemotePeerId = remotePeerId;
        CreatedAtUtc = createdAtUtc;
        LastActivityUtc = createdAtUtc;
    }

    public static DomainResult<DirectConversation> Create(
        ConversationId id,
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        IDateTimeProvider timeProvider)
    {
        if (!id.IsValid)
        {
            return DomainResult<DirectConversation>.Failure(new DomainError("INVALID_CONVERSATION_ID", "ConversationId cannot be empty."));
        }

        if (!ownerIdentityId.IsValid)
        {
            return DomainResult<DirectConversation>.Failure(new DomainError("INVALID_OWNER_ID", "OwnerIdentityId cannot be empty."));
        }

        if (!remotePeerId.IsValid)
        {
            return DomainResult<DirectConversation>.Failure(new DomainError("INVALID_PEER_ID", "RemotePeerId cannot be empty."));
        }

        if (ownerIdentityId == remotePeerId)
        {
            return DomainResult<DirectConversation>.Failure(new DomainError("SELF_CONVERSATION_NOT_ALLOWED", "Owner cannot establish a direct conversation with self."));
        }

        var conversation = new DirectConversation(id, ownerIdentityId, remotePeerId, timeProvider.UtcNow);
        return DomainResult<DirectConversation>.Success(conversation);
    }

    public DomainResult AppendMessage(Message message, IDateTimeProvider timeProvider)
    {
        if (message.ConversationId != Id)
        {
            return DomainResult.Failure(new DomainError("CONVERSATION_MISMATCH", "Message does not belong to this conversation."));
        }

        if (message.AuthorId != OwnerIdentityId && message.AuthorId != RemotePeerId)
        {
            return DomainResult.Failure(new DomainError("SENDER_NOT_PARTICIPANT", "Message author is not a participant in this direct conversation."));
        }

        _messages.Add(message);
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MessageAppendedEvent(Id, message.Id, LastActivityUtc));

        return DomainResult.Success();
    }

    public void MarkAsRead(MessageId messageId)
    {
        LastReadMessageId = messageId;
    }
}
