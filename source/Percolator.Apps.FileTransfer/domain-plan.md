# Percolator.Apps.FileTransfer: Domain Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: In Progress)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Pending)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Pending)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Pending)
- [ ] Implementation Gate: LOCKED (Requires Approval of Lenses 1-5)

---

## 1. Summary & Domain Boundaries

- **Target Bounded Context**: `Percolator.Apps.FileTransfer.Domain` (Core Domain Model for `AppId = 0x03`).
- **Target Environment**: **Desktop only** (multi-core, high-bandwidth unmetered networks, NVMe/SSD storage, large addressable memory, sparse files).
- **Core Domain Responsibilities**:
  - Encapsulate file transfer sessions, multi-peer swarming state, bitfield piece tracking, Merkle leaf validation, and filter policies.
  - Model multi-file directory manifests, chunk boundary spanning math, and selective download ranges.
- **Strict Onion Architecture Invariants**:
  - Depends **strictly** on `Percolator.Domain.Common` base contracts (`AggregateRoot<TId>`, `IEntity<TId>`, `IDomainEvent`, `DomainResult`, `DomainResult<T>`, `IDateTimeProvider`) and `Percolator.SourceGenerators`.
  - **Zero dependencies** on transport sockets (TCP/QUIC), storage I/O, SQLite, Protobuf wire encoders, serialization libraries, or `PluginSdk`.
  - **Zero references** to UI frameworks, view models, or presentation controls.
  - All domain concepts modeled as strongly typed Value Objects with **zero implicit/explicit cast operators**.

---

## 2. Domain Aggregate Roots & Entities

### 2.1 `TransferSession` Aggregate Root
Following `Percolator.Domain.Common.AggregateRoot<TId>`, `TransferSession` models the transfer lifecycle as an aggregate boundary emitting domain events upon state transitions. Swarm peer states, bitfield tracking, piece verification, and participant boundaries are fully encapsulated within the aggregate root:

