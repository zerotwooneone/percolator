using System;
using System.Collections.Generic;

namespace Pecolator.Cryptography
{
    internal class SenderKeySessionState
    {
        public byte[] Context { get; set; } = Array.Empty<byte>();
        public byte[] SigningKeyPrivate { get; set; } = Array.Empty<byte>();
        public byte[] ChainKey { get; set; } = Array.Empty<byte>();
        public uint Iteration { get; set; }
        public Dictionary<uint, byte[]> SkippedMessageKeys { get; set; } = new();
    }
}
