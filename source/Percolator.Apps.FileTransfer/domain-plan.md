# Percolator.Apps.FileTransfer: Domain Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [x] Implementation Gate: UNLOCKED (All Lenses Approved)

---

## 1. Summary & Domain Boundaries

> ### Core Domain Boundary Definition
> **The `FileTransfer.Domain` bounded context encapsulates strictly the mathematical, structural, and lifecycle invariants of a content-addressed multi-file transfer session.** Specifically, it is the sole authority on: (1) manifest structural integrity and contiguous multi-file bundle offset arithmetic; (2) piece possession state via immutable bitfields; (3) Merkle tree leaf and root cryptographic verification; and (4) session state machine transitions gated by participant topology and user consent. It is strictly agnostic to physical storage I/O, operating system filesystem or path encoding quirks, network framing, socket pools, peer reputation scoring, and transport coordination.

- **Target Bounded Context**: `Percolator.Apps.FileTransfer.Domain` (Standalone Core Domain Library for `AppId = 0x03`).
- **Target Project**: `Percolator.Apps.FileTransfer.Domain.csproj` (Completely standalone domain library decoupled from `Percolator.Domain`).
- **Target Environment**: **Desktop only** (multi-core, high-bandwidth unmetered networks, NVMe/SSD storage, large addressable memory, sparse files).
- **Core Domain Responsibilities**:
  - Encapsulate file transfer sessions, multi-peer swarming state, bitfield piece tracking, Merkle leaf validation, bidirectional choking flow control, and seeding ratios.
  - Model multi-file directory manifests, chunk boundary spanning math, zero-byte file bundle layout invariants, and selective download ranges.
- **Strict Onion Architecture Invariants**:
  - Depends **strictly** on `Percolator.Domain.Kernel` (`AggregateRoot<TId>`, `IEntity<TId>`, `IDomainEvent`, `DomainResult`, `DomainError`, `IDateTimeProvider`, `ISensitiveSecret`, `PublicIdentityId`) and compile-time `Percolator.SourceGenerators`.
  - **Zero dependencies** on `Percolator.Domain` (zero awareness of Double Ratchet, X3DH, Relays, or Mailbox Queues).
  - **Zero dependencies** on transport sockets (TCP/QUIC), socket pools, direct IP/port candidates, NAT traversal, relay pipes, storage I/O, SQLite, Protobuf wire encoders, serialization libraries, or `PluginSdk`.
  - **Zero references** to UI frameworks, view models, or presentation controls.
  - All domain concepts modeled as strongly typed Value Objects with **zero implicit/explicit cast operators**.

---

## 2. Domain Aggregate Roots & Entities

### 2.1 `TransferSession` Aggregate Root
Following `Percolator.Domain.Common.AggregateRoot<TId>`, `TransferSession` models the transfer lifecycle as an aggregate boundary emitting domain events upon state transitions. Swarm peer piece possession, verification, upload volume tracking, and participant topologies are fully encapsulated within the aggregate root:

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
    InFlight = 0x03,         // Active transfer session; parallel streams exchanging chunks/bitfields
    Paused = 0x04,           // Active transfer temporarily paused; bitfield and manifest preserved
    Completed = 0x05,        // All selected files downloaded and verified against Merkle tree
    Seeding = 0x06,          // Serving completed chunks to active swarm peers based on SeedRatio
    Stalled = 0x07,          // Missing chunks across active swarm; waiting for seeders
    Cancelled = 0x08,        // Explicitly aborted by user or expired
    Failed = 0x09            // Unrecoverable cryptographic verification or domain invariant failure
}

public sealed class TransferSession : AggregateRoot<TransferSessionId>
{
    public override TransferSessionId Id { get; }
    public TransferRole Role { get; }
    public TransferSessionState State { get; private set; }
    public TransferTopology Topology { get; }                        // Cohesive participant topology
    public PublicIdentityId InitiatorId => Topology.InitiatorId;
    public TransferManifest Manifest { get; }
    public Bitfield LocalBitfield { get; private set; }
    public SelectedFileIndices? SelectedFiles { get; private set; }
    public SeedRatio TargetSeedRatio { get; private set; }           // Enforced seeding ratio
    public TotalBundleBytes UploadedBytes { get; private set; }      // Tracked uploaded volume
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private readonly Dictionary<PublicIdentityId, SwarmMember> _swarmMembers = [];
    public IReadOnlyCollection<SwarmMember> SwarmMembers => _swarmMembers.Values;

    // Evaluates selective download completion against required chunk mask:
    public bool IsDownloadComplete =>
        SelectedFiles != null && LocalBitfield.HasAllChunks(Manifest.GetRequiredChunks(SelectedFiles));

    public SeedRatio CurrentSeedRatio =>
        Manifest.TotalSize.Bytes > 0
            ? SeedRatio.FromMultiplier((double)UploadedBytes.Bytes / Manifest.TotalSize.Bytes)
            : SeedRatio.Zero;

    public bool HasReachedTargetSeedRatio =>
        !TargetSeedRatio.IsUnlimited && CurrentSeedRatio.Multiplier >= TargetSeedRatio.Multiplier;

    // Invariant-enforced Factory Methods:
    public static TransferSession CreateOutgoing1to1(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        PublicIdentityId recipientId,
        TransferManifest manifest,
        SeedRatio targetSeedRatio,
        IDateTimeProvider clock);

    public static TransferSession CreateOutgoingMultiPeer(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        IReadOnlyList<PublicIdentityId> recipients,
        TransferManifest manifest,
        SeedRatio targetSeedRatio,
        IDateTimeProvider clock);

    public static TransferSession CreateIncomingStaged(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        TransferManifest manifest,
        SeedRatio targetSeedRatio,
        IDateTimeProvider clock);

    // Encapsulated Domain Mutation Methods:
    // Transitions PendingConsent -> InFlight directly upon user consent
    public DomainResult AcceptTransfer(SelectedFileIndices selectedFiles, IDateTimeProvider clock);
    
    // Transitions Offered -> InFlight on first peer acceptance
    public DomainResult RecordPeerAcceptance(
        PublicIdentityId recipientId,
        Bitfield resumptionBitfield,
        IDateTimeProvider clock);

    // Swarm admission invariant: peer must be an authorized participant in Topology
    public DomainResult RegisterSwarmMember(
        PublicIdentityId peerId,
        Bitfield initialBitfield,
        IDateTimeProvider clock);

    // Transitions PendingConsent -> Cancelled with reason OfferExpired
    public DomainResult ExpireOffer(IDateTimeProvider clock);
    
    // Piece possession mutations:
    public DomainResult RecordPeerBitfield(PublicIdentityId peerId, Bitfield bitfield); // Enforces bitfield length
    public DomainResult RecordPeerHave(PublicIdentityId peerId, ChunkIndex chunkIndex);

    // Bidirectional flow control mutations:
    public DomainResult SetWeChokingPeer(PublicIdentityId peerId, PeerChokeStatus status);
    public DomainResult RecordPeerChokingUs(PublicIdentityId peerId, PeerChokeStatus status);
    public DomainResult SetWeInterestedInPeer(PublicIdentityId peerId, PeerInterestStatus status);
    public DomainResult RecordPeerInterestedInUs(PublicIdentityId peerId, PeerInterestStatus status);

