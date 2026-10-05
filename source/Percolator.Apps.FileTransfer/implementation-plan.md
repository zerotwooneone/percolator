# Percolator.Apps.FileTransfer Implementation Plan: Croc-Inspired High-Performance Desktop File Transfer

## 1. Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.FileTransfer` (`AppId = 0x03`).
- **Target Environment**: **Desktop only** (assumes multi-core processors, high-bandwidth unmetered networks, fast SSD/NVMe storage, large addressable memory, and sparse file OS support).
- **Core Inspiration**: **croc** (multi-stream parallel TCP/QUIC saturation, direct P2P connection with relay pipe fallback, directory tree bundling, chunked streaming, and bitfield-based resumption) combined with **BitTorrent swarm piece sharing, peer tracking, and selective downloads** in group channels.
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **strictly** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no raw TCP/UDP sockets, gRPC client stubs, SQLite, or OS filesystem APIs). Technical realization is delegated to `Percolator.Infrastructure2`.
  - Implements `IAppPlugin` (`AppId.FileTransfer = 0x03`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
- **Protocol Separation (In-Band Signaling vs. Out-of-Band Data Streaming)**:
  - **In-Band Control Channel ($\le 64\text{ KB}$)**: Transfer session negotiations, manifest blob references, Merkle root hashes, session encryption keys, candidate network endpoints, and accept/reject decisions flow strictly through domain end-to-end encrypted ratchet channels (`DirectChannel` / `GroupChannel`). **Double Ratchet messages are never used for high-frequency or chatty swarm traffic**.
  - **Out-of-Band Data Streaming & Swarming**: Binary chunk transfers, `HAVE` announcements, `REQUEST_CHUNK` frames, and peer discoveries (PEX) run entirely out-of-band over **4 to 8 parallel encrypted streams** running over direct TCP/QUIC connections or relay proxy pipes.
- **Manifest Distribution via `IBlobStorageService`**:
  - Rather than inflating the in-band ratchet envelope or implementing custom out-of-band manifest chunking, the complete `TransferManifest` is stored via `Percolator.PluginSdk.IBlobStorageService`.
  - The in-band `FileTransferOfferDto` contains only high-level summary metrics (`RootName`, `TotalSizeBytes`, `TotalFileCount`, `TotalChunks`, `MerkleRootHash`, $K_{\text{transfer}}$) and a `BlobReference` pointing to the manifest.
  - The recipient downloads the manifest using the channel's standard blob storage transport (direct peer stream or relay) before disk pre-allocation.
- **Channel-Bound Designated Relay & Relay Operator Opt-In**:
  - File transfers inherit the designated relay of the active conversation where the transfer was initiated.
  - **Relay Operator Opt-In**: Relays are **not** assumed to have unlimited bandwidth. Relays explicitly configure and advertise their supported capabilities:
    - `SupportsTransferPipes`: If false, the channel relay refuses out-of-band byte piping; transfers in that channel must be **Direct P2P Only**.
    - `SupportsSwarmTracker`: If false, the relay does not provide swarm rendezvous; peer discovery falls back to pure out-of-band PEX seeded by the offer initiator.
    - `MaxPipeBandwidthBytesPerSec` & `MaxConcurrentPipes`: Operators can throttle proxy bandwidth and cap simultaneous pipe sessions.
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
- **File Staging (`.percolator-part`) & Antivirus / OS Lock Protection**:
  - Files are written with a `.percolator-part` extension during download to prevent Windows Defender, search indexers, or users from locking or launching incomplete binaries.
  - Upon full verification of all chunks in a file, it is atomically renamed to its final target filename.

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
│   • IPluginActionRouter & IFileTransferActionHandler                                   │
│   • TimelineCardDto & TimelineActionDto                                                │
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
│   │     TokenBucketRateLimiter       │        │       DiskChunkAssembler          │    │
│   │ (Global / Per-Transfer Caps)     │        │ (Sparse Alloc, .percolator-part)  │    │
│   └──────────────────────────────────┘        └─────────────────┬─────────────────┘    │
└─────────────────────────────────────────────────────────────────┼──────────────────────┘
                                                                  │ Calls Outbound Ports
                                                                  ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                    Percolator.Infrastructure2 (Technical Realization)                  │
│   ┌──────────────────────────────────┐        ┌───────────────────────────────────┐    │
│   │      TcpMultiStreamPool (4–8)    │        │      DesktopSparseFileStore       │    │
│   │ (Direct TCP/QUIC Connection)     │        │ (FSCTL_SET_SPARSE, Atomic Rename) │    │
│   └─────────────────┬────────────────┘        └───────────────────────────────────┘    │
│                     │                                                                  │
│                     ▼ Fallback if Direct Fails (and Relay Opts-In)                     │
│   ┌──────────────────────────────────┐                                                 │
│   │    RelayTransferPipeClient       │                                                 │
│   │ (Blind Proxy via Channel Relay)  │                                                 │
│   └─────────────────┬────────────────┘                                                 │
└─────────────────────┼──────────────────────────────────────────────────────────────────┘
                      │ Out-of-Band High-Speed Data Streaming & Blind Swarm Rendezvous
                      ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                  Designated Channel Relay Server (Opt-In Features)                     │
│   • POST /transfer-pipes/{sessionId} (Optional: Blind bidirectional socket proxy)      │
│   • POST /swarms/{blindSwarmId}/announce (Optional: Lightweight in-memory tracker)     │
│   • Zero Disk Storage • Zero Decryption Knowledge • Ephemeral 60–90s TTL                │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Croc-Inspired Multi-Stream Engine & Relay Opt-In Model

### 3.1 Overcoming the Bandwidth-Delay Product (BDP)
Single TCP streams across high-latency internet routes suffer severe throughput degradation because TCP congestion windows cannot scale rapidly enough.
Following **croc**:
1. Every transfer session opens a pool of **4 to 8 parallel multiplexed streams** (`TransferStreamPool`).
2. Data is divided into uniform **1 MB chunks** (1,048,576 bytes).
3. Chunks are scheduled concurrently across all active streams in the pool.
4. Each stream uses independent congestion windows (`TCP_NODELAY = true`), saturating gigabit LANs and multi-hundred-megabit WANs.

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

### 3.2 Dual Transport Paths: Direct-First with Relay Pipe Fallback
When establishing the out-of-band transfer session:
1. **Direct P2P Connection (Primary Path)**:
   - Senders gather direct connection candidates (local LAN IPv4/IPv6, UPnP/PMP mapped ports, STUN reflexive addresses).
   - Candidates are sent to the recipient in the in-band `FileTransferOfferDto`.
   - The recipient attempts simultaneous TCP dial/connect on candidate addresses.
   - If a direct connection succeeds, all 4–8 streams connect peer-to-peer.
2. **Channel Relay Transfer Pipe (Fallback & Relayed Channels)**:
   - Used if direct connection fails (symmetric NAT, corporate firewall) or if the channel topology is explicitly designated as Relayed.
   - **Prerequisite**: The channel relay must have `SupportsTransferPipes = true`.
   - Both sender and receiver connect out-of-band to the **Channel's Designated Relay Transfer Pipe** (`POST /transfer-pipes/{sessionId}`).
   - The relay acts as a blind multiplexed socket pump, matching the two peers by `SessionId` and proxying bytes between the streams.
   - **Zero Relay Knowledge**: Every chunk transferred across the relay pipe is end-to-end encrypted with an ephemeral 256-bit symmetric key ($K_{\text{transfer}}$) generated by the sender and transmitted exclusively via the Double Ratchet in-band offer. The relay cannot decrypt or inspect file contents.

### 3.3 Relay Capabilities & Operator Opt-In Specification
Relays cannot be assumed to have the bandwidth or memory to act as high-speed file transfer proxies. Relay operators explicitly configure their level of participation:

```csharp
namespace Percolator.PluginSdk;

public sealed record RelayFileTransferCapabilities(
    bool SupportsTransferPipes,             // True if relay allows raw socket proxying
    bool SupportsSwarmTracker,              // True if relay allows blind swarm tracking
    long? MaxPipeBandwidthBytesPerSec,      // Bandwidth limit per active pipe
    int MaxConcurrentPipes);                // Max concurrent transfer pipes hosted
```

#### Client Behavior Based on Relay Capabilities:
- **Case 1: Relay Supports Both Pipes & Tracker**:
  - Full feature set: Senders fall back to relay pipes if direct P2P fails; swarms discover peers via the relay tracker.
- **Case 2: Relay Supports Tracker Only (Low-Bandwidth Mode)**:
  - Relay provides the in-memory `/swarms/{blindSwarmId}/announce` rendezvous table (using negligible bandwidth).
  - Out-of-band data streaming is strictly **Direct P2P Only**. If peers cannot establish direct P2P (e.g. symmetric NAT), the transfer fails with: *"Direct connection required: channel relay does not support data proxying"*.
- **Case 3: Relay Supports Neither (Pure Messaging Relay)**:
  - Both data transfer and swarm discovery must be serverless. Data transfer connects directly P2P; swarm peer discovery uses **Peer Exchange (PEX)** seeded by the initiator's initial offer endpoints.

---

## 4. Cryptographic Security & Onion Routing Privacy

### 4.1 Onion Routing Preservation vs. Direct P2P IP Leakage
A critical architectural and privacy distinction in Percolator:
- **In-Band Signaling**: Flows strictly through the existing onion-routed, multi-hop or relay-encapsulated Double Ratchet channel. IP addresses are completely protected.
- **Direct P2P Data Streams**: Connecting directly over TCP leaks IP addresses to the peer.
  - In 1:1 trusted conversations, direct P2P is highly desirable for maximum transfer speed.
  - In anonymous or high-threat threat models, direct IP exposure is unacceptable.
- **Policy Enforcement**:
  - Percolator provides a global and per-channel privacy setting: `FileTransferPrivacyMode`:
    - `DirectAllowed` (default for 1:1 direct channels): Attempts Direct P2P first, falls back to Relay Pipe if supported.
    - `RelayOnly` (enforced for onion-routed channels or privacy-hardened profiles): Bypasses direct IP candidate generation entirely; forces all 4–8 streams through the blind Relay Transfer Pipe. If the relay does not support transfer pipes, file transfer is disabled for that channel.

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
  - Before writing any chunk to disk, the recipient verifies:
    1. AES-256-GCM authentication tag verifies successfully.
    2. $\text{SHA-256}(0x00 \parallel \text{PlaintextChunk}_i) == \text{Manifest.ChunkHashes}[i]$.
  - Corrupt or tampered chunks are immediately discarded; the stream requests a re-transmission.

---

## 5. Directory Recursion, Manifest Distribution & Desktop Disk I/O

### 5.1 Directory Trees, Path Sanitization & No Permissions
`Apps.FileTransfer` supports sending single files, multiple files, or deeply nested folder structures.
- **Directory Traversal Defense**:
  - All relative paths are strictly validated before acceptance.
  - Paths containing `..`, absolute paths (e.g. `/etc/passwd` or `C:\Windows`), null bytes, or Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`) are rejected immediately.
  - All paths are normalized using forward slashes (`/`) in the manifest wire format.
- **Elimination of File Permissions / Attributes**:
  - File permissions are **not** transmitted. NTFS ACLs and POSIX octal bits do not translate across operating systems and present privilege-escalation risks.
  - Only file size, relative path, start offset, and last modified UTC timestamp are preserved.

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
- When Alice shares 10,000 files, the serialized `TransferManifest` may exceed 1 MB, far surpassing the $\le 64\text{ KB}$ in-band limit.
- Alice serializes `TransferManifest` to JSON/Protobuf and calls:
  ```csharp
  BlobReference manifestRef = await _blobStorage.UploadBlobAsync(
      channelId, manifestBytes, "application/x-percolator-manifest", ct);
  ```
- The in-band `FileTransferOfferDto` contains `ManifestBlobRef = manifestRef`.
- Bob receives the offer card, clicks `[Download to...]`, and downloads the manifest blob via `_blobStorage.DownloadBlobAsync(offer.ManifestBlobRef)`.
- This reuses Percolator's existing channel blob pipeline without adding custom manifest chunking protocols.

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
   - The storage assembler only writes the sub-span corresponding to the selected file to disk.
5. **Disk Pre-Allocation**:
   - The storage layer only pre-allocates files that were checked by the user, saving local disk space.

### 5.4 File Staging (`.percolator-part`) & Desktop Disk I/O
To prevent Windows Defender, Windows Search Indexer, or users from locking/opening partially downloaded files:
1. **Pre-Allocation with Temporary Suffix**:
   - Files are created on disk as `{relativePath}.percolator-part`.
   - On Windows: The storage port issues `FSCTL_SET_SPARSE` via `DeviceIoControl` and sets the end-of-file pointer (`SetFilePointerEx`). On Linux: `fallocate(FALLOC_FL_PUNCH_HOLE)`.
   - File handles are opened with `FileShare.ReadWrite | FileShare.Delete`.
2. **Direct Random-Access Writing**:
   - Verified chunks are written directly to their offsets via `RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory<byte>, fileOffset, ct)`.
3. **Atomic Finalization**:
   - When all chunks for a file have been written and verified, the storage layer flushes buffers and atomically renames `{relativePath}.percolator-part` to `{relativePath}` (`File.Move(..., overwrite: true)`).
   - Preserves `LastModifiedUtc` from the manifest.
4. **Resumption via Disk Bitfield Scanning**:
   - If interrupted, the scanner checks existing `.percolator-part` and completed files.
   - Verifies 1 MB chunks against `ChunkHashes` using SHA-256 and sets the `Bitfield` accordingly.

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
   - **Announce Endpoint**: `POST /swarms/{blindSwarmId}/announce`.
     - Payload: `[PeerIdentityId, DirectCandidates (IP:port), RelayPipeEndpoint, IsSeeder, BitfieldSummary]`.
     - Response: List of active peer candidate endpoints.
   - **Ephemeral In-Memory Storage**: The relay stores records in memory with a 90-second TTL. Zero disk persistence on the relay.
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

### 6.4 Configurable Bandwidth Rate Limiter
To prevent file transfers from saturating home connections or degrading concurrent chat/audio:
- `ITransferRateLimiter` enforces global and per-session throughput limits using a token-bucket algorithm:
  - Global caps: `MaxDownloadBytesPerSec`, `MaxUploadBytesPerSec`.
  - Per-session overrides: `DownloadSpeedCapBytesPerSec`, `UploadSpeedCapBytesPerSec`.
- Rate limiters throttle `Stream.ReadAsync` and `Stream.WriteAsync` loops across the `TransferStreamPool`.

---

## 7. Decoupled Chat Timeline & Transfer Manager UI

### 7.1 Separation of Responsibilities
1. **Chat Feed (Timeline Card)**:
   - Acts as an **ingress offer invitation** in the conversation stream.
   - Shows who sent what, high-level summary (file count, total size), and initial action buttons (`[Download to...]`, `[Ignore]`).
   - On app reload, pending/unaccepted offers load as `Offer Expired`. Completed transfers remain as historic notices with an `[Open Folder]` button.
2. **Dedicated Transfer Manager Dashboard (UI View)**:
   - A dedicated tab/window in the shell for monitoring all file transfers across all channels.
   - Provides live controls: Pause, Resume, Cancel, Change Speed Cap, Adjust Seed Ratio, View Stream Health, Selective File Toggles, and Open Folder.
   - Displays real-time progress bars, instantaneous speeds, swarm peer counts, and disk I/O metrics.

### 7.2 PluginSdk Timeline Types
```csharp
namespace Percolator.PluginSdk;

