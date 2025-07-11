using System.Text.Json.Serialization;
using System.IO;

namespace Percolator.Cryptography
{
    public class RatchetMessage
    {
        public RatchetHeader Header { get; set; } = new();
        public Ciphertext Ciphertext { get; set; } = new(Array.Empty<byte>());
    }

    public class RatchetHeader
    {
        [JsonInclude]
        public RatchetEphemeralKey RatchetKey { get; set; } = new(Array.Empty<byte>());

        [JsonInclude]
        public ulong Counter { get; set; }

        public byte[] ToAssociatedData()
        {
            // It is critical that this serialization is stable and canonical.
            // The order and format must be identical for both sender and receiver.
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(RatchetKey.Value);
            writer.Write(Counter);
            return stream.ToArray();
        }
    }
}