    // Seeding ratio and upload tracking:
    public DomainResult RecordUploadedBytes(TransferByteLength bytes, IDateTimeProvider clock);
    public DomainResult SetTargetSeedRatio(SeedRatio seedRatio);

    // Encapsulates exact chunk verification, idempotency, and file completion boundary detection:
    public DomainResult VerifyAndRecordChunk(ChunkIndex chunkIndex, PlaintextChunkData data, IDateTimeProvider clock);
    
    // Lifecycle transitions:
    public DomainResult CompleteTransfer(IDateTimeProvider clock);
    public DomainResult MarkStalled(IDateTimeProvider clock);
    public DomainResult MarkFailed(TransferTerminationReason reason, IDateTimeProvider clock);
    public DomainResult Pause();
    public DomainResult Resume();
    public DomainResult Cancel(TransferTerminationReason reason, IDateTimeProvider clock);
}
```

### 2.1.1 Formal Lifecycle State Transition Matrix

The table below codifies the exact state machine invariants enforced by `TransferSession`. Any mutation attempted from an invalid state returns `DomainResult.Failure` or throws `DomainValidationException`:

| Current State | Triggering Domain Method | Next State | Guard Conditions & Observable Invariants |
| :--- | :--- | :--- | :--- |
| *(None)* | `CreateOutgoing1to1` / `MultiPeer` | `Offered` | Sender initialization. `LocalBitfield` is 100% full. |
| *(None)* | `CreateIncomingStaged` | `PendingConsent` | Recipient staging. `LocalBitfield` is empty. Zero network ACK emitted. |
| `Offered` | `RecordPeerAcceptance` | `InFlight` | Gated on `Topology.ContainsParticipant(recipientId)`. Registers peer bitfield. Emits `PeerAcceptedTransferDomainEvent`. |
| `Offered` | `Cancel(reason, clock)` | `Cancelled` | Emits `TransferCancelledDomainEvent`. Terminal state. |
| `Offered` | `MarkFailed(reason, clock)` | `Failed` | Emits `TransferFailedDomainEvent`. Terminal state. |
| `PendingConsent` | `AcceptTransfer(selectedFiles, clock)` | `InFlight` | Validates `selectedFiles` non-empty. Emits `TransferAcceptedLocallyDomainEvent`. |
| `PendingConsent` | `ExpireOffer(clock)` | `Cancelled` | Reason is strictly `TransferTerminationReason.OfferExpired`. Emits `TransferCancelledDomainEvent`. |
| `PendingConsent` | `Cancel(reason, clock)` | `Cancelled` | Reason is strictly `TransferTerminationReason.UserDeclined`. Terminal state. |
| `InFlight` | `VerifyAndRecordChunk(index, data, clock)` | `InFlight` | Piece verified. Emits `ChunkVerifiedDomainEvent`. If last chunk of file, emits `FileCompletedDomainEvent`. |
| `InFlight` | `VerifyAndRecordChunk(index, data, clock)` | `Completed` | Last required chunk verified, and `TargetSeedRatio.IsZero`. Emits `TransferCompletedDomainEvent`. |
| `InFlight` | `VerifyAndRecordChunk(index, data, clock)` | `Seeding` | Last required chunk verified, and `TargetSeedRatio > 0`. Emits `TransferCompletedDomainEvent`. |
| `InFlight` | `Pause()` | `Paused` | Suspends active chunk verification. Emits `TransferPausedDomainEvent`. |
| `InFlight` | `MarkStalled(clock)` | `Stalled` | Triggered when zero connected peers possess remaining missing pieces. Emits `TransferStalledDomainEvent`. |
| `InFlight` | `Cancel(reason, clock)` | `Cancelled` | User or timeout abortion. Emits `TransferCancelledDomainEvent`. Terminal state. |
| `InFlight` | `MarkFailed(reason, clock)` | `Failed` | Cryptographic verification or bundle corruption. Emits `TransferFailedDomainEvent`. Terminal state. |
| `Paused` | `Resume()` | `InFlight` | Resumes verification. Emits `TransferResumedDomainEvent`. |
| `Paused` | `Cancel(reason, clock)` | `Cancelled` | Terminal state. |
| `Stalled` | `RecordPeerHave` / `Bitfield` | `InFlight` | Transition occurs automatically when a newly recorded piece resolves stall condition. |
| `Stalled` | `Pause()` | `Paused` | Emits `TransferPausedDomainEvent`. |
| `Stalled` | `Cancel(reason, clock)` | `Cancelled` | Terminal state. |
| `Seeding` | `RecordUploadedBytes(bytes, clock)` | `Completed` | Transition occurs when `CurrentSeedRatio >= TargetSeedRatio`. Terminal state. |
| `Seeding` | `Pause()` | `Paused` | Temporarily halts seeding. Emits `TransferPausedDomainEvent`. |
| `Seeding` | `Cancel(reason, clock)` | `Cancelled` | Terminal state. |
| `Completed` | *(Any mutation)* | `Completed` | **Terminal State**: `IsSuccess = false` / throws `DomainValidationException`. State is immutable. |
| `Cancelled` | *(Any mutation)* | `Cancelled` | **Terminal State**: `IsSuccess = false` / throws `DomainValidationException`. State is immutable. |
| `Failed` | *(Any mutation)* | `Failed` | **Terminal State**: `IsSuccess = false` / throws `DomainValidationException`. State is immutable. |

---

### 2.2 `SwarmMember` Entity
Encapsulates swarm participant piece possession and **bidirectional flow control**:

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
    
    // Bidirectional Flow Control Properties:
    public PeerChokeStatus PeerChokingUs { get; private set; }         // Did remote peer choke us?
    public PeerChokeStatus WeChokingPeer { get; private set; }         // Are we choking remote peer?
    public PeerInterestStatus PeerInterestedInUs { get; private set; } // Is remote peer interested in us?
    public PeerInterestStatus WeInterestedInPeer { get; private set; } // Are we interested in remote peer?

    public SwarmPeerRole Role => Bitfield.IsComplete ? SwarmPeerRole.Seeder : SwarmPeerRole.Leecher;

    internal SwarmMember(
        PublicIdentityId peerId,
        Bitfield bitfield)
    {
        PeerId = peerId;
        Bitfield = bitfield;
        PeerChokingUs = PeerChokeStatus.Choked;
        WeChokingPeer = PeerChokeStatus.Choked;
        PeerInterestedInUs = PeerInterestStatus.NotInterested;
        WeInterestedInPeer = PeerInterestStatus.NotInterested;
    }

    internal void UpdateBitfield(Bitfield bitfield) => Bitfield = bitfield;
    internal void RecordHave(ChunkIndex chunkIndex) => Bitfield = Bitfield.WithChunk(chunkIndex);
    
    // Directional flow control setters:
    internal void SetPeerChokingUs(PeerChokeStatus status) => PeerChokingUs = status;
    internal void SetWeChokingPeer(PeerChokeStatus status) => WeChokingPeer = status;
    internal void SetPeerInterestedInUs(PeerInterestStatus status) => PeerInterestedInUs = status;
    internal void SetWeInterestedInPeer(PeerInterestStatus status) => WeInterestedInPeer = status;
}
```