```csharp
namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public enum TransferRole : byte
{
    Sender = 0x01,
    Recipient = 0x02
}

public enum TransferSessionState : byte
{
    Offered = 0x01,          // Outgoing transfer created by sender; awaiting recipient connections
    PendingConsent = 0x02,   // Inbound transfer received and staged locally; zero wire response emitted
    InFlight = 0x03,         // Active transfer session; parallel streams connected, exchanging chunks/bitfields
    Paused = 0x04,           // Active streams temporarily closed; bitfield and manifest preserved on disk
    Completed = 0x05,        // All selected files downloaded, verified against Merkle tree, and atomically renamed
    Seeding = 0x06,          // Serving completed chunks to active swarm peers based on SeedRatio
    Stalled = 0x07,          // Missing chunks across active swarm; streams paused waiting for seeders
    Cancelled = 0x08,        // Explicitly aborted by user
    Failed = 0x09            // Unrecoverable I/O, storage quota, or cryptographic verification failure
}

public sealed class TransferSession : AggregateRoot<TransferSessionId>
{
    public override TransferSessionId Id { get; }
    public TransferRole Role { get; }
    public TransferSessionState State { get; private set; }
    public PublicIdentityId InitiatorId { get; }
    public PublicIdentityId? RemotePeerId { get; }                   // 1:1 transfer participant
    public IReadOnlySet<PublicIdentityId> TargetRecipients { get; }   // 1:N push transfer targets
    public TransferManifest Manifest { get; }
    public Bitfield LocalBitfield { get; private set; }
    public RootDestinationPath? DestinationPath { get; private set; }
    public SelectedFileIndices? SelectedFiles { get; private set; }
    public SeedRatio TargetSeedRatio { get; private set; }
    public DirectWanPolicy WanPolicy { get; }
    public TransferKey SessionKey { get; }
    public BaseNonce BaseNonce { get; }
    public StreamAuthToken StreamAuthToken { get; }
    public RelayRendezvousDescriptor? RelayRendezvous { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private readonly Dictionary<PublicIdentityId, SwarmMember> _swarmMembers = [];
    public IReadOnlyCollection<SwarmMember> SwarmMembers => _swarmMembers.Values;

    // Invariant-enforced Factory Methods (V-46):
    public static TransferSession CreateOutgoing1to1(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        PublicIdentityId recipientId,
        TransferManifest manifest,
        DirectWanPolicy wanPolicy,
        TransferKey sessionKey,
        BaseNonce baseNonce,
        StreamAuthToken streamAuthToken,
        RelayRendezvousDescriptor? relayRendezvous,
        IDateTimeProvider clock);

    public static TransferSession CreateOutgoingMultiPeer(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        IReadOnlyList<PublicIdentityId> recipients,
        TransferManifest manifest,
        DirectWanPolicy wanPolicy,
        TransferKey sessionKey,
        BaseNonce baseNonce,
        StreamAuthToken streamAuthToken,
        RelayRendezvousDescriptor? relayRendezvous,
        IDateTimeProvider clock);

    public static TransferSession CreateIncomingStaged(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        TransferManifest manifest,
        DirectWanPolicy wanPolicy,
        TransferKey sessionKey,
        BaseNonce baseNonce,
        StreamAuthToken streamAuthToken,
        RelayRendezvousDescriptor? relayRendezvous,
        IDateTimeProvider clock);

    // Encapsulated Domain Mutation Methods:
    public DomainResult AcceptTransfer(RootDestinationPath destinationFolder, SelectedFileIndices? selectedFiles, IDateTimeProvider clock);
    public DomainResult StartStreaming();
    public DomainResult RegisterSwarmMember(PublicIdentityId peerId, Bitfield initialBitfield, IReadOnlyList<DirectEndpointCandidate> candidates, TransportRoutingPreference routingPreference, IDateTimeProvider clock);
    public DomainResult RecordPeerBitfield(PublicIdentityId peerId, Bitfield bitfield);
    public DomainResult RecordPeerHave(PublicIdentityId peerId, ChunkIndex chunkIndex);
    public DomainResult UpdatePeerCandidates(PublicIdentityId peerId, IReadOnlyList<DirectEndpointCandidate> candidates);
    public DomainResult SetPeerChoke(PublicIdentityId peerId, PeerChokeStatus status);
    public DomainResult SetPeerInterest(PublicIdentityId peerId, PeerInterestStatus status);
    
    // Encapsulates file completion boundary detection (V-45):
    public DomainResult VerifyAndRecordChunk(ChunkIndex chunkIndex, PlaintextChunkData data, IDateTimeProvider clock);
    
    public DomainResult CompleteTransfer(IDateTimeProvider clock);
    public DomainResult MarkStalled(IDateTimeProvider clock);
    public DomainResult MarkFailed(TransferTerminationReason reason, IDateTimeProvider clock);
    public DomainResult Pause();
    public DomainResult Resume();
    public DomainResult Cancel(TransferTerminationReason reason, IDateTimeProvider clock);
}
```

### 2.2 `SwarmMember` Entity
Encapsulates swarm participant piece availability, connection flow control, and fast-resume endpoint candidates:

```csharp
namespace Percolator.Apps.FileTransfer.Domain.SwarmCoordination;

public enum PeerChokeStatus : byte
{
    Choked = 0x01,
    Unchoked = 0x02
}

public enum PeerInterestStatus : byte
{
    NotInterested = 0x01,
    Interested = 0x02
}

public enum SwarmPeerRole : byte
{
    Leecher = 0x01,
    Seeder = 0x02
}

public sealed class SwarmMember : IEntity<PublicIdentityId>
{
    public PublicIdentityId Id => PeerId;
    public PublicIdentityId PeerId { get; }
    public Bitfield Bitfield { get; private set; }
    public PeerChokeStatus ChokeStatus { get; private set; }
    public PeerInterestStatus InterestStatus { get; private set; }
    public DateTimeOffset LastSeenUtc { get; private set; }
    public IReadOnlyList<DirectEndpointCandidate> EndpointCandidates { get; private set; }
    public TransportRoutingPreference RoutingPreference { get; private set; }
    public SwarmPeerRole Role => Bitfield.IsComplete ? SwarmPeerRole.Seeder : SwarmPeerRole.Leecher;

    internal SwarmMember(
        PublicIdentityId peerId,
        Bitfield bitfield,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        TransportRoutingPreference routingPreference,
        DateTimeOffset initialSeenUtc)
    {
        PeerId = peerId;
        Bitfield = bitfield;
        EndpointCandidates = candidates;
        RoutingPreference = routingPreference;
        ChokeStatus = PeerChokeStatus.Choked;
        InterestStatus = PeerInterestStatus.NotInterested;
        LastSeenUtc = initialSeenUtc;
    }

    internal void UpdateBitfield(Bitfield bitfield) => Bitfield = bitfield;
    internal void RecordHave(ChunkIndex chunkIndex) => Bitfield = Bitfield.WithChunk(chunkIndex);
    internal void UpdateCandidates(IReadOnlyList<DirectEndpointCandidate> candidates) => EndpointCandidates = candidates;
    internal void SetChoke(PeerChokeStatus status) => ChokeStatus = status;
    internal void SetInterest(PeerInterestStatus status) => InterestStatus = status;
    internal void Touch(DateTimeOffset timestamp) => LastSeenUtc = timestamp;
}
```

