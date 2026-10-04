using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Ports;

public interface ILocalIdentityKeyStore
{
    Task<byte[]?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default);
    Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default);
}
