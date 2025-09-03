using MediatR;
using Percolator.Cryptography;

namespace Percolator.Prekey.Handlers
{
    public class GetPreKeyBundleQuery : IRequest<PreKeyBundle?>
    {
        public byte[] TargetPublicSigningKeyHash { get; set; } = System.Array.Empty<byte>();
    }
}