---

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

    // Invariant-enforced Factory:
    public static TransferManifest Create(
        TransferSessionId manifestId,
        RootBundleName rootName,
        ChunkSizeBytes chunkSize,
        MerkleTreeIndex merkleTree,
        IReadOnlyList<FileEntry> files)
    {
        if (files == null || files.Count == 0)
        {
            throw new DomainValidationException("Transfer bundle must contain at least one file entry.");
        }

        long totalBytes = 0;
        foreach (FileEntry file in files)
        {
            totalBytes += file.FileSize.Bytes;
        }

        if (totalBytes <= 0)
        {
            throw new DomainValidationException("Transfer bundle total byte size must be greater than zero.");
        }

        // Strict contiguous layout invariant and zero-byte file semantics:
        long expectedOffset = 0;
        for (int i = 0; i < files.Count; i++)
        {
            FileEntry file = files[i];
            if (file.Index.Value != i)
            {
                throw new DomainValidationException($"File entry at index {i} has non-sequential index {file.Index.Value}.");
            }
            if (file.StartOffset.Offset != expectedOffset)
            {
                throw new DomainValidationException($"File '{file.RelativePath.Value}' start offset ({file.StartOffset.Offset}) does not match expected contiguous bundle offset ({expectedOffset}).");
            }
            // Zero-byte files have StartOffset == EndOffset and advance expected bundle offset by 0 bytes.
            expectedOffset += file.FileSize.Bytes;
        }

        if (expectedOffset != totalBytes)
        {
            throw new DomainValidationException($"Total bundle bytes ({totalBytes}) does not equal cumulative file offsets ({expectedOffset}).");
        }

        TotalBundleBytes bundleBytes = TotalBundleBytes.FromBytes(totalBytes);
        int chunkCount = (int)Math.Max(1, (totalBytes + chunkSize.Bytes - 1) / chunkSize.Bytes);

        if (merkleTree.LeafCount != chunkCount)
        {
            throw new DomainValidationException(
                $"Merkle tree leaf count ({merkleTree.LeafCount}) must match bundle chunk count ({chunkCount}).");
        }

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

    // Returns mathematically expected chunk length for a given chunk index:
    public ChunkByteLength GetExpectedChunkSize(ChunkIndex chunkIndex)
    {
        if (chunkIndex.Value < 0 || chunkIndex.Value >= TotalChunks.Count)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));

        long chunkStart = (long)chunkIndex.Value * ChunkSize.Bytes;
        long remainingBytes = TotalSize.Bytes - chunkStart;
        return ChunkByteLength.FromBytes((int)Math.Min((long)ChunkSize.Bytes, remainingBytes));
    }

    // Computes bitfield mask of required chunks for selective file download:
    public Bitfield GetRequiredChunks(SelectedFileIndices selectedFiles)
    {
        Bitfield required = Bitfield.Empty(TotalChunks);
        foreach (FileIndex index in selectedFiles.Indices)
        {
            FileEntry file = Files[index.Value];
            if (file.FileSize.Bytes == 0) continue;

            int firstChunk = (int)(file.StartOffset.Offset / ChunkSize.Bytes);
            int lastChunk = (int)((file.EndOffset.Offset - 1) / ChunkSize.Bytes);

            for (int c = firstChunk; c <= lastChunk; c++)
            {
                required = required.WithChunk(ChunkIndex.FromInt(c));
            }
        }
        return required;
    }
}
```

---

## 3. Strongly Typed Value Objects Catalog

All domain concepts in `FileTransfer.Domain` are modeled as strongly typed Value Objects with **zero implicit/explicit cast operators**, explicit factory methods, and named property accessors.

> [!IMPORTANT]
> **Strict Prohibition of Implicit / Explicit Conversion Operators**:
> To guarantee type safety, domain integrity, and prevent primitive obsession relapse:
> - **Zero implicit or explicit conversion operators are permitted** across any Domain Value Objects, Identifiers, or Entities.
> - Allowing implicit type conversions (e.g. converting a `ChunkIndex` to an `int` or `SanitizedRelativePath` to a `string`) breaks encapsulation, subverts domain boundary invariants, and permits accidental parameter transpositions (such as passing a `FileIndex` where a `ChunkIndex` is expected, or `FileByteOffset` where `BundleByteOffset` is expected).
> - All transformations must be 100% explicit via designated factory methods (e.g. `FromInt`, `FromBytes`, `FromOffset`, `Parse`) and named property accessors (e.g. `.Value`, `.Bytes`, `.Count`, `.Offset`).

| Value Object | Source Generator / Structure | Encapsulated Concept & Invariants | Zero Cast Guarantee |
| :--- | :--- | :--- | :--- |
| **`TransferSessionId`** | `[GuidId(GuidIdKind.CryptographicRandom)]` | 16-byte UUID identifying a transfer session | Explicit `FromGuid(Guid)`, `NewUnique()`, `.Value`. Zero cast operators. |
| **`MerkleRoot`** | `[ByteArray(length: 32)]` | 32-byte cryptographic SHA-256 Merkle root | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`ChunkHash`** | `[ByteArray(length: 32)]` | 32-byte SHA-256 leaf hash | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`TransferBundleSummary`** | `sealed record` | Cohesive bundle metadata (title, size, files, chunks, Merkle root) | Explicit `Create(...)`, `.RootName`, `.TotalSize`, `.TotalFiles`, `.ChunkSizeBytes`, `.TotalChunks`, `.MerkleRoot`. Zero cast operators. |
| **`TransferTopology`** | `sealed record` | Discriminated union of participant topology: OneToOne, MultiPeer, Incoming. Exposes pure participant query: `.ContainsParticipant(PublicIdentityId)`. Zero cast operators. |
| **`RootBundleName`** | `sealed record` | Sanitized display title for transfer root ($1 \le \text{Length} \le 255$) | Explicit `Parse(string)`, `.Value`. Zero cast operators. |
| **`TransferTerminationReason`**| `sealed record` | Reason for transfer rejection, cancellation, or failure | Explicit `UserDeclined`, `SenderCancelled`, `StorageExhausted`, `VerificationFailed`, `Timeout`, `OfferExpired`, `FromText(string)`, `.Value`. Zero cast operators. |
| **`SanitizedRelativePath`** | `sealed record` | Relative file path verified against directory traversal and recursion depth. Enforces max total length 1,024 bytes, max segment length 255 bytes, and max depth of 32 segments. Rejects empty/whitespace paths, leading/trailing separators, root/drive prefixes (`C:`), parent traversal (`..`), and current directory segments (`.`). Normalizes separators to canonical `/`. | Explicit `Parse(string)`, `.Value`. Zero cast operators. |
| **`FileChunkSlice`** | `sealed record` | Pure domain calculation mapping chunk byte slice to target file | Explicit `Create(...)`, `.FileIndex`, `.RelativePath`, `.OffsetInFile`, `.SliceLength`, `.BufferOffset`. Zero cast operators. |
| **`FileLastModifiedUtc`** | `readonly record struct` | Validated file modification timestamp ($0 \le \text{epoch} \le \text{future buffer}$) | Explicit `FromDateTimeOffset(DateTimeOffset)`, `FromUnixSeconds(long)`, `.UtcDateTime`, `.UnixSeconds`. Zero cast operators. |
| **`FileIndex`** | `readonly record struct` | Zero-indexed file sequence in manifest: $0 \le \text{Value} < \text{TotalFiles}$ | Explicit `FromInt(int)`, `.Value`. Zero cast operators. |
| **`ChunkIndex`** | `readonly record struct` | Zero-indexed 32-bit chunk sequence: $0 \le \text{Index} < \text{TotalChunks}$ | Explicit `FromInt(int)`, `WriteBigEndian(Span<byte>)`, `.Value`, `Next()`. Zero cast operators. |
| **`ChunkSizeBytes`** | `readonly record struct` | Bounded chunk size: $64\text{ KB} \le \text{Bytes} \le 4\text{ MB}$, default $1\text{ MB}$ | Explicit `Default`, `FromBytes(int)`, `.Bytes`. Zero cast operators. |
| **`TotalBundleBytes`** | `readonly record struct` | Combined byte size of bundle: $\text{Bytes} > 0$ | Explicit `FromBytes(long)`, `.Bytes`. Zero cast operators. |
| **`FileSizeBytes`** | `readonly record struct` | Byte size of single file: $\text{Bytes} \ge 0$ | Explicit `FromBytes(long)`, `RemainingSlice(FileByteOffset, ChunkByteLength)`, `.Bytes`. Zero cast operators. |
| **`BundleByteOffset`** | `readonly record struct` | Offset across global bundle byte layout: $\text{Offset} \ge 0$ | Explicit `FromOffset(long)`, `FromChunk(ChunkIndex, ChunkSizeBytes)`, `Add(FileSizeBytes)`, `Add(TransferByteLength)`, `Subtract(BundleByteOffset)`, `SubtractToFileOffset(BundleByteOffset)`, `Zero`, `.Offset`. Zero cast operators. |
| **`FileByteOffset`** | `readonly record struct` | Offset within specific file on disk: $\text{Offset} \ge 0$ | Explicit `FromOffset(long)`, `Add(TransferByteLength)`, `Zero`, `.Offset`. Zero cast operators. |
| **`FileCount`** | `readonly record struct` | Total file count in manifest: $\text{Count} \ge 1$ | Explicit `FromInt(int)`, `.Count`. Zero cast operators. |
| **`ChunkCount`** | `readonly record struct` | Total chunks in transfer: $\text{Count} \ge 1$ | Explicit `FromInt(int)`, `.Count`. Zero cast operators. |
| **`PeerCount`** | `readonly record struct` | Count of connected swarm peers: $\text{Count} \ge 0$ | Explicit `FromInt(int)`, `Zero`, `.Count`. Zero cast operators. |
| **`TransferByteLength`** | `readonly record struct` | Byte length slice for transfer operations: $\text{Bytes} \ge 0$ | Explicit `FromBytes(int)`, `.Bytes`. Zero cast operators. |
| **`ChunkByteLength`** | `readonly record struct` | Actual bounded byte length of an individual chunk: $1 \le \text{Bytes} \le 4\text{ MB}$ | Explicit `FromBytes(int)`, `Subtract(TransferByteLength)`, `.Bytes`. Zero cast operators. |
| **`PlaintextChunkData`** | `sealed record` | Decrypted chunk bytes for disk I/O ($1 \le \text{Bytes} \le 4\text{ MB}$) | Explicit `FromBytes(byte[])`, `FromMemory(ReadOnlyMemory<byte>)`, `.Memory`, `.Span`, `.Length`. Zero cast operators. |
| **`SeedRatio`** | `readonly record struct` | Upload/Download ratio cap: $\ge 0.0$ or Unlimited | Explicit `Zero`, `Unlimited`, `FromMultiplier(double)`, `.Multiplier`, `.IsUnlimited`. Rejects NaN, Infinity, negative values. Zero cast operators. |
| **`Bitfield`** | `sealed record` | Chunk possession bitmask: byte length $\lceil \text{TotalChunks} / 8 \rceil$. Big-endian bit ordering. Trailing unused bits strictly zeroed. **Immutable** | Explicit `WithChunk(ChunkIndex)` returning new instance; `Empty(ChunkCount)`, `Full(ChunkCount)`, `FromBytes(ReadOnlySpan<byte>, ChunkCount)`, `HasChunk(ChunkIndex)`, `HasAllChunks(Bitfield)`, `Intersect(Bitfield)`, `Difference(Bitfield)`, `PopCount`, `ToByteArray()`, `.ByteLength`, `.IsComplete`. Zero cast operators. |
| **`MerkleTreeIndex`** | `sealed record` | Encapsulated Merkle tree root and leaf verification index | Explicit `Create(root, leaves)`, `VerifyLeaf(index, span)`, `GetLeaf(index)`. Zero cast operators. |
| **`SelectedFileIndices`** | `sealed record` | Checked file indices: non-null immutable set of `FileIndex` | Explicit `All(FileCount)`, `FromIndices(...)`, `Contains(FileIndex)`, `.SelectedCount`, `.Indices`. Zero cast operators. |

---

### 3.1 Detailed Specification: `Bitfield` Value Object

```csharp
namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public sealed record Bitfield
{
    // Big-endian bit ordering (BitTorrent Standard):
    // Bit 0 is the most significant bit (0x80) of Byte 0.
    // Bit 7 is the least significant bit (0x01) of Byte 0.
    // Bit 8 is the most significant bit (0x80) of Byte 1.
    public ChunkCount TotalChunks { get; }
    public int ByteLength => (TotalChunks.Count + 7) / 8;
    public int PopCount { get; }
    public bool IsEmpty => PopCount == 0;
    public bool IsComplete => PopCount == TotalChunks.Count;

    private readonly byte[] _bytes;

    private Bitfield(byte[] bytes, ChunkCount totalChunks, int popCount)
    {
        _bytes = bytes;
        TotalChunks = totalChunks;
        PopCount = popCount;
    }

    public static Bitfield Empty(ChunkCount totalChunks)
    {
        int len = (totalChunks.Count + 7) / 8;
        return new Bitfield(new byte[len], totalChunks, 0);
    }

    public static Bitfield Full(ChunkCount totalChunks)
    {
        int len = (totalChunks.Count + 7) / 8;
        byte[] bytes = new byte[len];
        for (int i = 0; i < len; i++) bytes[i] = 0xFF;
        
        // Invariant: Trailing unused bits in the final byte must be strictly zeroed:
        int remainder = totalChunks.Count % 8;
        if (remainder != 0)
        {
            byte mask = (byte)(0xFF << (8 - remainder));
            bytes[len - 1] &= mask;
        }

        return new Bitfield(bytes, totalChunks, totalChunks.Count);
    }

    public static Bitfield FromBytes(ReadOnlySpan<byte> source, ChunkCount totalChunks)
    {
        int expectedLen = (totalChunks.Count + 7) / 8;
        if (source.Length != expectedLen)
            throw new DomainValidationException($"Bitfield byte length ({source.Length}) does not match expected ({expectedLen}) for {totalChunks.Count} chunks.");

        byte[] copy = source.ToArray();
        
        // Strict Invariant: Trailing unused bits must be 0:
        int remainder = totalChunks.Count % 8;
        if (remainder != 0)
        {
            byte trailingMask = (byte)(0xFF >> remainder);
            if ((copy[expectedLen - 1] & trailingMask) != 0)
                throw new DomainValidationException("Bitfield contains non-zero bits in trailing padding.");
        }

        int count = CountBits(copy);
        return new Bitfield(copy, totalChunks, count);
    }

    public bool HasChunk(ChunkIndex index)
    {
        if (index.Value < 0 || index.Value >= TotalChunks.Count) return false;
        int byteIdx = index.Value / 8;
        int bitIdx = 7 - (index.Value % 8);
        return (_bytes[byteIdx] & (1 << bitIdx)) != 0;
    }

    public Bitfield WithChunk(ChunkIndex index)
    {
        if (HasChunk(index)) return this;
        byte[] next = (byte[])_bytes.Clone();
        int byteIdx = index.Value / 8;
        int bitIdx = 7 - (index.Value % 8);
        next[byteIdx] |= (byte)(1 << bitIdx);
        return new Bitfield(next, TotalChunks, PopCount + 1);
    }

    public Bitfield WithoutChunk(ChunkIndex index)
    {
        if (!HasChunk(index)) return this;
        byte[] next = (byte[])_bytes.Clone();
        int byteIdx = index.Value / 8;
        int bitIdx = 7 - (index.Value % 8);
        next[byteIdx] &= (byte)~(1 << bitIdx);
        return new Bitfield(next, TotalChunks, PopCount - 1);
    }

    public bool HasAllChunks(Bitfield mask)
    {
        if (mask.TotalChunks != TotalChunks) return false;
        for (int i = 0; i < _bytes.Length; i++)
        {
            if ((_bytes[i] & mask._bytes[i]) != mask._bytes[i]) return false;
        }
        return true;
    }

    public Bitfield Intersect(Bitfield other)
    {
        if (other.TotalChunks != TotalChunks)
            throw new DomainValidationException("Cannot intersect bitfields with differing total chunk counts.");
        byte[] result = new byte[_bytes.Length];
        for (int i = 0; i < _bytes.Length; i++) result[i] = (byte)(_bytes[i] & other._bytes[i]);
        return new Bitfield(result, TotalChunks, CountBits(result));
    }

    public Bitfield Difference(Bitfield other)
    {
        if (other.TotalChunks != TotalChunks)
            throw new DomainValidationException("Cannot compute difference of bitfields with differing total chunk counts.");
        byte[] result = new byte[_bytes.Length];
        for (int i = 0; i < _bytes.Length; i++) result[i] = (byte)(_bytes[i] & ~other._bytes[i]);
        return new Bitfield(result, TotalChunks, CountBits(result));
    }

    public byte[] ToByteArray() => (byte[])_bytes.Clone();

    private static int CountBits(byte[] bytes)
    {
        int count = 0;
        foreach (byte b in bytes) count += System.Numerics.BitOperations.PopCount(b);
        return count;
    }
}
```

---

## 4. Pure Domain Events Hierarchy

Every state transition in `TransferSession` emits an immutable domain event conforming strictly to `IDomainEvent` (`Percolator.Domain.Kernel`), possessing `EventId` and `OccurredOnUtc`:

```csharp
namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record TransferOfferedDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId InitiatorId,
    TransferBundleSummary Summary,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record TransferAcceptedLocallyDomainEvent(
    TransferSessionId SessionId,
    SelectedFileIndices SelectedFiles,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record PeerAcceptedTransferDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId RecipientId,
    Bitfield ResumptionBitfield,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record SwarmMemberJoinedDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId PeerId,
    Bitfield InitialBitfield,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record ChunkVerifiedDomainEvent(
    TransferSessionId SessionId,
    ChunkIndex ChunkIndex,
    ChunkHash LeafHash,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record FileCompletedDomainEvent(
    TransferSessionId SessionId,
    FileIndex FileIndex,
    SanitizedRelativePath RelativePath,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record TransferCompletedDomainEvent(
    TransferSessionId SessionId,
    TotalBundleBytes TotalBytes,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record TransferPausedDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record TransferResumedDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record TransferStalledDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record TransferFailedDomainEvent(
    TransferSessionId SessionId,
    TransferTerminationReason Reason,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record TransferCancelledDomainEvent(
    TransferSessionId SessionId,
    TransferTerminationReason Reason,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
```

---

## 5. Pure Domain Algorithms & Boundary Math

### 5.1 Multi-File Boundary Spanning Calculation (`ChunkBoundaryCalculator`)
Encapsulates calculating byte offsets across multi-gigabyte continuous byte streams into target file slices with **zero I/O dependencies**:

```csharp
namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public static class ChunkBoundaryCalculator
{
    public static IReadOnlyList<FileChunkSlice> CalculateSlices(
        TransferManifest manifest,
        ChunkIndex chunkIndex,
        ChunkByteLength actualChunkSize)
    {
        List<FileChunkSlice> slices = [];
        BundleByteOffset chunkStart = BundleByteOffset.FromChunk(chunkIndex, manifest.ChunkSize);
        ChunkByteLength remainingChunkBytes = actualChunkSize;
        BundleByteOffset currentOffset = chunkStart;
        int bufferOffset = 0;

        while (remainingChunkBytes.Bytes > 0)
        {
            FileEntry file = FindFileForOffset(manifest.Files, currentOffset);
            FileByteOffset offsetInFile = currentOffset.SubtractToFileOffset(file.StartOffset);
            TransferByteLength bytesForThisFile = file.FileSize.RemainingSlice(offsetInFile, remainingChunkBytes);

            slices.Add(new FileChunkSlice(
                file.Index,
                file.RelativePath,
                offsetInFile,
                bytesForThisFile,
                bufferOffset));

            bufferOffset += bytesForThisFile.Bytes;
            currentOffset = currentOffset.Add(bytesForThisFile);
            remainingChunkBytes = remainingChunkBytes.Subtract(bytesForThisFile);
        }

        return slices;
    }

    private static FileEntry FindFileForOffset(IReadOnlyList<FileEntry> files, BundleByteOffset offset)
    {
        // Binary search over non-empty file bundle offsets (zero-byte files occupy 0 bytes and are skipped in chunk stream mapping)
        int low = 0;
        int high = files.Count - 1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            FileEntry candidate = files[mid];

            // If mid is a zero-byte file (StartOffset == EndOffset), advance search rightward
            if (candidate.FileSize.Bytes == 0)
            {
                if (offset.Offset < candidate.StartOffset.Offset)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
                continue;
            }

            if (offset.Offset < candidate.StartOffset.Offset)
            {
                high = mid - 1;
            }
            else if (offset.Offset >= candidate.EndOffset.Offset)
            {
                low = mid + 1;
            }
            else
            {
                return candidate;
            }
        }

        throw new DomainValidationException($"Offset {offset.Offset} falls outside bundle layout.");
    }
}
```

### 5.2 Merkle Tree Index Verification & Odd-Node Promotion
To eliminate Merkle tree malleability (CVE-2012-2459) while guaranteeing root hash determinism:
- **Leaf hashing**: $H_{\text{leaf}}(i) = \text{SHA-256}(0x00 \parallel \text{PlaintextChunk}_i)$
- **Interior node hashing**: $H_{\text{interior}}(\text{Left}, \text{Right}) = \text{SHA-256}(0x01 \parallel \text{Left} \parallel \text{Right})$
- **Odd-Node Promotion Rule**: When a level in the Merkle tree has an odd number of nodes, the final unpaired node is promoted directly to the next level **without duplicate hashing** ($H(N, N)$ is strictly prohibited).
- Encapsulated inside `MerkleTreeIndex.VerifyLeaf(ChunkIndex index, ReadOnlySpan<byte> plaintextChunk)`.

---

## 6. Domain Invariant Security & Integrity Invariants (Lens 3)

The pure domain kernel enforces mathematical, cryptographic, and structural defenses against invariant tampering and corrupted state, completely independent of network framing or operating system environments:

| Domain Integrity Threat | Pure Domain Defense | Verification Invariant |
| :--- | :--- | :--- |
| **Merkle Tree Malleability (CVE-2012-2459)** | Odd-node promotion rule promotes unpaired nodes directly to the next level without duplicate hashing ($H(N, N)$ prohibited). Domain separation prefixes (`0x00` leaf, `0x01` interior) prevent collision attacks. | `MerkleTreeIndex` deterministically produces root and rejects duplicate-node spoofing. |
| **Bitfield Padding Malleability** | `Bitfield` constructor validates that trailing unused bits in the final byte are strictly zero (`0x00`). `Bitfield` is immutable; bitwise operations return new instances with recomputed `PopCount`. | Instantiating `Bitfield` with non-zero padding bits throws `DomainValidationException`. |
| **Swarm Admission & Phantom Peers** | `RegisterSwarmMember` enforces `Topology.ContainsParticipant(peerId)`. Mutations (`RecordPeerHave`, `RecordPeerBitfield`, `RecordPeerChokingUs`) require prior registration in `_swarmMembers`. | Mutations with unknown or unauthorized peer IDs return `DomainResult.Failure`. |
| **Chunk Size Misalignment & Overrun** | `VerifyAndRecordChunk` validates input byte length against `manifest.GetExpectedChunkSize(chunkIndex)`. | Chunk data exceeding or falling short of expected size returns `DomainResult.Failure`. |
| **Duplicate Event Poisoning** | `VerifyAndRecordChunk` checks `LocalBitfield.HasChunk(chunkIndex)`. If already present, returns `DomainResult.Success` idempotently without re-emitting events. | Re-verifying chunks emits zero duplicate domain events. |
| **Bundle Layout Gaps & Overlaps** | `TransferManifest.Create` enforces contiguous offsets: $\text{StartOffset}_{i+1} == \text{StartOffset}_i + \text{FileSize}_i$ and $\sum \text{FileSize} == \text{TotalSize}$. Zero-byte files have $\text{StartOffset} == \text{EndOffset}$. | Non-contiguous offsets throw `DomainValidationException`. |
| **Logical Relative Path Recursion** | `SanitizedRelativePath.Parse` rejects absolute paths, drive roots (`C:`), parent traversal (`..`), current directory (`.`), and caps depth at 32 segments and length at 1,024 bytes. | Malicious paths throw `DomainValidationException`. |
| **Lifecycle Precondition Violations** | Formal State Transition Matrix (§2.1.1) gates all aggregate mutations. Terminal states (`Completed`, `Cancelled`, `Failed`) reject any further transitions. | Aggregate rejects out-of-order mutations across all invalid states. |
| **Seeding Ratio Tampering** | `SeedRatio` validates against negative numbers, NaN, and Infinity. Aggregate automatically signals completion when `CurrentSeedRatio >= TargetSeedRatio`. | Invalid multipliers throw `DomainValidationException`; completion triggers on target ratio. |

---

---

---

## 7. High-Value, Low-Brittle Domain Unit Testing Suite (Lens 4)

### 7.1 Testing Philosophy & Behavioral Invariants
In alignment with the Percolator domain testing standards, unit tests within `Percolator.Apps.FileTransfer.Domain` focus strictly on **observable domain outcomes, cryptographic integrity, state machine transitions, and mathematical boundary invariants**.

#### Anti-Brittle Directives:
1. **Zero Mock Invocation Assertions**: Tests never assert internal dispatch counts or method sequences. All domain tests evaluate pure state transformations, returned `DomainResult` values, and emitted `IDomainEvent` payloads.
2. **Zero Serialization & String Matching**: Tests never perform fragile string matching against exception messages. Tests assert typed domain errors, enum codes, or structured domain event properties.
3. **Pure State & Admission Testing**: Aggregates and entities are tested through deterministic input/output matrices, boundary condition limits, mathematical verification, and state transition matrices.
4. **Isolated Mathematical Kernel**: The domain contains zero network sockets, transport pools, or filesystem I/O, guaranteeing 100% deterministic, high-speed unit test execution.
5. **Deterministic Virtualized Clocks**: All temporal evaluations (such as offer expiration via `ExpireOffer`) must use an injected `IDateTimeProvider` virtualized clock, explicitly prohibiting real-time wall-clock delays (`Task.Delay`, `Thread.Sleep`).

---

### 7.2 Suite 1: `TransferSession` Lifecycle & State Transitions

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- | :--- |
| **DT-LIF-01** | Outgoing 1:1 session instantiation validation | `CreateOutgoing1to1` with empty or null recipient `PublicIdentityId`. | Throws `DomainValidationException`. Aggregate is not constructed. | Asserts aggregate cannot be constructed in headless state. |
| **DT-LIF-02** | Outgoing multi-peer session recipient validation | `CreateOutgoingMultiPeer` with empty recipient collection. | Throws `DomainValidationException`. Aggregate is not constructed. | Prevents headless push transfers. |
| **DT-LIF-03** | Peer acceptance transitions sender to `InFlight` | Outgoing session in `Offered` state receives `RecordPeerAcceptance(recipientId, bitfield)`. | State transitions to `InFlight`; `SwarmMember` is registered; emits `PeerAcceptedTransferDomainEvent`. | Verifies lifecycle progression and domain event emission. |
| **DT-LIF-04** | Valid pause and resume lifecycle transitions | Active session in `InFlight` receives `Pause()`, followed by `Resume()`. | `Pause()` transitions state to `Paused` (emits `TransferPausedDomainEvent`); `Resume()` transitions state to `InFlight` (emits `TransferResumedDomainEvent`). | Asserts bidirectional lifecycle control. |
| **DT-LIF-05** | Staged offer timeout expiration via virtual clock | Staged incoming session in `PendingConsent` receives `ExpireOffer(clock)` where virtual clock is advanced 25 hours. | State transitions to `Cancelled` with reason `OfferExpired`; emits `TransferCancelledDomainEvent`. | Verifies offer timeout lifecycle progression with zero wall-clock wait. |
| **DT-LIF-06** | Parametric state transition matrix integrity | Parametric theory testing all forbidden $(S_{\text{from}}, S_{\text{to}})$ state transition pairs (e.g., `Offered` $\to$ `Completed`, `Completed` $\to$ `InFlight`, `Cancelled` $\to$ `Paused`). | Every forbidden state transition returns `DomainResult.Failure`; state remains unchanged; zero domain events emitted. | Systematically proves state machine boundary integrity across all invalid paths. |
| **DT-LIF-07** | Terminal state mutation lockout | Session in terminal state (`Completed`, `Cancelled`, or `Failed`) receives `VerifyAndRecordChunk`, `Pause`, `Resume`, or `Cancel`. | Returns `DomainResult.Failure`; state remains unchanged; zero domain events emitted. | Prevents post-termination state corruption. |
| **DT-LIF-08** | Stalled state detection and event emission | Active session in `InFlight` detects missing pieces across all swarm members and receives `MarkStalled()`. | State transitions to `Stalled`; emits `TransferStalledDomainEvent`. | Signals swarm piece unavailability to application layer. |

---

### 7.3 Suite 2: `TransferManifest` & Contiguous Boundary Math

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- | :--- |
| **DT-MAN-01** | Empty bundle rejection | `TransferManifest.Create` with 0 files. | Throws `DomainValidationException`. | Enforces non-empty bundle invariant. |
| **DT-MAN-02** | Contiguous offset validation across files | File 0 (1000 bytes, `StartOffset = 0`), File 1 (`StartOffset = 1001` - gap of 1 byte). | `TransferManifest.Create` throws `DomainValidationException`. | Guards against layout gaps and corruption. |
| **DT-MAN-03** | Cumulative file size equality invariant | Manifest with files totaling 5,000 bytes, but final `expectedOffset != totalBytes`. | Throws `DomainValidationException`. | Guarantees total bundle size matches file sum. |
| **DT-MAN-04** | Sub-chunk bundle layout math | Single-file bundle of 500 bytes with `ChunkSizeBytes = 1 MB`. | Manifest computes `TotalChunks = 1`; `GetExpectedChunkSize(0) == 500 bytes`. | Verifies edge case where bundle is smaller than 1 chunk. |
| **DT-MAN-05** | Chunk boundary spanning across 2 adjacent files | Chunk 0 (1 MB) covers File 0 (600 KB) and File 1 (800 KB). | `ChunkBoundaryCalculator.CalculateSlices` returns exactly 2 slices: Slice 0 (600 KB for File 0), Slice 1 (424 KB for File 1). | Pure mathematical offset calculation without disk I/O. |
| **DT-MAN-06** | Chunk boundary spanning with interleaved zero-byte files | Chunk 0 (1 MB) spans File 0 (500 KB), File 1 (0 bytes), and File 2 (800 KB). | `CalculateSlices` returns exactly 2 slices (for File 0 and File 2); cleanly skips 0-byte File 1. | Proves zero-byte files do not disrupt chunk stream layout. |
| **DT-MAN-07** | Selective download required chunk bitfield calculation | 10-file bundle (1 MB files, 1 MB chunks). User selects files 2 and 5. | `manifest.GetRequiredChunks` returns bitfield with bits 2 and 5 set (PopCount = 2); bits 0, 1, 3, 4, 6, 7, 8, 9 unset. | Validates selective chunk requirement derivation. |
| **DT-MAN-08** | Relative path directory traversal sanitization | Relative path containing `../../etc/passwd` or `C:/file.txt`. | `SanitizedRelativePath.Parse` throws `DomainValidationException`. | Enforces path sanitization at parser boundary. |
| **DT-MAN-09** | Multi-chunk single-file boundary calculations | Single file of 2.5 MB with `ChunkSizeBytes = 1 MB` (3 chunks total). | Chunk 0: slice offset 0, length 1 MB; Chunk 1: slice offset 1 MB, length 1 MB; Chunk 2: slice offset 2 MB, length 500 KB. | Asserts multi-chunk continuous file layout math. |
| **DT-MAN-10** | All-zero-byte bundle rejection | `TransferManifest.Create` with bundle where all files have `FileSize = 0` (TotalSize = 0). | Throws `DomainValidationException` ("bundle total byte size must be greater than zero"). | Prevents zero-byte ghost transfers. |
| **DT-MAN-11** | Selected file indices out-of-bounds rejection | User creates `SelectedFileIndices` containing index $\ge \text{TotalFiles}$. | Throws `DomainValidationException`. | Prevents array index out-of-bounds crashes during chunk mask derivation. |

---

### 7.4 Suite 3: Immutable `Bitfield` Algebra & Piece Possession

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- | :--- |
| **DT-BIT-01** | Structural immutability of bitfield mutations | Calling `bitfield.WithChunk(index)` and `WithoutChunk(index)`. | Returns new `Bitfield` instance; original instance byte array and `PopCount` remain unchanged. | Guarantees thread-safe immutability without defensive copying. |
| **DT-BIT-02** | Trailing unused padding bit zero-mask enforcement | Instantiating `Bitfield.FromBytes` for 9 chunks (2 bytes needed: 8 bits in byte 0, 1 bit in byte 1) where trailing padding bits in byte 1 are non-zero. | Throws `DomainValidationException`. | Enforces zeroed unused bit invariant to prevent bitfield malleability. |
| **DT-BIT-03** | Boundary bitfield byte length calculations | `Bitfield.Empty` and `Full` for boundary chunk counts ($1, 7, 8, 9, 15, 16$ chunks). | Byte lengths are exactly 1, 1, 1, 2, 2, 2 bytes respectively; `Full` correctly zeros trailing padding bits. | Verifies mathematical bitfield sizing and padding masks. |
| **DT-BIT-04** | Bitfield set intersection algebra | Peer A bitfield (chunks 0, 1, 2) intersects Peer B bitfield (chunks 1, 2, 3). | `Intersect` returns bitfield with chunks 1, 2 set (`PopCount = 2`). | Validates swarm piece overlap calculation. |
| **DT-BIT-05** | Bitfield set difference algebra | Local missing bitfield computed via `other.Difference(local)`. | `Difference` returns chunks possessed by `other` that are absent locally. | Validates rarest-first piece scheduling input. |
| **DT-BIT-06** | Selective download completion evaluation | Session with `SelectedFiles` checking files 1 and 3. `LocalBitfield` satisfies all required chunks for files 1 and 3, but missing other files. | `session.IsDownloadComplete` evaluates to `true`. | Asserts selective completion logic ignores unselected files. |
| **DT-BIT-07** | Bitfield malformed byte length rejection | Calling `Bitfield.FromBytes` with byte array shorter or longer than $\lceil \text{TotalChunks} / 8 \rceil$ (e.g. 1 byte or 3 bytes for 10 chunks). | Throws `DomainValidationException`. | Rejects truncated or padded bitfield frames before processing. |

---

### 7.5 Suite 4: Cryptographic Merkle Tree & Integrity Invariants

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- | :--- |
| **DT-MRK-01** | Odd-node promotion rule verification | Merkle tree constructed from 3 leaf chunks (odd count). | Level 0 has 3 nodes; final node is promoted directly to Level 1 without duplicate hashing ($H(N, N)$ is not computed). Root is deterministic. | Proves resistance to CVE-2012-2459 duplicate-leaf malleability. |
| **DT-MRK-02** | Merkle leaf hash domain separation prefixes | Computing leaf hash vs interior node hash. | Leaf hash prefixes data with `0x00`; interior node prefixes children with `0x01`. | Prevents second-preimage collision attacks between leaves and interior nodes. |
| **DT-MRK-03** | Corrupted chunk leaf verification rejection | `VerifyAndRecordChunk` called with altered plaintext chunk bytes. | Fails leaf hash check against Merkle tree; returns `DomainResult.Failure`; bitfield unchanged; emits zero events. | Verifies chunk tampering detection. |
| **DT-MRK-04** | Idempotent chunk verification protection | `VerifyAndRecordChunk` called on chunk index already marked present in `LocalBitfield`. | Returns `DomainResult.Success` idempotently; emits zero duplicate `ChunkVerifiedDomainEvent` or `FileCompletedDomainEvent`. | Protects domain event stream from duplicate poisoning. |
| **DT-MRK-05** | Merkle tree leaf count boundary topologies | Constructing Merkle trees for $N = 1, 2, 4, 5$ leaves. | $N=1$: root equals single leaf hash; $N=2$: single level; $N=4$: balanced binary tree; $N=5$: 3-tier tree with odd promotion at Level 0 (5 nodes $\to$ 3 nodes $\to$ 2 nodes $\to$ root). All roots deterministic and collision-free. | Mathematically proves Merkle builder correctness across all structural edge cases. |

---

### 7.6 Suite 5: Swarm Admission & Flow Control (Choking/Interested)

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- | :--- |
| **DT-SWM-01** | Swarm member admission gating | Invoking `RegisterSwarmMember` with peer identity not present in `TransferTopology`. | Returns `DomainResult.Failure`. Member is not added to `_swarmMembers`. | Protects swarm aggregate boundary from unauthorized peers. |
| **DT-SWM-02** | Independent bidirectional choking flow control | Local node chokes remote peer (`SetWeChokingPeer(peer, Choked)`), while remote peer unchokes local node (`RecordPeerChokingUs(peer, Unchoked)`). | `WeChokingPeer == Choked` and `PeerChokingUs == Unchoked`. | Verifies asymmetric directional flow control states. |
| **DT-SWM-03** | Independent bidirectional interest flow control | Local node updates interest (`SetWeInterestedInPeer(peer, Interested)`), and remote peer signals interest (`RecordPeerInterestedInUs(peer, NotInterested)`). | `WeInterestedInPeer == Interested` and `PeerInterestedInUs == NotInterested`. | Verifies independent interest state tracking. |
| **DT-SWM-04** | Peer bitfield update dimension validation | Connected member submits bitfield with total chunk count differing from manifest. | `RecordPeerBitfield` returns `DomainResult.Failure`; member bitfield remains empty. | Prevents mismatched piece mask injection. |
| **DT-SWM-05** | Seeding ratio target auto-completion | Outgoing session with `TargetSeedRatio = 2.0`. Cumulative `UploadedBytes` reaches $2.0 \times \text{TotalSize}$. | `HasReachedTargetSeedRatio` evaluates to `true`; state transitions to `Completed`; emits `TransferCompletedDomainEvent`. | Enforces seeding economics invariant. |
| **DT-SWM-06** | File completion detection on last chunk verification | Verifying chunk index $i$ completes the final missing chunk spanning `FileEntry` $K$. | Emits `FileCompletedDomainEvent` containing file index and sanitized relative path. | Validates automated file boundary completion detection. |

---

---

## 8. Domain Persistence Independence & Boundary Encapsulation (Lens 5)

In strict adherence to Onion Architecture, `Percolator.Apps.FileTransfer.Domain` remains **100% database-agnostic and infrastructure-ignorant**. The domain contains zero references to SQLite, SQL data types, relational schemas, ORM attributes, or wire serialization formats.

### 8.1 Persistence Independence & Anti-Corruption Invariants
1. **Zero Database / ORM Contamination**:
   - Domain entities and value objects contain zero database attributes (e.g., `[Table]`, `[Key]`, `[Column]`), zero EF Core navigation hooks, and zero parameterless constructors designed for deserializers.
   - All state is strictly encapsulated behind read-only properties (`IReadOnlyCollection<SwarmMember>`, `LocalBitfield`, `TransferTopology`).
2. **Atomic Aggregate Persistence Capability**:
   - The aggregate root `TransferSession` encapsulates all child entities (`SwarmMember`), value objects (`Bitfield`, `SelectedFileIndices`), and invariants needed for persistence.
   - The persistence boundary must persist and load the aggregate root as an atomic whole, strictly preventing partial update side-doors.
3. **Pure Rehydration Invariant (Zero Re-Execution of Business Events)**:
   - For reconstituting persisted aggregate state without executing side-effects or re-emitting historic domain events, the aggregate provides a dedicated rehydration factory method:
     ```csharp
     public static TransferSession Rehydrate(
         TransferSessionId id,
         TransferRole role,
         TransferSessionState state,
         TransferTopology topology,
         TransferManifest manifest,
         Bitfield localBitfield,
         SelectedFileIndices? selectedFiles,
         SeedRatio targetSeedRatio,
         TotalBundleBytes uploadedBytes,
         IReadOnlyList<SwarmMember> swarmMembers,
         DateTimeOffset createdAtUtc,
         DateTimeOffset? completedAtUtc);
     ```
   - Rehydration populates state directly, verifying only structural integrity (non-null references, valid bitfield dimensions) while suppressing domain event publication.
4. **Zero Knowledge of Outbound Ports**:
   - In pure Onion Architecture, outbound persistence ports (`ITransferSessionRepository`, `IDesktopFileStorage`) are declared in the **Application layer** (`Percolator.Apps.FileTransfer.Ports`), never in the Domain layer.
   - The Domain kernel accepts only pure inputs and emits pure domain events, maintaining total architectural isolation from storage mechanisms.

---

## 9. Phased Domain Implementation Milestones

### Milestone 1: Standalone Domain Project, Primitives & Roslyn Integration
1. Create standalone project `Percolator.Apps.FileTransfer.Domain.csproj` targeting `net10.0` referencing `Percolator.Domain.Kernel` and `Percolator.SourceGenerators` (Analyzer).
2. Verify zero project references to `Percolator.Domain` or external network/storage packages.
3. Generate and implement all Domain Value Objects:
   - `TransferSessionId`, `MerkleRoot`, `ChunkHash`, `TransferBundleSummary`, `TransferTopology`, `RootBundleName`, `SanitizedRelativePath`, `TransferTerminationReason`.
   - Records & Structs: `FileChunkSlice`, `FileLastModifiedUtc`, `FileIndex`, `ChunkIndex`, `ChunkSizeBytes`, `TotalBundleBytes`, `FileSizeBytes`, `BundleByteOffset`, `FileByteOffset`, `FileCount`, `ChunkCount`, `PeerCount`, `TransferByteLength`, `ChunkByteLength`, `PlaintextChunkData`, `SeedRatio`, `Bitfield` (with PopCount & bitwise set operations), `MerkleTreeIndex`, `SelectedFileIndices`.
4. Implement Domain Enums: `TransferRole`, `TransferSessionState`, `SwarmPeerRole`, `PeerChokeStatus`, `PeerInterestStatus`.
5. Strictly verify **zero implicit/explicit cast operators** across all Value Objects.

### Milestone 2: Domain Entities & Aggregate Roots
1. Implement `TransferManifest : IEntity<TransferSessionId>` and `FileEntry : IEntity<FileIndex>` with strict count and size validation, `GetExpectedChunkSize`, and `GetRequiredChunks`.
2. Implement `SwarmMember : IEntity<PublicIdentityId>` with piece possession `Bitfield` and bidirectional flow control methods (zero transport liveness or socket candidates).
3. Implement `TransferSession : AggregateRoot<TransferSessionId>` with invariant factories (`CreateOutgoing1to1`, `CreateOutgoingMultiPeer`, `CreateIncomingStaged`), formal state transition matrix, seeding ratio tracking, selective download completion evaluation, exact chunk verification with odd-node Merkle promotion, and file completion detection.
4. Implement all 12 pure `IDomainEvent` records with `EventId` and `OccurredOnUtc`.
5. Implement `MerkleTreeIndex`, `MerkleTreeBuilder` (with odd-node promotion rule), and pure `ChunkBoundaryCalculator`.
6. Implement pure domain unit tests covering the Domain Unit Testing Matrix.
