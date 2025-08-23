using MediatR;

namespace Percolator.Application.Network
{
    public class SubmitPreKeyBundleCommand : IRequest<Unit>
    {
        public byte[] IdentityKeyBytes { get; set; } = System.Array.Empty<byte>();
        public byte[] SignedPayloadBytes { get; set; } = System.Array.Empty<byte>();
        public byte[] SignatureBytes { get; set; } = System.Array.Empty<byte>();
    }
}
