using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat;

/// <summary>
/// Repository for managing messages as independent aggregates.
/// Messages are no longer stored as a collection within Conversation aggregates.
/// </summary>
public interface IMessageRepository
{
    Task AddAsync(Message message, int selfIdentityId, CancellationToken cancellationToken);
    Task UpdateAsync(Message message, int selfIdentityId, CancellationToken cancellationToken);
    Task<Message?> GetByIdAsync(MessageId id, int selfIdentityId, CancellationToken cancellationToken);
}
