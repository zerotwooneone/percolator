using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Ports;

/// <summary>
/// Port for the local identity key material (identity keypair + profile key) held in the
/// unlocked vault. The identity private key is an <see cref="EphemeralPrivateKey"/>
/// (<see cref="ISensitiveSecret"/>): callers own the returned copy and must <c>Zeroize()</c>
/// it when done. Implementations clone on write so no stored buffer is shared.
/// </summary>
public interface ILocalIdentityKeyStore
{
    Task<EphemeralPrivateKey?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default);
    Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default);
    Task<ProfileKey?> GetProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default);
    Task SaveIdentityKeysAsync(
        PublicIdentityId identityId,
        EphemeralPrivateKey privateKey,
        IdentityKey publicKey,
        ProfileKey profileKey,
        CancellationToken ct = default);
    Task<ProfileKey> RotateProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default);
    Task DeleteIdentityKeysAsync(PublicIdentityId identityId, CancellationToken ct = default);
}
