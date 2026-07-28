namespace Percolator.Cryptography.GroupLedger;

public interface ISkippedMessageKeyRepository
{
    Task<SkippedMessageKey?> GetByIdAsync(GroupId groupId, uint senderKeyId, int messageIndex, CancellationToken cancellationToken);
    Task SaveAsync(SkippedMessageKey key, CancellationToken cancellationToken);
    Task DeleteAsync(GroupId groupId, uint senderKeyId, int messageIndex, CancellationToken cancellationToken);
}
