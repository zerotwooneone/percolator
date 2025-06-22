using System.Security.Cryptography;

namespace Percolator.Identity;

public record X3dhKeys(
    ECDiffieHellman IdentityKey,
    ECDiffieHellman SignedPreKey,
    ECDiffieHellman OneTimePreKey) : IDisposable
{
    public void Dispose()
    {
        IdentityKey.Dispose();
        SignedPreKey.Dispose();
        OneTimePreKey.Dispose();
    }
}
