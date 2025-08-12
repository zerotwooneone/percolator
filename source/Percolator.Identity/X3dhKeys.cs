using System.Security.Cryptography;

namespace Percolator.Identity;

public record X3dhKeys(
    ECDiffieHellman IdentitySigningKey,
    ECDiffieHellman IdentityAgreementKey,
    ECDiffieHellman SignedPreKey) : IDisposable
{
    public void Dispose()
    {
        IdentitySigningKey.Dispose();
        IdentityAgreementKey.Dispose();
        SignedPreKey.Dispose();
    }
}
