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

    public DirectConversation(
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

    public DomainResult AppendMessage(Message message, IDateTimeProvider timeProvider)
    {
        if (message.ConversationId != Id)
        {
            return DomainResult.Failure(new DomainError("CONVERSATION_MISMATCH", "Message does not belong to this conversation."));
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
