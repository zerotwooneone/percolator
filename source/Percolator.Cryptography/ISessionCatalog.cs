namespace Percolator.Cryptography;

public interface ISessionCatalog
{
    IAsyncEnumerable<SessionId> EnumerateActiveAsync(CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
}
