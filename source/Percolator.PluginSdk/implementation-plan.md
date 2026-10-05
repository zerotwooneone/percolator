# Percolator.PluginSdk Implementation Plan: Out-of-Band Encrypted Blob Storage & Transfer

## 1. Problem Definition & Architectural Motivation

### 1.1 The Double Ratchet 64 KB Payload Limit
In `Percolator.Application`, end-to-end encrypted direct channels and multi-party group channels operate over Double Ratchet and Signal Sender Key state machines with a strict invariant:
```csharp
public const int MaxPayloadBytes = 64 * 1024; // 64 KB
```
This constraint is essential for cryptographic channel health:
- Bounds memory footprint of in-flight message keys.
- Prevents transactional outbox bloat and head-of-line blocking in reliable delivery queues.
- Protects against memory exhaustion when advancing skipped-key ratchets.

### 1.2 Why `Percolator.Apps.FileTransfer` is the Wrong Fit for Chat Media
`Percolator.Apps.FileTransfer` is designed as a heavy, BitTorrent-like decentralized distribution mechanism for arbitrary multi-gigabyte files (Merkle trees, K-buckets, peer swarms). Using `FileTransfer` for a 1.5 MB photo or voice note introduces unacceptable latency, complex swarm discovery, and excessive protocol overhead.

### 1.3 The Solution: Single-Endpoint Encrypted Blob Storage in `PluginSdk`
`Percolator.PluginSdk` provides a high-level abstraction (`IBlobStorageService`) allowing any application plugin (`Apps.Chat`, `Apps.Discovery`, etc.) to:
1. Encrypt medium-sized blobs (1 KB to 50 MB) client-side with an ephemeral symmetric key using chunked streaming AEAD.
2. Pad media to discrete size buckets to eliminate traffic-analysis fingerprinting.
3. Transmit media based on **Channel Topology**:
   - **1:1 Direct (No Relay)**: Stream directly peer-to-peer (both peers must be online).
   - **1:1 Relayed**: Upload to the channel's designated relay for asynchronous delivery.
   - **Group Channel**: Upload **once** to the group's single common relay for all members to fetch.
4. Transmit a compact `BlobReference` inside the normal 64 KB E2EE ratchet envelope.
5. Download, verify, and decrypt out-of-band with byte-range resumption and zero Large Object Heap (LOH) memory bloat.

---

## 2. Cryptographic Security Model (Zero Server Knowledge)

```
Sender Client                          Channel Relay / Direct P2P              Recipient Client
┌───────────────────────┐                                                     ┌───────────────────────┐
│ Plaintext Media       │                                                     │ Decrypted Media       │
│ (Photo / Voice / GIF) │                                                     │ (Rendered in Chat UI) │
└──────────┬────────────┘                                                     └──────────▲────────────┘
           │ 1. Pad to discrete bucket                                                   │ 6. Decrypt chunk stream
           │    Generate K_blob (256-bit)                                                │    Verify tags per chunk
           │    STREAM Chunked AEAD                                                      │
           ▼                                                                             │
┌───────────────────────┐              2. Upload Ciphertext Blob              ┌──────────┴────────────┐
│ Ciphertext Stream     ├────────────────────────────────────────────────────►┌───────────────────────┐
│ (64 KB Chunks + Tags) │                          │ Channel Media Storage  │ │ Ciphertext Stream     │
│ + SHA-256 Digest      │                          │ (Zero Plaintext Access)│ │ (Downloaded & Cached) │
└───────────────────────┘                          │ Content-Addressed      │ └──────────▲────────────┘
                                                   │ Enforces Quotas & TTL  │            │
                                                   └──────────┬─────────────┘            │ 5. Resumable Download
                                                              │                          │    (Range requests)
                                                              │                          │    Verify chunk tags
┌───────────────────────┐   3. Send E2EE Chat Message         │                          │
│ Ratchet Envelope      ├─────────────────────────────────────┼──────────────────────────┘
│ (< 64 KB payload)     │   (carries BlobReference with       │
│                       │    K_blob, Hash, Size, MIME, TTL,   │
│                       │    inline BlurHash & thumbnail)     │
└───────────────────────┘                                     ▼
```

### 2.1 Cryptographic Guarantees
1. **Client-Side Envelope Encryption**:
   - For every uploaded blob, the sender generates a cryptographically secure 256-bit symmetric root key ($K_{\text{blob}}$) and a 96-bit base nonce ($N_{\text{base}}$).
   - Relays storing the ciphertext have zero access to $K_{\text{blob}}$, the plaintext, or the media MIME type.
