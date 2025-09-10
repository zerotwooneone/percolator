namespace Percolator.Chat.ValueObjects
{
    // SPKI-encoded public key bytes for an admin
    public readonly struct AdminPublicKey
    {
        public byte[] Bytes { get; }
        public AdminPublicKey(byte[] bytes)
        {
            // minimal validation; detailed validation occurs in cryptography layer
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("AdminPublicKey cannot be null or empty", nameof(bytes));
            Bytes = bytes;
        }
    }
}