### 2.3 `TransferManifest` & `FileEntry` Entities
```csharp
namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed class FileEntry : IEntity<FileIndex>
{
    public FileIndex Id => Index;
    public FileIndex Index { get; }
    public SanitizedRelativePath RelativePath { get; }  // e.g. "Source/Assets/diffuse.png"
    public FileSizeBytes FileSize { get; }              // Exact byte length
    public BundleByteOffset StartOffset { get; }        // Global byte offset across bundle
    public BundleByteOffset EndOffset => StartOffset.Add(FileSize); // Encapsulated end boundary
    public FileLastModifiedUtc LastModifiedUtc { get; } // Preserves sanitized file timestamp

    public FileEntry(
        FileIndex index,
        SanitizedRelativePath relativePath,
        FileSizeBytes fileSize,
        BundleByteOffset startOffset,
        FileLastModifiedUtc lastModifiedUtc)
    {
        Index = index;
        RelativePath = relativePath;
        FileSize = fileSize;
        StartOffset = startOffset;
        LastModifiedUtc = lastModifiedUtc;
    }
}

public sealed class TransferManifest : IEntity<TransferSessionId>
{
    public TransferSessionId Id => ManifestId;
    public TransferSessionId ManifestId { get; }
    public RootBundleName RootName { get; }             // Display name (e.g. "Release_Build_v2")
    public TotalBundleBytes TotalSize { get; }          // Combined byte size of all files
    public FileCount TotalFiles { get; }                // Number of files in bundle
    public ChunkSizeBytes ChunkSize { get; }            // Bounded chunk size
    public ChunkCount TotalChunks { get; }              // Total chunks across bundle
    public MerkleTreeIndex MerkleTree { get; }          // Validated Merkle tree root and leaf hashes
    public IReadOnlyList<FileEntry> Files { get; }      // File structure entities

    private TransferManifest(
        TransferSessionId manifestId,
        RootBundleName rootName,
        TotalBundleBytes totalSize,
        FileCount totalFiles,
        ChunkSizeBytes chunkSize,
        ChunkCount totalChunks,
        MerkleTreeIndex merkleTree,
        IReadOnlyList<FileEntry> files)
    {
        ManifestId = manifestId;
        RootName = rootName;
        TotalSize = totalSize;
        TotalFiles = totalFiles;
        ChunkSize = chunkSize;
        TotalChunks = totalChunks;
        MerkleTree = merkleTree;
        Files = files;
    }

    public static TransferManifest Create(
        TransferSessionId manifestId,
        RootBundleName rootName,
        ChunkSizeBytes chunkSize,
        MerkleTreeIndex merkleTree,
        IReadOnlyList<FileEntry> files)
    {
        long totalBytes = 0;
        foreach (FileEntry file in files)
        {
            totalBytes += file.FileSize.Bytes;
        }

        TotalBundleBytes bundleBytes = TotalBundleBytes.FromBytes(totalBytes);
        int chunkCount = (int)Math.Max(1, (totalBytes + chunkSize.Bytes - 1) / chunkSize.Bytes);

        return new TransferManifest(
            manifestId,
            rootName,
            bundleBytes,
            FileCount.FromInt(files.Count),
            chunkSize,
            ChunkCount.FromInt(chunkCount),
            merkleTree,
            files);
    }
}
```

