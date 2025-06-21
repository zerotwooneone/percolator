namespace Percolator.Cryptography
{
    public class SenderKeyMessage
    {
        public SenderKeyHeader Header { get; set; } = new();
        public byte[]? Ciphertext { get; set; } = Array.Empty<byte>();
    }

    public class SenderKeyHeader
    {
        public uint Iteration { get; set; }

        public byte[] ToAssociatedData(byte[] context)
        {
            // It is critical that this serialization is stable and canonical.
            // The order and format must be identical for both sender and receiver.
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(context);
            writer.Write(Iteration);
            return stream.ToArray();
        }
    }
}
