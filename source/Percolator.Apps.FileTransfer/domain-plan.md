# Percolator.Apps.FileTransfer: Domain Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: In Progress)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Pending)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Pending)
- [ ] Implementation Gate: LOCKED (Requires Approval of Lenses 1-5)

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
    Cancelled = 0x08,        // Explicitly aborted by user
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

### 2.2 `SwarmMember` Entity
Encapsulates swarm participant piece possession, last-seen activity, and **bidirectional flow control**:

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

    public DateTimeOffset LastSeenUtc { get; private set; }
    public SwarmPeerRole Role => Bitfield.IsComplete ? SwarmPeerRole.Seeder : SwarmPeerRole.Leecher;

    internal SwarmMember(
        PublicIdentityId peerId,
        Bitfield bitfield,
        DateTimeOffset initialSeenUtc)
    {
        PeerId = peerId;
        Bitfield = bitfield;
        PeerChokingUs = PeerChokeStatus.Choked;
        WeChokingPeer = PeerChokeStatus.Choked;
        PeerInterestedInUs = PeerInterestStatus.NotInterested;
        WeInterestedInPeer = PeerInterestStatus.NotInterested;
        LastSeenUtc = initialSeenUtc;
    }

    internal void UpdateBitfield(Bitfield bitfield) => Bitfield = bitfield;
    internal void RecordHave(ChunkIndex chunkIndex) => Bitfield = Bitfield.WithChunk(chunkIndex);
    
    // Directional flow control setters:
    internal void SetPeerChokingUs(PeerChokeStatus status) => PeerChokingUs = status;
    internal void SetWeChokingPeer(PeerChokeStatus status) => WeChokingPeer = status;
    internal void SetPeerInterestedInUs(PeerInterestStatus status) => PeerInterestedInUs = status;
    internal void SetWeInterestedInPeer(PeerInterestStatus status) => WeInterestedInPeer = status;
    
    internal void Touch(IDateTimeProvider clock) => LastSeenUtc = clock.UtcNow;
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

| Value Object | Source Generator / Structure | Encapsulated Concept & Invariants | Zero Cast Guarantee |
| :--- | :--- | :--- | :--- |
| **`TransferSessionId`** | `[GuidId(GuidIdKind.CryptographicRandom)]` | 16-byte UUID identifying a transfer session | Explicit `FromGuid(Guid)`, `NewUnique()`, `.Value`. Zero cast operators. |
| **`MerkleRoot`** | `[ByteArray(length: 32)]` | 32-byte cryptographic SHA-256 Merkle root | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`ChunkHash`** | `[ByteArray(length: 32)]` | 32-byte SHA-256 leaf hash | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`TransferBundleSummary`** | `sealed record` | Cohesive bundle metadata (title, size, files, chunks, Merkle root) | Explicit `Create(...)`, `.RootName`, `.TotalSize`, `.TotalFiles`, `.ChunkSizeBytes`, `.TotalChunks`, `.MerkleRoot`. Zero cast operators. |
| **`TransferTopology`** | `sealed record` | Discriminated union of participant topology: OneToOne, MultiPeer, Incoming. Exposes pure participant query: `.ContainsParticipant(PublicIdentityId)`. Zero cast operators. |
| **`RootBundleName`** | `sealed record` | Sanitized display title for transfer root ($1 \le \text{Length} \le 255$) | Explicit `Parse(string)`, `.Value`. Zero cast operators. |
| **`TransferTerminationReason`**| `sealed record` | Reason for transfer rejection, cancellation, or failure | Explicit `UserDeclined`, `SenderCancelled`, `StorageExhausted`, `VerificationFailed`, `Timeout`, `OfferExpired`, `FromText(string)`, `.Value`. Zero cast operators. |
| **`SanitizedRelativePath`** | `sealed record` | Relative file path verified against directory traversal. Pure relative directory hierarchy: rejects empty/whitespace paths, leading/trailing separators, root/drive prefixes (`C:`), parent traversal (`..`), and current directory segments (`.`). Normalizes separators to canonical `/`. | Explicit `Parse(string)`, `.Value`. Zero cast operators. |
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
| **`Bitfield`** | `sealed record` | Chunk possession bitmask: byte length $\lceil \text{TotalChunks} / 8 \rceil$. **Immutable** | Explicit `WithChunk(ChunkIndex)` returning new instance; `Empty(ChunkCount)`, `HasChunk(ChunkIndex)`, `HasAllChunks(Bitfield)`, `ToByteArray()`, `.Length`, `.IsComplete`. Zero cast operators. |
| **`MerkleTreeIndex`** | `sealed record` | Encapsulated Merkle tree root and leaf verification index | Explicit `Create(root, leaves)`, `VerifyLeaf(index, span)`, `GetLeaf(index)`. Zero cast operators. |
| **`SelectedFileIndices`** | `sealed record` | Checked file indices: non-null immutable set of `FileIndex` | Explicit `All(FileCount)`, `FromIndices(...)`, `Contains(FileIndex)`, `.SelectedCount`, `.Indices`. Zero cast operators. |

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

