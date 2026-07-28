namespace Percolator.Cryptography.GroupLedger;

public interface ISenderKeyRatchetRepository
{
    Task<SenderKeyRatchet?> GetByIdAsync(GroupId id, uint senderKeyId, CancellationToken cancellationToken);
    Task SaveAsync(SenderKeyRatchet ratchet, CancellationToken cancellationToken);
}
