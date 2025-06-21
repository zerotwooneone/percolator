using System;
using System.Collections.Generic;

namespace Percolator.Cryptography
{
    public class SenderKeySessionState
    {
        public byte[] Context { get; set; } = Array.Empty<byte>();
        public byte[] SigningKeyPrivate { get; set; } = Array.Empty<byte>();
        public byte[] SigningKeyPublic { get; set; } = Array.Empty<byte>();
        public uint Iteration { get; set; }
        public byte[] ChainKey { get; set; } = Array.Empty<byte>();
        public Dictionary<uint, byte[]> MessageKeyCache { get; set; } = new();
    }
}