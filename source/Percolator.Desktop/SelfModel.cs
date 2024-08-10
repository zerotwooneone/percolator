using System.Security.Cryptography;

namespace Percolator.Desktop;

public class SelfModel 
{
    public Guid IdentitySuffix { get; }
    public RSA Identity { get; }

    public SelfModel(Guid identitySuffix,RSA identity)
    {
        IdentitySuffix = identitySuffix;
        Identity = identity;
    }
}