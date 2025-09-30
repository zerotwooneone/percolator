using System;

namespace Percolator.Application.Handshake
{
    // Configurable options for opaque handshake processing (Step 16)
    public sealed class HandshakeOptions
    {
        // Freshness skew window for sent_timestamp_utc
        public TimeSpan TimestampSkewWindow { get; init; } = TimeSpan.FromMinutes(2);

        // Enable nonce usage for replay resistance within the skew window
        public bool EnableNonce { get; init; } = true;

        // Maximum handshake attempts per remote peer per minute
        public int MaxPerPeerPerMinute { get; init; } = 60;

        // Maximum handshake attempts per node per minute
        public int MaxPerNodePerMinute { get; init; } = 600;

        // Size bounds (pre-crypto validation)
        public int MaxSpkiBytes { get; init; } = 1536; // ~1.5KB
        public int MaxPayloadBytes { get; init; } = 8192; // 8KB
    }
}
