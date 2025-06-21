namespace Pecolator.Cryptography
{
    public class SenderKeyMessage
    {
        public uint Iteration { get; set; }
        public byte[]? Ciphertext { get; set; }
        public byte[]? Signature { get; set; } // For future use
    }
}
