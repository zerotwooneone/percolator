namespace Percolator.Cryptography;

public interface ISessionCatalog
{
    IAsyncEnumerable<SessionId> EnumerateActiveAsync(uint selfIdentityId, CancellationToken cancellationToken = default);
}
