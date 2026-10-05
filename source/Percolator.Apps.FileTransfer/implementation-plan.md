# Percolator.Apps.FileTransfer Implementation Plan: Croc-Inspired High-Performance Desktop File Transfer

## 1. Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.FileTransfer` (`AppId = 0x03`).
- **Target Environment**: **Desktop only** (assumes multi-core processors, high-bandwidth unmetered networks, fast SSD/NVMe storage, large addressable memory, and sparse file OS support).
- **Core Inspiration**: **croc** (multi-stream parallel TCP/QUIC saturation, direct P2P connection with relay pipe fallback, directory tree bundling, chunked streaming, and bitfield-based resumption) combined with **BitTorrent swarm piece sharing, peer tracking, and selective downloads** in group channels.
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **strictly** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no raw TCP/UDP sockets, gRPC client stubs, SQLite, or OS filesystem APIs). Technical realization is delegated to `Percolator.Infrastructure2`.
  - Implements `IAppPlugin` (`AppId.FileTransferControl = 0x03`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
- **Protocol Separation (In-Band Signaling vs. Out-of-Band Data Streaming)**:
  - **In-Band Control Channel ($\le 64\text{ KB}$)**: Transfer session negotiations, manifest blob references, Merkle root hashes, session encryption keys, candidate network endpoints, and accept/reject decisions flow strictly through domain end-to-end encrypted ratchet channels (`DirectChannel` / `GroupChannel`). **Double Ratchet messages are never used for high-frequency or chatty swarm traffic**.
  - **Out-of-Band Data Streaming & Swarming**: Binary chunk transfers, `HAVE` announcements, `REQUEST_CHUNK` frames, and peer discoveries (PEX) run entirely out-of-band over **4 to 8 parallel encrypted streams** running over direct connections or relay proxy pipes.
- **Strict Onion Architecture & Network Privacy**:
  - Clearnet STUN servers and UPnP port forwarding are strictly prohibited to prevent physical IP and geolocation leakage.
  - Direct P2P is permitted **only for local LAN endpoints** (same private subnet).
  - All WAN / remote internet transfers route strictly through the **Channel's Designated Relay Transfer Pipe** or onion rendezvous.
- **Manifest Distribution via `IBlobStorageService`**:
  - Rather than inflating the in-band ratchet envelope or implementing custom out-of-band manifest chunking, the complete `TransferManifest` is stored via `Percolator.PluginSdk.IBlobStorageService`.
  - The in-band `FileTransferOfferDto` contains only high-level summary metrics (`RootName`, `TotalSizeBytes`, `TotalFileCount`, `TotalChunks`, `MerkleRootHash`, $K_{\text{transfer}}$) and a `BlobReference` pointing to the manifest.
  - The recipient downloads the manifest using the channel's standard blob storage transport (direct peer stream or relay) before disk pre-allocation.
- **Channel-Bound Designated Relay & Relay Operator Opt-In**:
  - File transfers inherit the designated relay of the active conversation where the transfer was initiated.
  - Relays explicitly advertise their capabilities via `Percolator.PluginSdk.RelayFileTransferCapabilities`:
    - `SupportsTransferPipes`: If false, the channel relay refuses out-of-band byte piping; transfers in that channel must be **Local LAN Direct Only**.
    - `SupportsSwarmTracker`: If false, the relay does not provide swarm rendezvous; peer discovery falls back to pure out-of-band PEX seeded by the offer initiator.
- **Selective File Downloads & BitTorrent Global Byte Model**:
  - Uses BitTorrent's continuous global byte layout across the bundle (1 MB chunks).
  - Users can select a subset of files from the manifest to download (`SelectedFileIndices`).
  - Chunks overlapping only unselected files are skipped. Boundary-spanning chunks are verified against the Merkle tree, but only the sub-spans for selected files are committed to disk.
- **No File Permissions / Attributes Across OS Boundaries**:
  - Cross-platform filesystem permissions (NTFS ACLs vs POSIX chmod 755/644) make no sense in P2P file transfers and create attack vectors. Files carry only `RelativePath`, `FileSizeBytes`, `StartByteOffsetInBundle`, and `LastModifiedUtc`.
- **Ephemeral Active Transfers vs. Chat History Reload**:
  - Active parallel streams, session encryption keys ($K_{\text{transfer}}$), and socket pools are ephemeral in-memory state.
  - On application reload: Incomplete or unaccepted transfers displayed in the chat log load in an inactive state (`Offer Expired`). Completed transfers remain as historic records with an "Open Folder" button.
  - Active transfer control (pause, resume, cancel, speed metrics, seed ratio) is managed in a dedicated **Transfer Manager View**, not inside the chat feed.

---

## 2. System Architecture & Component Interaction

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                       Shell / UI                                       │
│    ┌─────────────────────────┐                           ┌────────────────────────┐    │
│    │     Chat Feed View      │                           │ Transfer Dashboard UI  │    │
│    │ (Generic Timeline Card) │                           │ (Progress, Speed, I/O) │    │
│    └────────────┬────────────┘                           └───────────┬────────────┘    │
└─────────────────┼────────────────────────────────────────────────────┼─────────────────┘
                  │ Renders Card & Dispatches Actions                  │ Reads Stats & Controls
                  ▼                                                    ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                  Percolator.PluginSdk                                  │
│   • ITimelinePostSink (PublishCard, UpdateCardStatus, RemoveCard)                      │
│   • IBlobStorageService (Upload/Download TransferManifest via BlobReference)           │
│   • IPluginActionHandler & TimelineCardDto / TimelineActionDto                         │
│   • RelayFileTransferCapabilities                                                      │
└────────┬───────────────────────────────────────────────────────────────────────────────┘
         │ Routes Offers, Manifest Blobs, Card Updates
         ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                        Percolator.Apps.FileTransfer (AppId = 0x03)                     │
│   ┌──────────────────────────────────┐        ┌───────────────────────────────────┐    │
│   │   FileTransferPayloadHandler     │        │    TransferSessionCoordinator     │    │
│   │ (In-Band Ingress: Offers, ACKs)  │        │ (Stream Scheduler, Bitfields)     │    │
│   └─────────────────┬────────────────┘        └─────────────────┬─────────────────┘    │
│                     │                                           │                      │
│                     ▼                                           ▼                      │
│   ┌──────────────────────────────────┐        ┌───────────────────────────────────┐    │
│   │   MerkleTreeBuilder & Verifier   │        │     SwarmCoordinator & Tracker    │    │
│   │ (SHA-256 Leaves, Domain-Sep)     │        │ (Rarest-First, Have, PEX, Ratios) │    │
│   └─────────────────┬────────────────┘        └─────────────────┬─────────────────┘    │
│                     │                                           │                      │
│                     ▼                                           ▼                      │
│   ┌──────────────────────────────────┐        ┌───────────────────────────────────┐    │
│   │     TokenBucketRateLimiter       │        │       Selective File Mapper       │    │
│   │ (Global / Per-Transfer Caps)     │        │ (Chunk Bitmasks, Boundary Spans)  │    │
│   └──────────────────────────────────┘        └─────────────────┬─────────────────┘    │
└─────────────────────────────────────────────────────────────────┼──────────────────────┘
                                                                  │ Calls Outbound Ports
                                                                  ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                    Percolator.Infrastructure2 (Technical Realization)                  │
│   • ITransferStreamPool / ITransferStream (TcpMultiStreamPool, 4–8 parallel streams)   │
│   • IRelaySwarmTrackerClient (Out-of-band blind swarm rendezvous)                      │
│   • IDesktopFileStorage (DesktopSparseFileStore, .percolator-part, atomic rename)      │
│   • ITransferSessionRepository (Encrypted SQLite persistence)                          │
│   • ITransferRateLimiter (Bandwidth throttling engine)                                 │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Croc-Inspired Multi-Stream Engine & Transport Mechanics

### 3.1 Overcoming the Bandwidth-Delay Product (BDP)
Single TCP streams across high-latency internet routes suffer severe throughput degradation because TCP congestion windows cannot scale rapidly enough.
Following **croc**:
1. Every transfer session opens a pool of **4 to 8 parallel multiplexed streams** (`TransferStreamPool`).
2. Data is divided into uniform **1 MB chunks** (1,048,576 bytes).
3. Chunks are scheduled concurrently across all active streams in the pool.
4. Each stream uses independent congestion windows, saturating available bandwidth.

```
                             Transfer Session (SessionId, K_transfer)
                             ┌───────────────────────────────────────┐
                             │       TransferStreamPool (4–8)        │
                             └───┬────────┬────────┬────────┬────────┘
                                 │        │        │        │
            ┌────────────────────┼────────┴────────┼────────┴────────────────────┐
            ▼                    ▼                 ▼                             ▼
┌───────────────────────┐ ┌─────────────┐   ┌─────────────┐              ┌───────────────┐
│ Stream 1              │ │ Stream 2    │   │ Stream 3    │              │ Stream 4..8   │
│ Chunks: 0, 4, 8...    │ │ 1, 5, 9...  │   │ 2, 6, 10... │              │ 3, 7, 11...   │
│ Encrypted: AES-GCM    │ │ AES-GCM     │   │ AES-GCM     │              │ AES-GCM       │
└───────────────────────┘ └─────────────┘   └─────────────┘              └───────────────┘
```

### 3.2 Dual Transport Paths: LAN Direct-First with Channel Relay Pipe Fallback
When establishing the out-of-band transfer session:
1. **Local LAN Direct Connection (Primary if on same subnet)**:
   - Senders gather local private LAN IPv4/IPv6 candidates (`192.168.x.x`, `10.x.x.x`).
   - If peers detect they share a local subnet, all 4–8 streams connect directly over the local network.
   - **Clearnet WAN STUN/UPnP is strictly prohibited** to prevent IP leakage.
2. **Channel Relay Transfer Pipe (Fallback & Remote WAN)**:
   - All WAN transfers route out-of-band to the **Channel's Designated Relay Transfer Pipe**.
   - **Prerequisite**: Channel relay must advertise `SupportsTransferPipes = true` via `RelayFileTransferCapabilities`.
   - The relay acts as a blind multiplexed socket pump, matching the two peers by `SessionId` and proxying bytes between the streams.
   - **Zero Relay Knowledge**: Every chunk transferred across the relay pipe is end-to-end encrypted with an ephemeral 256-bit symmetric key ($K_{\text{transfer}}$) generated by the sender and transmitted exclusively via the Double Ratchet in-band offer. The relay cannot decrypt or inspect file contents.

---

## 4. Cryptographic Security & Onion Routing Privacy

### 4.1 Onion Routing Preservation vs. IP Leakage Guardrails
- **In-Band Signaling**: Flows strictly through the existing onion-routed, multi-hop or relay-encapsulated Double Ratchet channel. IP addresses are completely protected.
- **WAN Isolation**: Direct TCP connections across the internet are disabled to prevent peer IP exposure. Remote peers communicate out-of-band strictly through the channel's designated relay pipe.
- **Local LAN Exemption**: Direct P2P is permitted only when both peers verify they reside on the same private local network subnet.

### 4.2 Ephemeral Session Keys & Per-Chunk Encryption
- **Key Generation**: For every file transfer session, the sender cryptographically generates:
  $$K_{\text{transfer}} \leftarrow \text{CSPRNG}(32\text{ bytes})$$
  $$\text{BaseNonce} \leftarrow \text{CSPRNG}(12\text{ bytes})$$
- **Nonce Derivation**:
  To guarantee nonce uniqueness without re-negotiation overhead, chunk nonces are derived deterministically:
  $$\text{ChunkNonce}(i) = \text{BaseNonce} \oplus \text{BigEndianUInt96}(i)$$
- **Payload Ciphertext**:
  Each 1 MB chunk is encrypted using AES-256-GCM:
  $$C_i, T_i \leftarrow \text{AES-256-GCM-Encrypt}(K_{\text{transfer}}, \text{ChunkNonce}(i), \text{PlaintextChunk}_i, \text{AAD}_i)$$
  where $\text{AAD}_i = \text{SessionId} \parallel \text{BigEndianUInt32}(i)$.
  The 16-byte authentication tag $T_i$ is appended directly to the chunk ciphertext.

### 4.3 Merkle Tree Verification & Domain Separation
To defend against chunk substitution or second-preimage attacks:
- **Leaf Hashing**:
  $$H_{\text{leaf}}(i) = \text{SHA-256}(0x00 \parallel \text{PlaintextChunk}_i)$$
- **Interior Node Hashing**:
  $$H_{\text{interior}}(\text{Left}, \text{Right}) = \text{SHA-256}(0x01 \parallel \text{Left} \parallel \text{Right})$$
- **Root Verification**:
  - The root hash of the tree is included in the in-band `FileTransferOfferDto`.
  - The complete list of leaf hashes (`ChunkHashes`) is included in the manifest.
  - Before committing any chunk to disk, the recipient verifies:
    1. AES-256-GCM authentication tag verifies successfully.
    2. $\text{SHA-256}(0x00 \parallel \text{PlaintextChunk}_i) == \text{Manifest.ChunkHashes}[i]$.
  - Corrupt or tampered chunks are immediately discarded; the stream requests a re-transmission.

---

## 5. Directory Recursion, Manifest Distribution & Desktop Storage

### 5.1 Directory Trees, Path Sanitization & No Permissions
`Apps.FileTransfer` supports sending single files, multiple files, or deeply nested folder structures.
- **Directory Traversal Defense**:
  - All relative paths are strictly validated before acceptance.
  - Paths containing `..`, absolute paths (e.g. `/etc/passwd` or `C:\Windows`), null bytes, or Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`) are rejected immediately.
  - All paths are normalized using forward slashes (`/`) in the manifest wire format.
- **Elimination of File Permissions / Attributes**:
  - File permissions are **not** transmitted. Only file size, relative path, start offset, and last modified UTC timestamp are preserved.

### 5.2 Manifest Data Structure & Blob Storage Offloading
```csharp
public sealed record FileEntry(
    string RelativePath,              // e.g. "Source/Assets/diffuse.png"
    long FileSizeBytes,               // Exact byte length
    long StartByteOffsetInBundle,     // Global byte offset across the bundle
    DateTimeOffset LastModifiedUtc);  // Preserves file timestamp

public sealed record TransferManifest(
    Guid ManifestId,
    string RootName,                  // Display name (e.g. "Release_Build_v2")
    long TotalSizeBytes,              // Combined byte size of all files
    int TotalFileCount,               // Number of files in the bundle
    int ChunkSizeBytes,               // Standard 1 MB (1,048,576 bytes)
    int TotalChunks,                  // Total 1 MB chunks across the bundle
    byte[] MerkleRootHash,            // 32-byte root hash of chunk tree
    IReadOnlyList<FileEntry> Files,   // File structure
    IReadOnlyList<byte[]> ChunkHashes); // SHA-256 leaf hash per chunk
```

#### Manifest Distribution via `IBlobStorageService`:
- When Alice shares thousands of files, the serialized `TransferManifest` is stored via `Percolator.PluginSdk.IBlobStorageService`.
- The in-band `FileTransferOfferDto` contains `ManifestBlobRef = manifestRef`.
- Bob receives the offer card, clicks `[Download to...]`, and downloads the manifest blob via `_blobStorage.DownloadBlobAsync(offer.ManifestBlobRef)`.

### 5.3 Selective File Downloading Architecture
Following BitTorrent's continuous global byte model, selective downloading works smoothly without breaking Merkle tree verification:

1. **File Selection in UI**:
   - Recipient inspects manifest files and chooses `IReadOnlySet<int> SelectedFileIndices` (or selects all).
2. **Byte Range to Chunk Mapping**:
   - For each selected file $f$, its byte span in the bundle is:
     $$[\text{StartOffset}_f, \text{StartOffset}_f + \text{SizeBytes}_f)$$
   - The set of required chunks is the union of chunk indices overlapping all selected files:
     $$\text{RequiredChunks} = \bigcup_{f \in \text{SelectedFiles}} \left\{ k \;\middle|\; k \times \text{ChunkSize} < \text{EndOffset}_f \;\land\; (k + 1) \times \text{ChunkSize} > \text{StartOffset}_f \right\}$$
3. **Scheduler & Bitfield Logic**:
   - Chunks **not** in $\text{RequiredChunks}$ are marked as `Ignored`. The scheduler never requests them.
   - Chunks in $\text{RequiredChunks}$ are requested and scheduled across streams as usual.
4. **Boundary Spanning Chunks**:
   - If Chunk $k$ straddles the boundary between an unselected file and a selected file, Chunk $k$ is still downloaded and verified against the Merkle tree.
   - The storage port only writes the sub-span corresponding to the selected file to disk.
5. **Disk Pre-Allocation**:
   - The storage port only pre-allocates files that were checked by the user, saving local disk space.

### 5.4 Multi-File Boundary Spanning Math
When files do not align exactly with 1 MB boundaries, a chunk may contain the tail of `File A` and the head of `File B`:
```
Chunk k (1 MB):
┌─────────────────────────────────┬──────────────────────────────────┐
│ Tail of File A (300 KB)         │ Head of File B (724 KB)          │
└─────────────────────────────────┴──────────────────────────────────┘
```
The domain storage coordinator maps the chunk span:
```csharp
long chunkStart = (long)chunkIndex * chunkSize;
long chunkRemaining = actualChunkSize;
long currentOffset = chunkStart;
int bufferOffset = 0;

while (chunkRemaining > 0)
{
    var file = FindFileForOffset(manifest.Files, currentOffset);
    long offsetInFile = currentOffset - file.StartByteOffsetInBundle;
    long bytesForThisFile = Math.Min(chunkRemaining, file.FileSizeBytes - offsetInFile);

    if (selectedFileIndices.Contains(file.FileIndex))
    {
        var span = chunkBuffer.Slice(bufferOffset, (int)bytesForThisFile);
        await storage.WriteChunkAsync(file.RelativePath, offsetInFile, span, ct);
    }

    currentOffset += bytesForThisFile;
    bufferOffset += (int)bytesForThisFile;
    chunkRemaining -= bytesForThisFile;
}
```

---

## 6. BitTorrent Swarming, Hybrid Trackers & Seeding Lifecycle

### 6.1 Multi-Seeder Swarm Independence
- Unlike traditional 1:1 file transfer, Percolator's group file sharing creates an ad-hoc BitTorrent swarm.
- **Independence from Original Sender**: Once Alice uploads chunks to Bob and Charlie, Alice can go offline. Dave can join the swarm later and download chunks from Bob and Charlie.
- **Rarest-First Scheduling**: Peers inspect the bitfields of all connected swarm members and prioritize requesting chunks that have the fewest copies available in the swarm.
- **Endgame Mode**: When a peer has 95% of chunks downloaded and only a few remain, it broadcasts requests for the remaining missing chunks to all available seeders simultaneously. The first seeder to deliver wins; the peer sends `CANCEL_CHUNK` to the other seeders.

### 6.2 Swarm Discovery: Hybrid Blind Relay Tracker + Out-of-Band PEX
To eliminate chatty in-band ratchet messages while ensuring peers can find each other even when the original sender is offline:

1. **Relay Blind Swarm Tracker (If Relay Opts-In)**:
   - **Blind Swarm ID**: Senders and downloaders compute a blind identifier:
     $$\text{BlindSwarmId} = \text{HMAC-SHA256}(K_{\text{transfer}}, \text{"swarm-rendezvous"})$$
     The relay cannot decrypt file contents, see filenames, or link the swarm to a specific chat message.
   - Senders and downloaders announce via `IRelaySwarmTrackerClient.AnnounceAsync(...)` out-of-band.
   - The relay returns a list of active candidate endpoints for that swarm.
2. **Decentralized Peer Exchange (PEX) Across Out-of-Band Streams**:
   - Connected peers gossip active swarm connections using `0x0A PEX_PEERS` frames.
   - If the channel relay has disabled tracker support, peers use PEX exclusively, starting from the candidate endpoints provided in the initial offer.
3. **Zero Ratchet Overhead**:
   - Double Ratchet channels are **never** used for peer heartbeats, `HAVE` broadcasts, or connection handshakes.

### 6.3 Seeding Lifecycle & Ratio Controls
- Users can stop seeding at any time via the Transfer Manager UI.
- Configurable **Seed Ratio Targets**:
  - Global default setting: `DefaultSeedRatio` (e.g. `1.0` = seed until upload bytes equal download bytes, `2.0`, or `Unlimited`).
  - Per-transfer override option on initiation or download.
  - Calculated ratio:
    $$\text{Ratio} = \frac{\text{BytesUploaded}}{\text{TotalSizeBytes}}$$
  - When $\text{Ratio} \ge \text{TargetRatio}$, the session automatically transitions to `SeedingComplete` and gracefully closes out-of-band streams.

---

## 7. Decoupled Chat Timeline & Transfer Manager UI

### 7.1 Separation of Responsibilities
1. **Chat Feed (Timeline Card)**:
   - Acts as an **ingress offer invitation** in the conversation stream.
   - Uses `Percolator.PluginSdk.TimelineCardDto` published via `ITimelinePostSink`.
   - On app reload, pending/unaccepted offers load as `Offer Expired`. Completed transfers remain as historic notices with an `[Open Folder]` button.
2. **Dedicated Transfer Manager Dashboard (UI View)**:
   - A dedicated tab/window in the shell for monitoring all file transfers across all channels.
   - Consumes `IFileTransferQueryService` and `IFileTransferActionHandler`.
   - Provides live controls: Pause, Resume, Cancel, Change Speed Cap, Adjust Seed Ratio, View Stream Health, Selective File Toggles, and Open Folder.

---

## 8. Wire Protocols & Framing Specifications

### 8.1 In-Band Control Channel Envelope ($\le 64\text{ KB}$)
Carried inside the Double Ratchet envelope with `AppId.FileTransferControl = 0x03`:

```csharp
public enum FileTransferMessageType : byte
{
    Offer = 0x01,
    Accept = 0x02,
    Reject = 0x03,
    Cancel = 0x04
}

public sealed record EndpointCandidateDto(
    string Host,                      // Local LAN IPv4/IPv6, or Relay Pipe Uri
    int Port,                         // TCP port
    string CandidateType);            // "lan", "relay_pipe"

public sealed record FileTransferOfferDto(
    Guid TransferSessionId,
    string RootName,
    long TotalSizeBytes,
    int TotalFileCount,
    int ChunkSizeBytes,
    int TotalChunks,
    byte[] MerkleRootHash,
    BlobReference ManifestBlobRef,     // Reference to full manifest in IBlobStorageService
    byte[] SessionEncryptionKey,      // 32-byte AES key
    byte[] BaseNonce,                 // 12-byte base nonce
    byte[] StreamAuthToken,           // 32-byte auth token for out-of-band streams
    byte[] RelayPipeToken,            // 32-byte auth token for relay pipe rendezvous
    string? RelayPipeUri,             // Inherited from the conversation's designated relay
    bool RelaySupportsTransferPipes,   // Flagged from channel relay capabilities
    IReadOnlyList<EndpointCandidateDto> DirectCandidates);

public sealed record FileTransferAcceptDto(
    Guid TransferSessionId,
    IReadOnlyList<EndpointCandidateDto> ReceiverCandidates,
    bool ConnectViaRelayPipe,
    byte[] ResumptionBitfield);        // Bitmap of chunks already possessed

public sealed record FileTransferRejectDto(
    Guid TransferSessionId,
    string Reason);

public sealed record FileTransferCancelDto(
    Guid TransferSessionId,
    string Reason);
```

### 8.2 Out-of-Band Stream Protocol (Binary Framing over 4–8 Streams)
Each stream in the `TransferStreamPool` exchanges length-prefixed binary frames:

```
Frame Layout:
┌───────────────────────────┬────────────────────┬───────────────────────────────────────┐
│ Length (4 Bytes, Big End) │ Type (1 Byte)      │ Payload (Length - 1 Bytes)            │
└───────────────────────────┴────────────────────┴───────────────────────────────────────┘
```

#### Frame Types:
1. **`0x01 STREAM_HANDSHAKE`**:
   - Sent upon opening each stream.
   - Payload: `[Magic: 4B = 0x50435243 ('PCRC')] [Version: 1B = 0x01] [SessionId: 16B] [StreamIndex: 1B] [StreamAuthToken: 32B]`.
   - Response: `0x01 HANDSHAKE_ACK` (`Status: 1B` where `0x00 = OK`).
2. **`0x02 BITFIELD`**:
   - Declares piece availability.
   - Payload: `[BitfieldBytes: Variable]` (Length = $\lceil \text{TotalChunks} / 8 \rceil$).
3. **`0x03 HAVE`**:
   - Broadcast when a peer verifies and commits a chunk to disk.
   - Payload: `[ChunkIndex: 4B big-endian]`.
4. **`0x04 REQUEST_CHUNK`**:
   - Requests a 1 MB chunk from the peer.
   - Payload: `[ChunkIndex: 4B big-endian]`.
5. **`0x05 CHUNK_DATA`**:
   - Delivers encrypted chunk data.
   - Payload: `[ChunkIndex: 4B big-endian] [CiphertextLength: 4B big-endian] [CiphertextAndTag: Variable]`.
6. **`0x06 CANCEL_CHUNK`**:
   - Cancels a previously requested chunk (used in Endgame mode).
   - Payload: `[ChunkIndex: 4B big-endian]`.
7. **`0x07 CHOKE / 0x08 UNCHOKE`**:
   - Flow control signaling.
8. **`0x09 KEEP_ALIVE`**:
   - Empty payload frame sent every 15 seconds of silence.
9. **`0x0A PEX_PEERS`**:
   - Peer Exchange frame: shares known active swarm peers out-of-band.
   - Payload: `[PeerCount: 2B] { [PeerId: 32B] [EndpointType: 1B] [HostLen: 1B] [Host: string] [Port: 2B] }*`.

---

## 9. Required Outbound Port Definitions (`Percolator.Apps.FileTransfer.Ports`)

The domain and coordination logic of `Percolator.Apps.FileTransfer` calls the following ports (implemented by `Percolator.Infrastructure2`):

### 9.1 Network Stream Pool & Relay Tracker Ports
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public sealed record FrameEnvelope(byte FrameType, ReadOnlyMemory<byte> Payload);

public interface ITransferStream : IAsyncDisposable
{
    int StreamIndex { get; }
    ValueTask SendFrameAsync(byte frameType, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
    ValueTask<FrameEnvelope> ReceiveFrameAsync(CancellationToken ct = default);
}

public interface ITransferStreamPool : IAsyncDisposable
{
    int ActiveStreamCount { get; }
    ValueTask ConnectDirectAsync(IReadOnlyList<EndpointCandidateDto> candidates, byte[] authToken, CancellationToken ct = default);
    ValueTask ConnectRelayPipeAsync(string relayPipeUri, Guid sessionId, byte[] relayToken, CancellationToken ct = default);
    ITransferStream GetStream(int index);
    ValueTask BroadcastFrameAsync(byte frameType, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}

public interface ITransferStreamPoolFactory
{
    ITransferStreamPool CreatePool(Guid sessionId, int streamCount = 4);
}

public interface IRelaySwarmTrackerClient
{
    ValueTask<IReadOnlyList<EndpointCandidateDto>> AnnounceAsync(
        string relayUri,
        byte[] blindSwarmId,
        PublicIdentityId peerId,
        IReadOnlyList<EndpointCandidateDto> candidates,
        bool isSeeder,
        CancellationToken ct = default);
}
```

### 9.2 Desktop File Storage Port
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface IDesktopFileStorage
{
    ValueTask PreAllocateFilesAsync(string destinationRoot, IReadOnlyList<FileEntry> files, IReadOnlySet<int> selectedIndices, CancellationToken ct = default);
    ValueTask WriteChunkAsync(string destinationRoot, string relativePath, long fileOffset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(string sourceRoot, string relativePath, long fileOffset, int length, CancellationToken ct = default);
    ValueTask<byte[]> ScanAndComputeBitfieldAsync(string rootPath, TransferManifest manifest, CancellationToken ct = default);
    ValueTask FinalizeFileAsync(string destinationRoot, string relativePath, DateTimeOffset lastModifiedUtc, CancellationToken ct = default);
    ValueTask<bool> HasSufficientFreeSpaceAsync(string rootPath, long requiredBytes);
    void RevealInFileManager(string targetPath);
}
```

### 9.3 Rate Limiter Port
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferRateLimiter
{
    void SetGlobalSpeedCaps(long? maxDownloadBytesPerSec, long? maxUploadBytesPerSec);
    void SetSessionSpeedCaps(Guid sessionId, long? maxDownloadBytesPerSec, long? maxUploadBytesPerSec);
    ValueTask ThrottleDownloadAsync(Guid sessionId, int byteCount, CancellationToken ct = default);
    ValueTask ThrottleUploadAsync(Guid sessionId, int byteCount, CancellationToken ct = default);
}
```

### 9.4 Transfer Repository Port
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferSessionRepository
{
    ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default);
    ValueTask<TransferSession?> GetSessionByIdAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TransferSession>> GetActiveSessionsAsync(CancellationToken ct = default);
    ValueTask SaveBitfieldAsync(Guid sessionId, byte[] bitfield, int completedChunks, CancellationToken ct = default);
    ValueTask<byte[]?> GetBitfieldAsync(Guid sessionId, CancellationToken ct = default);
}
```

### 9.5 CQRS Transfer Manager Ports
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public sealed record TransferSessionReadModel(
    Guid SessionId,
    ChannelId ChannelId,
    string RootName,
    long TotalBytes,
    long TransferredBytes,
    long UploadedBytes,
    double SeedRatio,
    double TargetSeedRatio,
    int CompletedChunks,
    int TotalChunks,
    double DownloadSpeedBytesPerSec,
    double UploadSpeedBytesPerSec,
    int ConnectedStreamCount,
    int ConnectedPeerCount,
    string Status, // "Offered", "Downloading", "Seeding", "Paused", "Completed", "Cancelled"
    bool IsOutgoing,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public interface IFileTransferQueryService
{
    ValueTask<IReadOnlyList<TransferSessionReadModel>> GetAllTransfersAsync(CancellationToken ct = default);
    ValueTask<TransferSessionReadModel?> GetTransferByIdAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<FileEntry>> GetManifestFilesAsync(Guid sessionId, CancellationToken ct = default);
}

public interface IFileTransferActionHandler
{
    ValueTask AcceptTransferAsync(Guid sessionId, string destinationFolder, IReadOnlySet<int>? selectedFileIndices = null, CancellationToken ct = default);
    ValueTask PauseTransferAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask ResumeTransferAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask StopSeedingAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask CancelTransferAsync(Guid sessionId, string reason, CancellationToken ct = default);
    ValueTask SetSessionSpeedCapAsync(Guid sessionId, long? downloadCap, long? uploadCap, CancellationToken ct = default);
    ValueTask SetSessionSeedRatioAsync(Guid sessionId, double targetRatio, CancellationToken ct = default);
    ValueTask RevealInExplorerAsync(Guid sessionId, CancellationToken ct = default);
}
```

---

## 10. Security, Privacy & Threat Analysis

| Threat / Risk | Attack Vector | Mitigation in Architecture |
|---|---|---|
| **Directory Traversal** | Malicious manifest with `../../Windows/System32/evil.dll` | Strict sanitization: reject `..`, root paths, null bytes, and control chars before creating files. |
| **Disk Exhaustion** | 100 GB transfer offered to fill victim's disk | Recipient calls `HasSufficientFreeSpaceAsync` before accepting; pre-allocates files so disk space failures trigger upfront. |
| **Antivirus / OS File Locks** | Defender/Indexers locking incomplete files during write | `.percolator-part` staging files; atomic rename only upon full chunk verification. |
| **Tampered / Corrupted Chunks** | Man-in-the-middle or malicious peer injecting bad blocks | Dual verification: AES-256-GCM authentication tag check + SHA-256 leaf hash verification against Merkle tree before disk write. |
| **Relay Bandwidth Exhaustion** | Unmetered relay socket proxying overwhelming host | Relay operator opt-in flags (`SupportsTransferPipes = false` disables proxying; enforced LAN P2P only). |
| **IP Address Leakage** | Direct WAN P2P exposes IP address of peer on untrusted network | Strict Onion Rule: Clearnet STUN/UPnP prohibited; WAN transfers route exclusively through blind relay pipes. |
| **Relay Snooping** | Relay inspecting or caching transferred files | End-to-end encryption ($K_{\text{transfer}}$ exchanged solely via in-band ratchet); relay is a blind memory-only socket pump with zero disk storage. |
| **In-Band Ratchet Bloat** | Chatty swarm announcements thrashing ratchet state | Ratchet envelope touched only once (initial offer); all swarm tracking (blind tracker / PEX) runs out-of-band. |
| **Network Bufferbloat / Choke** | Unthrottled multi-stream file transfers degrading audio/chat | Token-bucket rate limiting (`ITransferRateLimiter`) with configurable global and per-transfer upload/download caps. |

---

## 11. Phased Implementation Milestones

### Milestone 1: Plugin Registration, Manifest Models & Blob Storage Integration
- **Scope**: `Percolator.Apps.FileTransfer` domain models and manifest storage.
- **Tasks**:
  1. Implement `FileTransferPlugin` implementing `IAppPlugin` (`AppId.FileTransferControl = 0x03`).
  2. Implement `TransferManifest`, `FileEntry`, and `MerkleTreeBuilder` (SHA-256 domain-separated hashing).
  3. Integrate `IBlobStorageService` to upload and fetch manifests via `BlobReference`.
  4. Implement in-band DTOs: `FileTransferOfferDto`, `FileTransferAcceptDto`, `FileTransferCancelDto`.
  5. Implement `FileTransferPayloadHandler` for ingress processing.
  6. Unit tests: Manifest generation, Merkle root verification, serialization roundtrips, blob manifest roundtrips.

### Milestone 2: Croc-Inspired Multi-Stream Coordinator & Out-of-Band Engine
- **Scope**: Parallel stream coordination and piece scheduling.
- **Tasks**:
  1. Define ports: `ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`.
  2. Implement `TransferSessionCoordinator` managing active 4–8 streams.
  3. Implement out-of-band binary framing serializer/deserializer (`0x01`–`0x0A`).
  4. Implement concurrent chunk request pipeline (keeping 4–8 requests in flight per stream).
  5. Unit tests: Stream pool scheduling, chunk completion tracking, simulated stream stall failover.

### Milestone 3: Desktop Disk I/O, Sparse Allocation & Selective Downloading
- **Scope**: Storage port abstraction, `.percolator-part` staging, and selective file downloads.
- **Tasks**:
  1. Define `IDesktopFileStorage` port.
  2. Implement `DiskChunkAssembler` handling multi-file boundary spanning, chunk writes, and atomic finalization.
  3. Implement selective file downloading chunk range mapping (`SelectedFileIndices`).
  4. Implement `BitfieldScanner` for resumption of partially completed downloads.
  5. Unit tests: Boundary spanning math, selective chunk ranges, sparse allocation, atomic rename verification.

### Milestone 4: Group Swarm Coordination, Relay Tracker & Seeding Ratios
- **Scope**: Swarm coordination, relay tracker client, PEX, and seeding lifecycle.
- **Tasks**:
  1. Implement `SwarmCoordinator` tracking peer bitfields and active swarm members.
  2. Implement `RarestFirstChunkSelector` and `EndgameMode`.
  3. Implement `IRelaySwarmTrackerClient` and `PEX_PEERS` out-of-band frame handling.
  4. Implement seeding ratio tracking ($\text{Uploaded} / \text{Total}$) and automatic disconnect on target ratio.
  5. Unit tests: Multi-peer chunk scheduling, rarest-first verification, endgame cancel handling, ratio auto-stop.

### Milestone 5: Timeline Cards, Rate Limiter & Transfer Manager UI
- **Scope**: Timeline integration, bandwidth throttling, and CQRS read/write controls.
- **Tasks**:
  1. Consume `ITimelinePostSink`, `TimelineCardDto`, `TimelineActionDto` from `Percolator.PluginSdk`.
  2. Implement timeline card emission upon sending/receiving offers, with `Offer Expired` reload behavior.
  3. Implement rate limiter adapter integration with `ITransferRateLimiter`.
  4. Implement `IFileTransferQueryService` and `IFileTransferActionHandler` for the Transfer Manager UI.
  5. Unit tests: Timeline card lifecycle transitions, action dispatching, rate limiter throttling accuracy.

### Milestone 6: Infrastructure Adapter Integration & End-to-End Verification
- **Scope**: End-to-end integration with `Percolator.Infrastructure2` ports.
- **Tasks**:
  1. Wire `FileTransferPlugin` with `TcpMultiStreamPool` and `DesktopSparseFileStore` from `Infrastructure2`.
  2. Test local LAN P2P multi-stream gigabit transfer.
  3. Test blind relay transfer pipe streaming with simulated latency and packet drop.
  4. Test swarm multi-peer piece sharing with 3+ desktop nodes.