## 6. High-Value Domain Unit Testing Matrix

| Test Category | Target Invariant Under Test | Observable Verification | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **Factory Invariant** | Instantiating 1:1 sender without recipient | Throws `DomainValidationException` | Asserts aggregate cannot be constructed in invalid state. |
| **Factory Invariant** | Instantiating 1:N sender with empty recipient list | Throws `DomainValidationException` | Prevents headless push transfers. |
| **Manifest Invariant** | Instantiating manifest with 0 files or 0 total bytes | Throws `DomainValidationException` | Enforces non-empty bundle invariant. |
| **Manifest Invariant** | Mismatched Merkle tree leaf count vs chunk count | Throws `DomainValidationException` | Enforces Merkle tree index integrity. |
| **Selective Completion** | Downloading 1 file out of 10 in bundle | Aggregate evaluates `IsDownloadComplete == true` once required chunks present | Validates selective completion invariant. |
| **Bidirectional Flow** | Peer chokes us while we keep peer unchoked | `PeerChokingUs == Choked` and `WeChokingPeer == Unchoked` | Validates independent directional flow control. |
| **Bitfield Validation** | Calling `RecordPeerBitfield` with mismatched byte length | Returns `DomainResult.Failure` | Enforces piece mask boundary validation. |
| **Bitfield Immutability** | Calling `Bitfield.WithChunk(index)` | Returns new instance; original unchanged | Asserts immutability of piece possession bitfields. |
| **Seeding Cap** | Calling `RecordUploadedBytes` reaching `TargetSeedRatio` | Aggregate transitions `State` to `Completed` | Validates seeding ratio enforcement. |
| **Boundary Spanning** | Chunk spanning exactly across 2 files | `ChunkBoundaryCalculator` produces 2 slice mappings | Pure domain offset math verification with zero storage mocks. |
| **File Completion** | Verifying last chunk of a multi-chunk file | Aggregate emits `FileCompletedDomainEvent` | Asserts aggregate directly evaluates file boundaries. |
| **Peer Acceptance** | Sender records peer acceptance via `RecordPeerAcceptance` | Aggregate registers member, transitions to `InFlight`, emits event | Verifies lifecycle transition and event emission. |
| **Traversal Rejection** | Manifest containing `../../etc/passwd` | `SanitizedRelativePath.Parse` throws exception | Asserts immediate validation rejection at parser level. |
| **SeedRatio Validation** | Passing negative multiplier or NaN to `SeedRatio` | Throws `DomainValidationException` | Enforces bounded invariant. |
| **Swarm Admission Gate** | Registering peer not in `TransferTopology` | `RegisterSwarmMember` returns `DomainResult.Failure` | Asserts aggregate root boundary protection against unauthorized swarm members. |
| **Contiguous Offsets** | Instantiating manifest with overlapping or gap file offsets | Throws `DomainValidationException` | Enforces contiguous bundle layout. |
| **Zero-Byte File Layout** | Bundle containing 0-byte file adjacent to non-empty files | Manifest validates contiguous offsets with `StartOffset == EndOffset`; `FindFileForOffset` correctly skips 0-byte file and maps chunks | Asserts pure mathematical layout for empty files without index disruption. |
| **Offer Expiration** | Calling `ExpireOffer` on staged session | Aggregate transitions `State` to `Cancelled` with reason `OfferExpired` and emits event | Verifies offer timeout lifecycle transition. |
| **Relative Path Hierarchy** | Attempting to parse `/etc/passwd`, `C:\Windows`, `foo/../bar`, or `.` into `SanitizedRelativePath` | Throws `DomainValidationException` | Asserts pure relative directory invariants. |
| **Chunk Size Validation** | Passing chunk data whose byte length does not equal `GetExpectedChunkSize(chunkIndex)` | `VerifyAndRecordChunk` returns `DomainResult.Failure` | Prevents buffer overrun and slice misalignment attacks. |
| **Idempotent Chunk Verification** | Calling `VerifyAndRecordChunk` on a chunk already present in `LocalBitfield` | Returns `DomainResult.Success` without re-emitting `ChunkVerifiedDomainEvent` or `FileCompletedDomainEvent` | Protects event stream from duplication or poisoning. |
| **State Gating Matrix** | Invoking `VerifyAndRecordChunk` when session is in `Paused`, `Completed`, or `Cancelled` | Returns `DomainResult.Failure` | Enforces state machine mutation precondition integrity. |
| **Merkle Tree Malleability** | Constructing Merkle tree with odd leaf count | Unpaired node is promoted without duplicate hashing; root calculation is deterministic | Asserts resistance to CVE-2012-2459 duplicate node malleability. |