2. **Chunked Streaming AEAD (STREAM Construction)**:
   - To eliminate Large Object Heap (LOH) allocations and enable streaming decryption, media is split into fixed $64\text{ KB}$ chunks.
   - Each chunk $i$ is encrypted with AES-256-GCM using a sequential counter nonce:
     $$\text{Nonce}_i = N_{\text{base}} \oplus i$$
   - Each chunk carries its own 16-byte authentication tag.
   - The recipient verifies and decrypts each chunk on the fly using pooled memory (`ArrayPool<byte>.Shared`), strictly bounding memory footprint to $\le 64\text{ KB}$ regardless of file size.
3. **Anti-Traffic-Analysis Padding**:
   - Plaintext media is padded using PKCS#7 or ISO/IEC 7816-4 to discrete bucket boundaries before encryption:
     - Files $< 1\text{ MB}$: Rounded up to nearest $32\text{ KB}$.
     - Files $1\text{ MB} - 10\text{ MB}$: Rounded up to nearest $256\text{ KB}$.
     - Files $> 10\text{ MB}$: Rounded up to nearest $1\text{ MB}$.
   - The exact unpadded length is carried securely inside the E2EE ratchet envelope.
4. **Content-Addressed Identifiers**:
   - The blob's identifier on the relay is the hex-encoded SHA-256 digest of the complete ciphertext:
     $$\text{BlobId} = \text{Hex}(\text{SHA-256}(\text{Ciphertext}))$$
   - Because the ciphertext is encrypted with a random 256-bit key, $\text{BlobId}$ is indistinguishable from random noise and acts as an unguessable capability token.
   - In group chats, the sender uploads once; all recipients download from the same content-addressed ID.
5. **Cryptographic Redaction**:
   - `BlobReference` overrides `ToString()` to redact `EncryptionKey` (`[REDACTED]`), preserving compliance with `CryptographyOptions.EnableCryptographicMaterialLogging = false`.

---

## 3. Channel Topologies & Media Transmission Expectations

We avoid complex federated distribution meshes or DHTs by grounding media transfer strictly in **Channel Topologies**:

```
                    ┌────────────────────────────────────────────────────────┐
                    │                    CHANNEL TOPOLOGY                    │
                    └───────────────────────────┬────────────────────────────┘
                                                │
                 ┌──────────────────────────────┼──────────────────────────────┐
                 ▼                              ▼                              ▼
     ┌───────────────────────┐      ┌───────────────────────┐      ┌───────────────────────┐
     │      1:1 Direct       │      │      1:1 Relayed      │      │     Group Channel     │
     │      (No Relay)       │      │                       │      │                       │
     │  - Both peers ONLINE  │      │  - Designated Relay   │      │  - Single Common Relay│
     │  - Ephemeral direct   │      │  - Asynchronous store │      │  - Single upload once │
     │    P2P streaming      │      │  - 14-day retention   │      │  - All members fetch  │
     └───────────────────────┘      └───────────────────────┘      └───────────────────────┘
```

### 3.1 Topology A: 1:1 Direct Channel (No Relay)
- **Operational Reality**: Media can **only** be transferred when **both parties are online concurrently**.
- **Behavior**:
  - If the peer is offline, the Chat UI disables the media upload button or queues the outbound message with an explicit status: *"Waiting for recipient to come online to stream media"*.
  - When both peers establish an active direct peer connection (QUIC / TCP), the sender streams encrypted $64\text{ KB}$ chunks directly to the recipient.
  - **Zero Relay Quota**: Consumes zero relay bandwidth or disk storage.

