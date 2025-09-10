namespace Percolator.Chat.ValueObjects
{
    // SPKI-encoded identity public key bytes
    public readonly struct IdentityPublicKey
    {
        public byte[] Bytes { get; }
        public IdentityPublicKey(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new System.ArgumentException("IdentityPublicKey cannot be null or empty", nameof(bytes));
            Bytes = bytes;
        }
    }
}
