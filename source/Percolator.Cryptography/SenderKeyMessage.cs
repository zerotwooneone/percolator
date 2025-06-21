namespace Percolator.Cryptography
{
    public class SenderKeyMessage
    {
        public uint Iteration { get; set; }
        public byte[] Ciphertext { get; set; } = System.Array.Empty<byte>();
        public byte[] Signature { get; set; } = System.Array.Empty<byte>();
    }
}