public sealed record TimelineActionDto(
    string ActionId,                  // e.g. "accept", "ignore", "cancel", "reveal"
    string Label,                     // e.g. "Download to...", "Ignore", "Cancel", "Open Folder"
    string Style,                     // "primary", "secondary", "danger"
    bool IsEnabled = true,
    string? ConfirmPrompt = null);    // Optional confirmation dialog text

public sealed record TimelineCardMetadata(
    string IconName,                  // e.g. "folder-zip", "file-binary"
    string StatusBadge,               // e.g. "Offered", "Downloading 45%", "Completed", "Expired"
    int? ProgressPercent = null,      // 0 to 100 for active transfers
    long? BytesTransferred = null,
    long? TotalBytes = null,
    double? TransferSpeedBytesPerSec = null);

public sealed record TimelineCardDto(
    Guid CardId,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    DateTimeOffset TimestampUtc,
    AppId SourceAppId,                // 0x03 for FileTransfer
    string Title,                     // "Alice is sharing: 'Release_v2'"
    string Summary,                   // "14 files • 4.2 GB"
    TimelineCardMetadata Metadata,
    IReadOnlyList<TimelineActionDto> Actions,
    byte[] CustomPayload);            // Serialized FileTransferOfferDto

public interface ITimelinePostSink
{
    ValueTask PublishCardAsync(TimelineCardDto card, CancellationToken ct = default);
    ValueTask UpdateCardStatusAsync(Guid cardId, string newSummary, TimelineCardMetadata newMetadata, IReadOnlyList<TimelineActionDto> newActions, CancellationToken ct = default);
    ValueTask RemoveCardAsync(Guid cardId, CancellationToken ct = default);
}

