using System.Collections.Generic;
using System.Security.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public class InMemoryOneTimeKeyProvider : IOneTimeKeyProvider
{
    public void AddKeys(IEnumerable<ECDiffieHellman> keys)
    {
        // This is intentionally left empty as keys are generated on demand.
    }

    public ECDiffieHellman PopOneTimeKey()
    {
        return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }
}
