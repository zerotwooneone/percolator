namespace Percolator.Cryptography
{
    // Cryptography-domain value object for group key material
    public sealed class GroupKeyMaterial
    {
        public byte[] Value { get; }
        public GroupKeyMaterial(byte[] value)
        {
            Value = value ?? throw new System.ArgumentNullException(nameof(value));
            if (Value.Length == 0) throw new System.ArgumentException("Group key material cannot be empty.", nameof(value));
        }
    }
}
