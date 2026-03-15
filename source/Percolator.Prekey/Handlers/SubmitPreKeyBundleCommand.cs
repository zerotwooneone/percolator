using MediatR;
using Percolator.Network;

namespace Percolator.Prekey.Handlers
{
    public class SubmitPreKeyBundleCommand : IRequest<Unit>
    {
        public record OneTimePreKey(Guid Id, byte[] Key);
        
        public byte[] PublicSigningKey { get; set; } = System.Array.Empty<byte>();
        public Guid SignedPreKeyId { get; set; } = Guid.Empty;
        public byte[] SignedPreKey { get; set; } = System.Array.Empty<byte>();
        public byte[] PreKeySignature { get; set; } = System.Array.Empty<byte>();
        public IReadOnlyCollection<OneTimePreKey> OneTimePreKeys { get; set; } = System.Array.Empty<OneTimePreKey>();
        public DateTimeOffset Expires { get; set; }
        public PeerId RemotePeerId { get; set; } = null!;
    }
}