### 3.2 Topology B: 1:1 Relayed Channel
- **Operational Reality**: The channel is configured to route through a specific relay. **That designated relay is the sole media store for the channel.**
- **Behavior**:
  - The sender uploads the encrypted blob to the channel's designated relay.
  - The relay stores the blind ciphertext (content-addressed by SHA-256, subject to the relay's TTL, e.g. 14 days).
  - The sender sends the E2EE chat message with the `BlobReference` containing the relay endpoint and decryption key.
  - The recipient fetches the ciphertext asynchronously from that same relay upon coming online.

### 3.3 Topology C: Group Channel (Common Relay)
- **Operational Reality**: Group channels in Percolator communicate via a **Single Common Relay** (the channel's rendezvous/epoch host). **That common relay is the authoritative media store for all group members.**
- **Behavior (The Signal Model)**:
  - The sender uploads the encrypted, padded media blob **exactly once** to the group's common relay.
  - The sender distributes the `BlobReference` inside their group message using their **Sender Key** ratchet.
  - The common relay fans out the small (~1 KB) text envelope to all group members.
  - Group members render the inline BlurHash immediately, and stream the full media chunks from that same common relay (via manual tap-to-download or Wi-Fi auto-download).
  - The common relay enforces retention TTL (e.g. 14 days) and purges expired blobs via a background pruner.

---

## 4. Opt-In Relay Storage Roles (Servers & Individual Peers)

Nodes in Percolator can opt-in to act as media storage providers:

1. **Standalone Relays (Server Opt-In)**:
   - Relay operators configure their storage policy:
     - `EnableBlobStorage = true | false`
     - `MaxBlobSizeBytes = 50 * 1024 * 1024` (50 MB)
     - `StorageQuotaBytes = 100 * 1024 * 1024 * 1024` (100 GB)
     - `DefaultRetentionDays = 14`
   - If a relay disables blob storage, it functions as a lightweight text/control router. Clients attempting to send media through a storage-disabled relay receive an explicit `RelayBlobStorageDisabled` error.
2. **Individual Peers as Relays (Desktop Opt-In)**:
   - Users running Percolator on desktop nodes with unmetered connections can opt-in:
     *`[x] Act as Relay & Media Host for my groups and direct channels`*.
   - When enabled, their local node hosts the embedded relay blob service, allowing their contacts and groups to use their machine as the channel relay.

---

## 5. Blob Lifecycle, Retention & Local Cache Management

### 5.1 Expiration & Time-to-Live (TTL)
- Every stored blob on a relay specifies `ExpiresAtUtc` (default: 14 days).
- Once expired, the relay permanently deletes the ciphertext.
- **Inline Continuity**: The low-resolution thumbnail ($\le 2\text{ KB}$) and BlurHash string travel **inline** within the 64 KB ratchet envelope. Even after the out-of-band blob expires, the chat timeline permanently retains visual context.

### 5.2 Relay-Side Retention Pruning (`BlobRetentionPruner`)
- Relays run a background pruning worker to purge expired blobs.
- Watermark protection: If relay disk usage exceeds 90%, the oldest expired or near-expiration blobs are evicted on an LRU basis.

### 5.3 Client-Side Local Cache Management
- Downloaded media files are stored in a dedicated local LRU cache directory with a user-configurable limit (e.g. 1 GB to 5 GB).
- When disk usage exceeds the threshold, unpinned/oldest media files are evicted. (They can be re-fetched from the relay if still within the TTL window).
- When a message is deleted (tombstone) or expires via disappearing message timer, the local client invokes `DeleteLocalCacheAsync` to immediately shred local media files.

---

## 6. Architectural Allocation (Onion Architecture Layers)

Responsibilities are cleanly divided across layers without any distributed DHT or gossip:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Percolator.Apps.Chat                            │
│  • Checks channel capabilities (Direct Online-Only vs Relayed)         │
│  • Embeds BlobReference in ChatMessageDto; renders BlurHash & badges   │
│  • Calls PluginSdk IBlobStorageService.UploadBlobAsync / Download      │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ consumes
┌───────────────────────────────────▼────────────────────────────────────┐
│                        Percolator.PluginSdk                            │
│  • Public Contracts: IBlobStorageService                               │
│  • Public DTOs: BlobUploadRequest, BlobUploadResult, BlobReference     │
│  • Status Enums: BlobStorageMode (DirectP2P, ChannelRelay)             │
│  • Zero crypto engines, zero network clients, zero file I/O            │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ implemented by
┌───────────────────────────────────▼────────────────────────────────────┐
│                      Percolator.Application2                           │
│  • BlobTransferCoordinator:                                            │
│    - If DirectChannel (no relay): Stream over active peer transport.   │
│    - If Relayed or GroupChannel: Upload/Download to/from channel relay.│
│  • LocalClientLruCacheService: Enforces local 1–5 GB device disk limit │
│  • Opt-in Relay Host Engine: Manages local hosted relay storage & TTL   │
│  • Outbound Ports: IRelayBlobClient, IPeerStreamBlobClient, IChunkStore │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ implemented by
┌───────────────────────────────────▼────────────────────────────────────┐
│                     Percolator.Infrastructure2                         │
│  • RelayBlobClient: HTTP/2 / gRPC chunked upload/download (Range)      │
│  • PeerStreamBlobClient: Direct QUIC/TCP stream between online peers   │
│  • StreamingAesGcmCryptoService: 64KB chunked STREAM AEAD + ArrayPool  │
│  • LocalFileChunkStore: Atomic file writes (.tmp -> rename) in AppData │
└────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────┐
│                     Percolator.Relay (Server/Host)                     │
│  • POST /blobs (Upload ciphertext chunk stream)                        │
│  • GET /blobs/{sha256} (Download ciphertext with HTTP Range support)   │
│  • BlobRetentionPruner: Background cron purging expired TTL blobs      │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 7. Proposed `PluginSdk` Contracts & Interfaces

### 7.1 Blob Identification & Reference
```csharp
namespace Percolator.PluginSdk;

public readonly record struct BlobId(string HexDigest)
{
    public static BlobId FromSha256(byte[] sha256Bytes) => new(Convert.ToHexString(sha256Bytes).ToLowerInvariant());
    public override string ToString() => HexDigest;
}

public enum BlobStorageMode
{
    DirectP2P = 1,
    ChannelRelay = 2
}

/// <summary>
/// Lightweight descriptor carried inside E2EE application payloads (e.g. Chat ChatMessageDto).
/// Fits comfortably within the 64 KB Double Ratchet payload limit.
/// </summary>
public sealed record BlobReference
{
    public required BlobId Id { get; init; }
    public required byte[] CiphertextSha256 { get; init; }
    public required byte[] EncryptionKey { get; init; }
    public required byte[] BaseNonce { get; init; }
    public required long PlaintextSizeBytes { get; init; }
    public required long CiphertextSizeBytes { get; init; }
    public required string MimeType { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required BlobStorageMode StorageMode { get; init; }
    public string? RelayEndpointUri { get; init; }
    public string? BlurHash { get; init; }
    public byte[]? InlineThumbnail { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// Explicitly sanitizes key material from diagnostic logs.
    /// </summary>
    public override string ToString() =>
        $"BlobReference {{ Id = {Id}, StorageMode = {StorageMode}, MimeType = {MimeType}, " +
        $"PlaintextSize = {PlaintextSizeBytes}, CiphertextSize = {CiphertextSizeBytes}, " +
        $"ExpiresAt = {ExpiresAtUtc:u}, EncryptionKey = [REDACTED], BaseNonce = [REDACTED] }}";
}
```

### 7.2 Upload & Download Models
```csharp
public sealed record BlobUploadRequest(
    Stream ContentStream,
    string MimeType,
    long ContentLength,
    ChannelId ChannelId,
    TimeSpan? TimeToLive = null,
    string? BlurHash = null,
    byte[]? InlineThumbnail = null,
    int? Width = null,
    int? Height = null,
    int? DurationSeconds = null);

public sealed record BlobUploadResult(
    BlobReference Reference,
    BlobStorageMode StorageMode);

public sealed record BlobDownloadRequest(
    BlobReference Reference,
    CancellationToken CancellationToken = default);

public sealed record BlobDownloadResult(
    Stream ContentStream,
    string MimeType,
    long ContentLength);
```

### 7.3 Service Port (`IBlobStorageService`)
```csharp
public interface IBlobStorageService
{
    /// <summary>
    /// Encrypts (chunked STREAM AEAD) and transfers media based on channel topology:
    /// - 1:1 Direct (no relay): streams directly to connected peer (fails if peer offline).
    /// - 1:1 Relayed or GroupChannel: uploads once to the channel's designated relay.
    /// Returns a compact BlobReference suitable for embedding in an E2EE chat message.
    /// </summary>
    ValueTask<DomainResult<BlobUploadResult>> UploadBlobAsync(
        BlobUploadRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads, verifies chunk authentication tags, and decrypts the media payload.
    /// Uses local disk cache if previously downloaded. Supports byte-range resumption.
    /// </summary>
    ValueTask<DomainResult<BlobDownloadResult>> DownloadBlobAsync(
        BlobDownloadRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether the blob is currently available in the local cache without network I/O.
    /// </summary>
    bool IsCachedLocally(BlobId blobId);

    /// <summary>
    /// Removes an expired or deleted blob from local disk cache.
    /// </summary>
    ValueTask DeleteLocalCacheAsync(BlobId blobId, CancellationToken ct = default);
}
```

---

## 8. Implementation Safeguards & Edge Cases

1. **Zero Large Object Heap (LOH) Allocations**:
   - `StreamingAesGcmCryptoService` rents $64\text{ KB}$ byte buffers from `ArrayPool<byte>.Shared`.
   - Chunks are encrypted/decrypted in place or piped through streams, avoiding multi-megabyte contiguous arrays.
2. **Byte-Range Resumption with Integrity**:
   - Relays support HTTP `Range: bytes={start}-{end}`.
   - Because each $64\text{ KB}$ chunk contains its own 16-byte authentication tag, a resumed download resumes at the nearest chunk boundary and validates integrity chunk-by-chunk.
3. **Malicious / Corrupted Blobs**:
   - If a relay or man-in-the-middle corrupts a single chunk, AEAD verification fails immediately.
   - The download worker aborts, discards unverified data, and reports `BLOB_CHUNK_AUTH_FAILED`.
4. **Offline Peer in Direct 1:1**:
   - If a user attempts to upload a blob to a 1:1 direct channel without an active connected session, `UploadBlobAsync` immediately returns `DomainError.DirectPeerOffline("Media cannot be sent because peer is offline")`.
