using System.Security.Cryptography;

namespace Percolator.Identity;

public record X3dhKeys(
    ECDsa IdentitySigningKey,
    ECDiffieHellman IdentityAgreementKey,
    ECDiffieHellman SignedPreKey,
    ECDiffieHellman OneTimePreKey) : IDisposable
{
    public void Dispose()
    {
        IdentitySigningKey.Dispose();
        IdentityAgreementKey.Dispose();
        SignedPreKey.Dispose();
        OneTimePreKey.Dispose();
    }
}
