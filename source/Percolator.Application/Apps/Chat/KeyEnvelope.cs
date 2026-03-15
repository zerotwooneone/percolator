namespace Percolator.Application.Apps.Chat
{
    // Minimal AEAD envelope for per-recipient group key distribution.
    // Layout (big-endian lengths):
    // [ver:1][alg:2][nonceLen:1][nonce:N][tagLen:1][tag:T][cipherLen:4][cipher:C]
    internal sealed class KeyEnvelope
    {
        public byte Version { get; }
        public ushort AlgorithmId { get; }
        public byte[] Nonce { get; }
        public byte[] Tag { get; }
        public byte[] Ciphertext { get; }

        private KeyEnvelope(byte version, ushort algorithmId, byte[] nonce, byte[] tag, byte[] cipher)
        {
            Version = version;
            AlgorithmId = algorithmId;
            Nonce = nonce;
            Tag = tag;
            Ciphertext = cipher;
        }

        public static KeyEnvelope Parse(ReadOnlySpan<byte> blob)
        {
            if (blob.Length < 1 + 2 + 1 + 1 + 4)
                throw new FormatException("KeyEnvelope too short");

            int offset = 0;
            byte ver = blob[offset++];
            ushort alg = (ushort)((blob[offset++] << 8) | blob[offset++]);

            byte nLen = blob[offset++];
            if (nLen == 0 || offset + nLen > blob.Length) throw new FormatException("Invalid nonce length");
            var nonce = blob.Slice(offset, nLen).ToArray();
            offset += nLen;

            byte tLen = blob[offset++];
            if (tLen == 0 || offset + tLen > blob.Length) throw new FormatException("Invalid tag length");
            var tag = blob.Slice(offset, tLen).ToArray();
            offset += tLen;

            if (offset + 4 > blob.Length) throw new FormatException("Missing ciphertext length");
            uint cLen = (uint)(blob[offset++] << 24 | blob[offset++] << 16 | blob[offset++] << 8 | blob[offset++]);
            if (cLen == 0 || offset + cLen > blob.Length) throw new FormatException("Invalid ciphertext length");
            var cipher = blob.Slice(offset, (int)cLen).ToArray();
            offset += (int)cLen;

            if (offset != blob.Length) throw new FormatException("Trailing bytes in KeyEnvelope");

            return new KeyEnvelope(ver, alg, nonce, tag, cipher);
        }
    }
}