public interface IPluginActionHandler
{
    AppId TargetAppId { get; }
    ValueTask HandleActionAsync(Guid cardId, string actionId, byte[] customPayload, object? actionContext, CancellationToken ct = default);
}
```

---

## 8. Wire Protocols & Framing Specifications

### 8.1 In-Band Control Channel Envelope ($\le 64\text{ KB}$)
Carried inside the Double Ratchet envelope with `AppId = 0x03`:

```csharp
public enum FileTransferMessageType : byte
{
    Offer = 0x01,
    Accept = 0x02,
    Reject = 0x03,
    Cancel = 0x04
}

public sealed record EndpointCandidateDto(
    string Host,                      // IPv4, IPv6, or hostname
    int Port,                         // TCP/QUIC port
    string CandidateType);            // "lan", "stun", "upnp"

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
    IReadOnlyList<EndpointCandidateDto> ReceiverDirectCandidates,
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

## 9. Comprehensive Port Definitions (Infrastructure & Storage)

`Percolator.Apps.FileTransfer` relies on ports implemented in `Percolator.Infrastructure2`:

### 9.1 Network Stream Pool & Relay Tracker Ports
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

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
        IReadOnlyList<EndpointCandidateDto> directCandidates,
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

### 9.4 CQRS Transfer Manager Ports
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

