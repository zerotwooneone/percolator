using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat
{
    public interface IGroupSenderKeyRepository
    {
        Task<bool> ExistsAsync(Guid conversationId, GroupKeyVersion version, CancellationToken cancellationToken);
        Task UpsertAsync(Guid conversationId, GroupKeyVersion version, byte[] chainKey, byte[] signingKey, CancellationToken cancellationToken);
    }
}
