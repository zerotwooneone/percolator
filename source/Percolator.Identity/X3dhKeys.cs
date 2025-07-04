using System.Security.Cryptography;

namespace Percolator.Identity;

public record X3dhKeys(
    ECDsa IdentitySigningKey,
    ECDiffieHellman IdentityAgreementKey,
    ECDiffieHellman SignedPreKey,
    ECDiffieHellman[] OneTimePreKeys) : IDisposable
{
    public void Dispose()
    {
        IdentitySigningKey.Dispose();
        IdentityAgreementKey.Dispose();
        SignedPreKey.Dispose();
        foreach (var key in OneTimePreKeys)
        {
            key.Dispose();
        }
    }
}
