using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Percolator.Cryptography
{
    public class SenderKeySessionState
    {
        [JsonInclude]
        public byte[] SessionKey { get; set; } = Array.Empty<byte>();

        [JsonInclude]
        public byte[] Context { get; set; } = Array.Empty<byte>();

        [JsonInclude]
        public byte[] ChainKey { get; set; } = Array.Empty<byte>();

        [JsonInclude]
        public uint Iteration { get; set; }

        [JsonInclude]
        public Dictionary<uint, byte[]> MessageKeyCache { get; set; } = new();
    }
}