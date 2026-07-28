namespace Percolator.Cryptography.GroupLedger;

public interface IGroupCredentialsRepository
{
    Task<GroupCredentials?> GetByIdAsync(GroupId id, CancellationToken cancellationToken);
    Task SaveAsync(GroupCredentials credentials, CancellationToken cancellationToken);
}
