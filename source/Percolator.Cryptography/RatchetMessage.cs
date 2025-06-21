namespace Percolator.Cryptography
{
    public class RatchetMessage
    {
        public byte[] RatchetPublicKey { get; set; } = System.Array.Empty<byte>();
        public uint Nonce { get; set; }
        public byte[] Ciphertext { get; set; } = System.Array.Empty<byte>();
    }
}
