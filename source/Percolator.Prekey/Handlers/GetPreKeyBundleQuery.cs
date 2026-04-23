using MediatR;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Prekey.Handlers
{
    public class GetPreKeyBundleQuery : IRequest<PreKeyBundle?>
    {
        public IdentityPublicKeyHash TargetPublicSigningKeyHash { get; set; } = IdentityPublicKeyHash.FromBytes(new byte[32]);
    }
}
