using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Percolator.Cryptography
{
    public class SenderKeySessionState
    {
        [JsonInclude]
        public byte[] SessionKey { get; set; } = null!;

        [JsonInclude]
        public byte[] Context { get; set; } = null!;

        [JsonInclude]
        public byte[] SigningKeyPrivate { get; set; } = null!;

        [JsonInclude]
        public byte[] SigningKeyPublic { get; set; } = Array.Empty<byte>();

        [JsonInclude]
        public uint Iteration { get; set; }

        [JsonInclude]
        public byte[] ChainKey { get; set; } = null!;

        [JsonInclude]
        public Dictionary<uint, byte[]> MessageKeyCache { get; set; } = null!;
    }
}