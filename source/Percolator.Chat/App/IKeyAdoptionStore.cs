using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App
{
    public interface IKeyAdoptionStore
    {
        Task AddAsync(Guid conversationId, GroupKeyVersion version, IdentityPublicKey adopterIdentityKey, DateTimeOffset sentAtUtc, byte[] signature, CancellationToken ct);
        Task<int> GetCountAsync(Guid conversationId, GroupKeyVersion version, CancellationToken ct);
    }
}