### 2.4 `FileTransferFilterPolicy` Aggregate Root
```csharp
namespace Percolator.Apps.FileTransfer.Domain.IngressControl;

public sealed class FileTransferFilterPolicy : AggregateRoot<PublicIdentityId>
{
    public override PublicIdentityId Id => OwnerId;
    public PublicIdentityId OwnerId { get; }

    private readonly HashSet<PublicIdentityId> _blockedPeers = [];
    private readonly HashSet<RelayPipeEndpointUri> _blockedRelays = [];

    public IReadOnlySet<PublicIdentityId> BlockedPeers => _blockedPeers;
    public IReadOnlySet<RelayPipeEndpointUri> BlockedRelays => _blockedRelays;

    public FileTransferFilterPolicy(PublicIdentityId ownerId)
    {
        OwnerId = ownerId;
    }

    public void BlockPeer(PublicIdentityId peerId) => _blockedPeers.Add(peerId);
    public void UnblockPeer(PublicIdentityId peerId) => _blockedPeers.Remove(peerId);
    public void BlockRelay(RelayPipeEndpointUri relayUri) => _blockedRelays.Add(relayUri);
    public void UnblockRelay(RelayPipeEndpointUri relayUri) => _blockedRelays.Remove(relayUri);

    public IngressDecision EvaluateEdgeIngress(PublicIdentityId senderId)
    {
        return _blockedPeers.Contains(senderId) ? IngressDecision.DiscardSilently : IngressDecision.Accept;
    }

    public bool IsRelayBlocked(RelayPipeEndpointUri relayUri)
    {
        return _blockedRelays.Contains(relayUri);
    }
}
```

---

## 3. Strongly Typed Value Objects Catalog

All domain concepts in `Apps.FileTransfer.Domain` are modeled as strongly typed Value Objects with **zero implicit/explicit cast operators**, explicit factory methods, and named property accessors.

