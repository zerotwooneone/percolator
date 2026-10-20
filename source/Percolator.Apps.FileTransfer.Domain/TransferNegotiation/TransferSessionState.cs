namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public enum TransferSessionState : byte
{
    Offered = 0x01,          // Outgoing transfer created by sender; awaiting recipient connections
    PendingConsent = 0x02,   // Inbound transfer received and staged locally; zero wire response emitted
    InFlight = 0x03,         // Active transfer session; parallel streams exchanging chunks/bitfields
    Paused = 0x04,           // Active transfer temporarily paused; bitfield and manifest preserved
    Completed = 0x05,        // All selected files downloaded and verified against Merkle tree
    Seeding = 0x06,          // Serving completed chunks to active swarm peers based on SeedRatio
    Stalled = 0x07,          // Missing chunks across active swarm; waiting for seeders
    Cancelled = 0x08,        // Explicitly aborted by user or expired
    Failed = 0x09            // Unrecoverable cryptographic verification or domain invariant failure
}
