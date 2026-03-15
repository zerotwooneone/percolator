namespace Percolator.Identity;

public interface ISelfIdentityKeysStore
{
    Task<X3dhKeys?> LoadAsync(SelfId selfIdentityId, CancellationToken cancellationToken = default);
    Task SaveAsync(SelfId selfIdentityId, X3dhKeys keys, CancellationToken cancellationToken = default);
}
