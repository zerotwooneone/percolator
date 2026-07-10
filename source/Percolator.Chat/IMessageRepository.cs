using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using PublicMessageId = Percolator.Chat.Messaging.PublicMessageId;

namespace Percolator.Chat;

/// <summary>
/// Repository for managing messages as independent aggregates.
/// Messages are no longer stored as a collection within Conversation aggregates.
/// </summary>
public interface IMessageRepository
{
    Task AddAsync(Message message, ChatSelfId selfIdentityId, CancellationToken cancellationToken);
    Task UpdateAsync(Message message, ChatSelfId selfIdentityId, CancellationToken cancellationToken);
    Task<Message?> GetByIdAsync(PublicMessageId id, ChatSelfId selfIdentityId, CancellationToken cancellationToken);
}
