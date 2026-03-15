namespace Percolator.Cryptography;

public interface ISessionCatalog
{
    IAsyncEnumerable<SessionId> EnumerateActiveAsync(int selfIdentityId, CancellationToken cancellationToken = default);
}