| Value Object | Source Generator / Structure | Encapsulated Concept & Invariants | Zero Cast Guarantee |
| :--- | :--- | :--- | :--- |
| **`TransferSessionId`** | `[GuidId(GuidIdKind.CryptographicRandom)]` | 16-byte UUID identifying a transfer session | Explicit `FromGuid(Guid)`, `NewUnique()`, `.Value`. Zero cast operators. |
| **`MerkleRoot`** | `[ByteArray(length: 32)]` | 32-byte cryptographic SHA-256 Merkle root | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`ChunkHash`** | `[ByteArray(length: 32)]` | 32-byte SHA-256 leaf hash | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`TransferKey`** | `[ByteArray(length: 32)]` | 32-byte ephemeral AES-256 session key (`ISensitiveSecret`) | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Implements `Zeroize()`. Zero cast operators. |
| **`SwarmRendezvousId`** | `[ByteArray(length: 32)]` | 32-byte HMAC-SHA256 rendezvous topic derived from $K_{\text{transfer}}$ | Explicit `Derive(TransferKey)`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`StreamAuthToken`** | `[ByteArray(length: 32)]` | 32-byte out-of-band stream authentication token | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`BaseNonce`** | `[ByteArray(length: 12)]` | 12-byte base nonce for AES-GCM deterministic derivation | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`RelayPipeToken`** | `[ByteArray(length: 32)]` | 32-byte authentication token for relay pipe rendezvous | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`RelayRendezvousDescriptor`** | `sealed record` | Encapsulated relay endpoint URI and 32-byte auth token | Explicit `Create(RelayPipeEndpointUri, RelayPipeToken)`, `.EndpointUri`, `.Token`. Zero cast operators. |
| **`TransferBundleSummary`** | `sealed record` | Cohesive bundle metadata (title, size, files, chunks, Merkle root) | Explicit `Create(...)`, `.RootName`, `.TotalSize`, `.TotalFiles`, `.ChunkSizeBytes`, `.TotalChunks`, `.MerkleRoot`. Zero cast operators. |
| **`InlineManifestData`** | `[ByteArray(minLength: 1, maxLength: 65536)]` | Serialized inline manifest payload ($\le 64\text{ KB}$) | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`RootBundleName`** | `sealed record` | Sanitized display title for transfer root ($1 \le \text{Length} \le 255$) | Explicit `Parse(string)`, `.Value`. Zero cast operators. |
| **`RelayPipeEndpointUri`** | `sealed record` | URI for relay transfer pipe rendezvous (`percolator-pipe://` / `wss://`) | Explicit `Parse(string)`, `FromUri(Uri)`, `.Value`. Zero cast operators. |
| **`TransferTerminationReason`**| `sealed record` | Reason for transfer rejection, cancellation, or failure | Explicit `UserDeclined`, `SenderCancelled`, `StorageExhausted`, `VerificationFailed`, `Timeout`, `FromText(string)`, `.Value`. Zero cast operators. |
| **`SanitizedRelativePath`** | `sealed record` | Relative file path verified against directory traversal | Explicit `Parse(string)`, `.Value`. Zero cast operators. |
| **`RootDestinationPath`** | `sealed record` | Validated absolute desktop target folder path | Explicit `Parse(string)`, `Resolve(SanitizedRelativePath)`, `.Value`. Zero cast operators. |
| **`ResolvedDestinationFilePath`**| `sealed record` | Validated combined absolute destination file path | Explicit `Resolve(RootDestinationPath, SanitizedRelativePath)`, `.AbsolutePath`. Zero cast operators. |
| **`FileLastModifiedUtc`** | `readonly record struct` | Validated file modification timestamp ($0 \le \text{epoch} \le \text{future buffer}$) | Explicit `FromDateTimeOffset(DateTimeOffset)`, `FromUnixSeconds(long)`, `.UtcDateTime`, `.UnixSeconds`. Zero cast operators. |
| **`EndpointHost`** | `sealed record` | Validated IP address or DNS hostname | Explicit `Parse(string)`, `.Value`. Zero cast operators. |
| **`PortNumber`** | `readonly record struct` | Network port: $1 \le \text{Port} \le 65535$ | Explicit `FromInt(int)`, `.Value`. Zero cast operators. |
| **`FileIndex`** | `readonly record struct` | Zero-indexed file sequence in manifest: $0 \le \text{Value} < \text{TotalFiles}$ | Explicit `FromInt(int)`, `.Value`. Zero cast operators. |
| **`ChunkIndex`** | `readonly record struct` | Zero-indexed 32-bit chunk sequence: $0 \le \text{Index} < \text{TotalChunks}$ | Explicit `FromInt(int)`, `WriteBigEndian(Span<byte>)`, `.Value`, `Next()`. Zero cast operators. |
| **`ChunkSizeBytes`** | `readonly record struct` | Bounded chunk size: $64\text{ KB} \le \text{Bytes} \le 4\text{ MB}$, default $1\text{ MB}$ | Explicit `Default`, `FromBytes(int)`, `.Bytes`. Zero cast operators. |
| **`TotalBundleBytes`** | `readonly record struct` | Combined byte size of bundle: $\text{Bytes} \ge 0$ | Explicit `FromBytes(long)`, `Zero`, `.Bytes`. Zero cast operators. |
| **`FileSizeBytes`** | `readonly record struct` | Byte size of single file: $\text{Bytes} \ge 0$ | Explicit `FromBytes(long)`, `RemainingSlice(FileByteOffset, ChunkByteLength)`, `.Bytes`. Zero cast operators. |
| **`BundleByteOffset`** | `readonly record struct` | Offset across global bundle byte layout: $\text{Offset} \ge 0$ | Explicit `FromOffset(long)`, `FromChunk(ChunkIndex, ChunkSizeBytes)`, `Add(FileSizeBytes)`, `Add(TransferByteLength)`, `Subtract(BundleByteOffset)`, `SubtractToFileOffset(BundleByteOffset)`, `Zero`, `.Offset`. Zero cast operators. |
| **`FileByteOffset`** | `readonly record struct` | Offset within specific file on disk: $\text{Offset} \ge 0$ | Explicit `FromOffset(long)`, `Add(TransferByteLength)`, `Zero`, `.Offset`. Zero cast operators. |
| **`FileCount`** | `readonly record struct` | Total file count in manifest: $\text{Count} \ge 0$ | Explicit `FromInt(int)`, `Zero`, `.Count`. Zero cast operators. |
| **`ChunkCount`** | `readonly record struct` | Total chunks in transfer: $\text{Count} \ge 1$ | Explicit `FromInt(int)`, `.Count`. Zero cast operators. |
| **`StreamCount`** | `readonly record struct` | Count of active parallel streams: $0 \le \text{Count} \le 8$ | Explicit `FromInt(int)`, `Default` (4), `.Count`. Zero cast operators. |
| **`PeerCount`** | `readonly record struct` | Count of connected swarm peers: $\text{Count} \ge 0$ | Explicit `FromInt(int)`, `Zero`, `.Count`. Zero cast operators. |
| **`TransferByteLength`** | `readonly record struct` | Byte length slice for transfer/throttle operations: $\text{Bytes} \ge 0$ | Explicit `FromBytes(int)`, `.Bytes`. Zero cast operators. |
| **`TransferBandwidth`** | `readonly record struct` | Bandwidth speed cap: $\text{BytesPerSec} \ge 0$ | Explicit `FromBytesPerSecond(long)`, `.BytesPerSecond`. Zero cast operators. |
| **`BandwidthLimit`** | `readonly record struct` | Bandwidth speed cap: Unlimited or bounded speed | Explicit `Unlimited`, `Capped(TransferBandwidth)`, `.IsUnlimited`, `.Value`. Zero cast operators. |
| **`PipeConcurrencyLimit`** | `readonly record struct` | Maximum concurrent relay streams: $1 \le \text{Count} \le 128$ | Explicit `FromInt(int)`, `Default` (8), `.Count`. Zero cast operators. |
| **`RelayTransferCapabilityFlags`** | `[Flags] enum : byte` | Advertised relay transfer feature flags | Explicit flags: `None` (0x00), `TransferPipes` (0x01), `SwarmTracker` (0x02), `StunReflection` (0x04). |
| **`RelayFileTransferCapabilities`** | `sealed record` | Relay feature capabilities and resource boundaries | Explicit `Create(...)`, `Capabilities`, `MaxPipeBandwidth`, `MaxConcurrentPipes`. Zero cast operators. |
| **`SeedRatio`** | `readonly record struct` | Upload/Download ratio cap: $\ge 0.0$ or Unlimited | Explicit `Zero`, `Unlimited`, `FromMultiplier(double)`, `.Multiplier`, `.IsUnlimited`. Rejects NaN, Infinity, negative values. Zero cast operators. |
| **`Bitfield`** | `sealed record` | Chunk possession bitmask: byte length $\lceil \text{TotalChunks} / 8 \rceil$. **Immutable** | Explicit `WithChunk(ChunkIndex)` returning new instance; `Empty(ChunkCount)`, `HasChunk(ChunkIndex)`, `ToByteArray()`, `.Length`, `.IsComplete`. Zero cast operators. |
| **`RoutePriority`** | `enum : byte` | Candidate route preference hierarchy | Explicit enum values: `DirectLan` (0x01), `DirectWanOptIn` (0x02), `PrimaryRelay` (0x03), `SecondaryRelay` (0x04). |
| **`DirectWanPolicy`** | `enum : byte` | Direct WAN candidate exposure policy | Explicit enum values: `DisabledByDefault` (0x01), `ExplicitlyAllowed` (0x02). |
| **`TransportRoutingPreference`** | `enum : byte` | In-band connection route preference | Explicit enum values: `DirectPreferred` (0x01), `RelayOnly` (0x02). |
| **`ChunkByteLength`** | `readonly record struct` | Actual bounded byte length of an individual chunk: $1 \le \text{Bytes} \le 4\text{ MB}$ | Explicit `FromBytes(int)`, `Subtract(TransferByteLength)`, `.Bytes`. Zero cast operators. |
| **`EncryptedChunkData`** | `sealed record` | Ciphertext payload of chunk with appended 16-byte GCM auth tag | Explicit `FromBytes(byte[])`, `FromSpan(ReadOnlySpan<byte>)`, `.Span`, `.Length`. Zero cast operators. |
| **`PlaintextChunkData`** | `sealed record` | Decrypted chunk bytes for disk I/O ($1 \le \text{Bytes} \le 4\text{ MB}$) | Explicit `FromBytes(byte[])`, `FromMemory(ReadOnlyMemory<byte>)`, `.Memory`, `.Span`, `.Length`. Zero cast operators. |
| **`DiskSpacePreflightResult`** | `sealed record` | Domain result of pre-flight disk capacity check | Explicit `Sufficient(required, available)`, `Insufficient(required, available)`, `.HasSpace`. Zero cast operators. |
| **`ManifestBlobDescriptor`** | `sealed record` | Clean out-of-band manifest storage reference | Explicit `Create(...)`, `BlobId`, `CiphertextSha256`, `PlaintextSizeBytes`, `RelayEndpointUri`. Zero cast operators. |
| **`ManifestDistribution`** | `sealed record` | Discriminated union of inline vs. out-of-band manifest | Explicit `FromInline(...)`, `FromBlob(...)`, `Kind`, `InlineData`, `BlobDescriptor`. Zero cast operators. |
| **`MerkleTreeIndex`** | `sealed record` | Encapsulated Merkle tree root and leaf verification index | Explicit `Create(root, leaves)`, `VerifyLeaf(index, span)`, `GetLeaf(index)`. Zero cast operators. |
| **`SelectedFileIndices`** | `sealed record` | Checked file indices: non-null immutable set of `FileIndex` | Explicit `All(FileCount)`, `FromIndices(...)`, `Contains(FileIndex)`. Zero cast operators. |
| **`StreamIndex`** | `readonly record struct` | Zero-indexed out-of-band stream: $0 \le \text{Index} \le 7$ | Explicit `FromInt(int)`, `.Value`. Zero cast operators. |
| **`DirectCandidateKind`** | `enum : byte` | Direct socket candidate network topology | Explicit enum values: `DirectLan` (0x01), `DirectWanUpnp` (0x02), `DirectWanStun` (0x03). |
| **`DirectEndpointCandidate`** | `sealed record` | Direct IP socket connection candidate | Explicit `Create(Host, Port, Kind)`, `.Host`, `.Port`, `.Kind`. Zero cast operators. |