## 10. Database Schema (SQLite Persistence in Infrastructure2)

```sql
CREATE TABLE IF NOT EXISTS ft_transfer_sessions (
    session_id TEXT PRIMARY KEY,
    channel_id TEXT NOT NULL,
    peer_identity_id TEXT NOT NULL,
    root_name TEXT NOT NULL,
    total_size_bytes INTEGER NOT NULL,
    total_file_count INTEGER NOT NULL,
    chunk_size_bytes INTEGER NOT NULL,
    total_chunks INTEGER NOT NULL,
    merkle_root_hash BLOB NOT NULL,
    manifest_blob_id TEXT NOT NULL,
    session_key BLOB NOT NULL,
    base_nonce BLOB NOT NULL,
    is_outgoing INTEGER NOT NULL,
    status TEXT NOT NULL, -- 'Offered', 'Downloading', 'Seeding', 'Paused', 'Completed', 'Cancelled'
    local_destination_path TEXT,
    selected_file_indices TEXT, -- JSON array of selected file indices
    uploaded_bytes INTEGER NOT NULL DEFAULT 0,
    target_seed_ratio REAL NOT NULL DEFAULT 1.0,
    created_at_utc TEXT NOT NULL,
    completed_at_utc TEXT
);

CREATE TABLE IF NOT EXISTS ft_manifest_files (
    session_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    file_size_bytes INTEGER NOT NULL,
    start_byte_offset INTEGER NOT NULL,
    last_modified_utc TEXT NOT NULL,
    PRIMARY KEY (session_id, relative_path),
    FOREIGN KEY (session_id) REFERENCES ft_transfer_sessions(session_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ft_session_bitfields (
    session_id TEXT PRIMARY KEY,
    bitfield BLOB NOT NULL,
    completed_chunk_count INTEGER NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY (session_id) REFERENCES ft_transfer_sessions(session_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ft_swarm_peers (
    session_id TEXT NOT NULL,
    peer_identity_id TEXT NOT NULL,
    direct_endpoints TEXT, -- JSON array of candidate endpoints
    is_seeder INTEGER NOT NULL DEFAULT 0,
    last_seen_utc TEXT NOT NULL,
    PRIMARY KEY (session_id, peer_identity_id),
    FOREIGN KEY (session_id) REFERENCES ft_transfer_sessions(session_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_ft_sessions_channel ON ft_transfer_sessions(channel_id);
CREATE INDEX IF NOT EXISTS idx_ft_sessions_status ON ft_transfer_sessions(status);
```

