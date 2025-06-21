using System;
using System.Collections.Generic;

namespace Percolator.Cryptography
{
    public class DoubleRatchetSessionState
    {
        public SessionRole Role { get; set; }
        public byte[] RootKey { get; set; } = Array.Empty<byte>();
        public byte[]? SendingChainKey { get; set; }
        public byte[]? ReceivingChainKey { get; set; }
        public int SendingCounter { get; set; }
        public int ReceivingCounter { get; set; }
        public Dictionary<uint, byte[]> SkippedMessageKeys { get; set; } = new();
        public byte[]? RemoteRatchetKeyBytes { get; set; }
        public byte[] RatchetKeyPrivate { get; set; } = Array.Empty<byte>();
    }
}