---

## 4. Formal Domain Events Hierarchy

Every domain transition in `TransferSession` emits an immutable domain event implementing `IDomainEvent` (`Percolator.Domain.Common`):

```csharp
namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record TransferOfferedDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId InitiatorId,
    TransferBundleSummary Summary,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferAcceptedLocallyDomainEvent(
    TransferSessionId SessionId,
    RootDestinationPath DestinationPath,
    SelectedFileIndices SelectedFiles,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PeerAcceptedTransferDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId RecipientId,
    IReadOnlyList<DirectEndpointCandidate> Candidates,
    TransportRoutingPreference RoutingPreference,
    Bitfield ResumptionBitfield,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferStreamingStartedDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record SwarmMemberJoinedDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId PeerId,
    Bitfield InitialBitfield,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ChunkVerifiedDomainEvent(
    TransferSessionId SessionId,
    ChunkIndex ChunkIndex,
    ChunkHash LeafHash,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record FileCompletedDomainEvent(
    TransferSessionId SessionId,
    FileIndex FileIndex,
    SanitizedRelativePath RelativePath,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferCompletedDomainEvent(
    TransferSessionId SessionId,
    TotalBundleBytes TotalBytes,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferPausedDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferResumedDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferStalledDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferFailedDomainEvent(
    TransferSessionId SessionId,
    TransferTerminationReason Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferCancelledDomainEvent(
    TransferSessionId SessionId,
    TransferTerminationReason Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
```