---

## 11. Security, Privacy & Threat Analysis

| Threat / Risk | Attack Vector | Mitigation in Architecture |
|---|---|---|
| **Directory Traversal** | Malicious manifest with `../../Windows/System32/evil.dll` | Strict sanitization: reject `..`, root paths, null bytes, and control chars before creating files. |
| **Disk Exhaustion** | 100 GB transfer offered to fill victim's disk | Recipient calls `HasSufficientFreeSpaceAsync` before accepting; pre-allocates files so disk space failures trigger upfront. |
| **Antivirus / OS File Locks** | Defender/Indexers locking incomplete files during write | `.percolator-part` staging files; atomic rename only upon full chunk verification. |
| **Tampered / Corrupted Chunks** | Man-in-the-middle or malicious peer injecting bad blocks | Dual verification: AES-256-GCM authentication tag check + SHA-256 leaf hash verification against Merkle tree before disk write. |
| **Relay Bandwidth Exhaustion** | Unmetered relay socket proxying overwhelming host | Relay operator opt-in flags (`SupportsTransferPipes = false` disables proxying; enforced direct P2P fallback). |
| **IP Address Leakage** | Direct P2P exposes IP address of peer on untrusted network | Global & channel `FileTransferPrivacyMode`: onion-routed channels force blind Relay Transfer Pipe fallback (when supported). |
| **Relay Snooping** | Relay inspecting or caching transferred files | End-to-end encryption ($K_{\text{transfer}}$ exchanged solely via in-band ratchet); relay is a blind memory-only socket pump with zero disk storage. |
| **In-Band Ratchet Bloat** | Chatty swarm announcements thrashing ratchet state | Ratchet envelope touched only once (initial offer); all swarm tracking (blind tracker / PEX) runs out-of-band. |
| **Network Bufferbloat / Choke** | Unthrottled multi-stream file transfers degrading audio/chat | Token-bucket rate limiting (`ITransferRateLimiter`) with configurable global and per-transfer upload/download caps. |