---

## 7. Phased Domain Implementation Milestones

### Milestone 1: Standalone Domain Project, Primitives & Roslyn Integration
1. Create standalone project `Percolator.Apps.FileTransfer.Domain.csproj` targeting `net10.0` referencing `Percolator.Domain.Kernel` and `Percolator.SourceGenerators` (Analyzer).
2. Verify zero project references to `Percolator.Domain` or external network/storage packages.
3. Generate and implement all Domain Value Objects:
   - `TransferSessionId`, `MerkleRoot`, `ChunkHash`, `TransferBundleSummary`, `TransferTopology`, `RootBundleName`, `SanitizedRelativePath`, `TransferTerminationReason`.
   - Records & Structs: `FileChunkSlice`, `FileLastModifiedUtc`, `FileIndex`, `ChunkIndex`, `ChunkSizeBytes`, `TotalBundleBytes`, `FileSizeBytes`, `BundleByteOffset`, `FileByteOffset`, `FileCount`, `ChunkCount`, `PeerCount`, `TransferByteLength`, `ChunkByteLength`, `PlaintextChunkData`, `SeedRatio`, `Bitfield`, `MerkleTreeIndex`, `SelectedFileIndices`.
4. Implement Domain Enums: `TransferRole`, `TransferSessionState`, `SwarmPeerRole`, `PeerChokeStatus`, `PeerInterestStatus`.
5. Strictly verify **zero implicit/explicit cast operators** across all Value Objects.

### Milestone 2: Domain Entities & Aggregate Roots
1. Implement `TransferManifest : IEntity<TransferSessionId>` and `FileEntry : IEntity<FileIndex>` with strict count and size validation, `GetExpectedChunkSize`, and `GetRequiredChunks`.
2. Implement `SwarmMember : IEntity<PublicIdentityId>` with piece possession `Bitfield` and bidirectional flow control methods.
3. Implement `TransferSession : AggregateRoot<TransferSessionId>` with invariant factories (`CreateOutgoing1to1`, `CreateOutgoingMultiPeer`, `CreateIncomingStaged`), `RecordPeerAcceptance`, seeding ratio tracking, selective download completion evaluation, exact chunk verification with odd-node Merkle promotion, and file completion detection.
4. Implement all 12 pure `IDomainEvent` records with `EventId` and `OccurredOnUtc`.
5. Implement `MerkleTreeIndex`, `MerkleTreeBuilder` (with odd-node promotion rule), and pure `ChunkBoundaryCalculator`.
6. Implement pure domain unit tests covering the Domain Unit Testing Matrix.