---

## 5. Pure Domain Algorithms & Boundary Math

### 5.1 Multi-File Boundary Spanning Math (`DiskChunkAssembler`)
Encapsulates calculating byte offsets across multi-gigabyte continuous byte streams into target file boundaries:

```csharp
BundleByteOffset chunkStart = BundleByteOffset.FromChunk(chunkIndex, chunkSize);
ChunkByteLength remainingChunkBytes = actualChunkSize;
BundleByteOffset currentOffset = chunkStart;
int bufferOffset = 0;

while (remainingChunkBytes.Bytes > 0)
{
    FileEntry file = FindFileForOffset(manifest.Files, currentOffset);
    FileByteOffset offsetInFile = currentOffset.SubtractToFileOffset(file.StartOffset);
    TransferByteLength bytesForThisFile = file.FileSize.RemainingSlice(offsetInFile, remainingChunkBytes);

    if (selectedFiles.Contains(file.Index))
    {
        ReadOnlyMemory<byte> slice = chunkBuffer.Slice(bufferOffset, bytesForThisFile.Bytes);
        PlaintextChunkData plainSlice = PlaintextChunkData.FromMemory(slice);
        ResolvedDestinationFilePath targetFile = destinationRoot.Resolve(file.RelativePath);
        await storage.WriteChunkAsync(targetFile, offsetInFile, plainSlice, ct);
    }

    bufferOffset += bytesForThisFile.Bytes;
    currentOffset = currentOffset.Add(bytesForThisFile);
    remainingChunkBytes = remainingChunkBytes.Subtract(bytesForThisFile);
}
```