---

## 12. Phased Implementation Milestones

### Milestone 1: Plugin Registration, Manifest Models & Blob Storage Integration
- **Scope**: `Percolator.Apps.FileTransfer` domain models and manifest storage.
- **Tasks**:
  1. Implement `FileTransferPlugin` implementing `IAppPlugin` (`AppId = 0x03`).
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
  1. Add `ITimelinePostSink`, `TimelineCardDto`, `TimelineActionDto` to `Percolator.PluginSdk`.
  2. Implement timeline card emission upon sending/receiving offers, with `Offer Expired` reload behavior.
  3. Implement `TokenBucketRateLimiter` port (`ITransferRateLimiter`) for upload/download caps.
  4. Implement `IFileTransferQueryService` and `IFileTransferActionHandler` for the Transfer Manager UI.
  5. Unit tests: Timeline card lifecycle transitions, action dispatching, rate limiter throttling accuracy.

### Milestone 6: Infrastructure2 Realization & End-to-End Integration
- **Scope**: Socket pooling, Relay Transfer Pipe server/client, Relay Swarm Tracker, and SQLite persistence.
- **Tasks**:
  1. Implement `TcpMultiStreamPool` (parallel direct sockets with `TCP_NODELAY`).
  2. Implement `RelayTransferPipeClient` and Relay Server `POST /transfer-pipes/{sessionId}` endpoint with opt-in flags.
  3. Implement Relay Server `/swarms/{blindSwarmId}/announce` endpoint with opt-in flags.
  4. Implement `DesktopSparseFileStore` (`FSCTL_SET_SPARSE` + `.percolator-part` + atomic rename).
  5. Implement SQLite repositories for transfer sessions, manifests, bitfields, and swarm peers.
  6. End-to-end integration tests: Transfer 1 GB test file over 4 parallel streams (both direct and relayed).
