using System.Text.Json.Serialization;

namespace Percolator.Cryptography
{
    public class RatchetMessage
    {
        public RatchetHeader Header { get; set; } = null!;
        public byte[] Ciphertext { get; set; } = null!;
    }

    public class RatchetHeader
    {
        [JsonInclude]
        public byte[] RatchetKey { get; set; } = null!;

        [JsonInclude]
        public ulong Counter { get; set; }

        public byte[] ToAssociatedData()
        {
            // It is critical that this serialization is stable and canonical.
            // The order and format must be identical for both sender and receiver.
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(RatchetKey);
            writer.Write(Counter);
            return stream.ToArray();
        }
    }
}