### 5.2 Merkle Tree Index Verification
- Leaf hashing: $H_{\text{leaf}}(i) = \text{SHA-256}(0x00 \parallel \text{PlaintextChunk}_i)$
- Interior node hashing: $H_{\text{interior}}(\text{Left}, \text{Right}) = \text{SHA-256}(0x01 \parallel \text{Left} \parallel \text{Right})$
- Encapsulated inside `MerkleTreeIndex.VerifyLeaf(ChunkIndex index, ReadOnlySpan<byte> plaintextChunk)`.

---

## 6. High-Value Domain Unit Testing Matrix

| Test Category | Target Invariant Under Test | Observable Verification | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **Factory Invariant** | Instantiating 1:1 sender without recipient | Throws `DomainValidationException` | Asserts aggregate cannot be constructed in invalid state. |
| **Factory Invariant** | Instantiating 1:N sender with empty recipient list | Throws `DomainValidationException` | Prevents headless push transfers. |
| **Bitfield Immutability** | Calling `Bitfield.WithChunk(index)` | Returns new instance; original unchanged | Asserts immutability of piece possession bitfields. |
| **Boundary Spanning** | Chunk spanning exactly across 2 files | Math produces correct offset slices for both files | Zero mock I/O; pure domain offset calculation verification. |
| **File Completion** | Verifying last chunk of a multi-chunk file | Aggregate emits `FileCompletedDomainEvent` | Asserts aggregate directly evaluates file boundaries. |
| **Traversal Rejection** | Manifest containing `../../etc/passwd` | `SanitizedRelativePath.Parse` throws exception | Asserts immediate validation rejection at parser level. |
| **SeedRatio Validation** | Passing negative multiplier or NaN to `SeedRatio` | Throws `DomainValidationException` | Enforces bounded invariant. |

---

## 7. Phased Domain Implementation Milestones

### Milestone 1: Domain Primitives, Value Objects & Roslyn Integration
1. Configure `Percolator.Apps.FileTransfer.csproj` with Roslyn incremental source generator analyzer reference to `Percolator.SourceGenerators`.
2. Generate and implement all Value Objects via source generators (`TransferSessionId`, `SwarmRendezvousId`, `MerkleRoot`, `ChunkHash`, `TransferKey`, `BaseNonce`, `StreamAuthToken`, `RelayPipeToken`, `RelayRendezvousDescriptor`, `TransferBundleSummary`, `InlineManifestData`, `EndpointHost`, `PortNumber`, `DirectEndpointCandidate`, `ChunkByteLength`, `EncryptedChunkData`, `PlaintextChunkData`, `TotalBundleBytes`, `FileSizeBytes`, `BundleByteOffset`, `FileByteOffset`, `ChunkIndex`, `FileIndex`, `StreamIndex`, `StreamCount`, `PeerCount`, `TransferByteLength`, `TransferBandwidth`, `BandwidthLimit`, `SeedRatio`, `Bitfield`, `SelectedFileIndices`, `RootDestinationPath`, `ResolvedDestinationFilePath`, `DiskSpacePreflightResult`, `TransferTerminationReason`).
3. Implement Domain Enums: `TransferRole`, `TransferSessionState`, `RoutePriority`, `DirectWanPolicy`, `TransportRoutingPreference`, `SwarmPeerRole`, `PeerChokeStatus`, `PeerInterestStatus`, `DirectCandidateKind`, `RelayTransferCapabilityFlags`.
4. Strictly verify **zero implicit/explicit cast operators** across all Value Objects.

### Milestone 2: Domain Entities & Aggregate Roots
1. Implement `TransferManifest : IEntity<TransferSessionId>` and `FileEntry : IEntity<FileIndex>`.
2. Implement `SwarmMember : IEntity<PublicIdentityId>` with `EndpointCandidates`, `Bitfield`, and flow control methods.
3. Implement `FileTransferFilterPolicy : AggregateRoot<PublicIdentityId>` with `EvaluateEdgeIngress` and `IsRelayBlocked`.
4. Implement `TransferSession : AggregateRoot<TransferSessionId>` with invariant factories (`CreateOutgoing1to1`, `CreateOutgoingMultiPeer`, `CreateIncomingStaged`), encapsulated chunk verification, and file completion detection.
5. Implement all 13 `IDomainEvent` records.
6. Implement `MerkleTreeIndex` and `MerkleTreeBuilder`.
7. Implement pure domain unit tests covering the Domain Unit Testing Matrix.
