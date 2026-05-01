using MediatR;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Prekey.Handlers
{
    public sealed class GetPreKeyBundleQuery : IRequest<PreKeyBundle?>
    {
        public IdentityPublicKeyHash TargetPublicSigningKeyHash { get; }

        public GetPreKeyBundleQuery(IdentityPublicKeyHash targetPublicSigningKeyHash)
        {
            TargetPublicSigningKeyHash = targetPublicSigningKeyHash;
        }
    }
}
