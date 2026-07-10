using MediatR;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Prekey.Handlers
{
    public sealed class GetPreKeyBundleQuery : IRequest<PreKeyBundle?>
    {
        public PublicIdentityId TargetPublicIdentityId { get; }

        public GetPreKeyBundleQuery(PublicIdentityId targetPublicIdentityId)
        {
            TargetPublicIdentityId = targetPublicIdentityId;
        }
    }
}
