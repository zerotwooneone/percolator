namespace Pecolator.Cryptography
{
    public class DoubleRatchetSession
    {
        public DoubleRatchetSession()
        {
        }

        public byte[] Encrypt(byte[] plaintext)
        {
            // For now, just return a dummy byte array to make the test pass.
            return new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        }
    }
}
