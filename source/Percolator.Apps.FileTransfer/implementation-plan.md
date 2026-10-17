# Percolator.Apps.FileTransfer Implementation Plan: Croc-Inspired High-Performance Desktop File Transfer

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: In Progress)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Pending)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Pending)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Pending)
- [ ] Implementation Gate: LOCKED (Requires Approval of Lenses 1-5)

---

## 1. Summary & Architectural Constraints

- **Target Project**: `Percolator.Apps.FileTransfer` (`AppId = 0x03`).
- **Target Environment**: **Desktop only** (multi-core processors, high-bandwidth unmetered networks, fast SSD/NVMe storage, large addressable memory, sparse file OS support).
- **Core Inspiration**: **croc** (multi-stream parallel socket saturation, direct P2P with relay pipe fallback, directory tree bundling, chunked streaming, bitfield-based resumption) combined with **BitTorrent swarm piece sharing, peer tracking, and selective downloads**.
- **Strict Onion Architecture Invariants**:
  - Depends **strictly** on `Percolator.Domain`, `Percolator.PluginSdk`, and `Percolator.SourceGenerators`.
  - Harmonizes aggregates and entities with `Percolator.Domain.Common` base contracts (`AggregateRoot<TId>`, `IEntity<TId>`, `IDomainEvent`, `DomainResult`, `DomainResult<T>`).
  - Zero reference to infrastructure/transport/storage libraries (no raw TCP/UDP sockets, gRPC stubs, SQLite, Protobuf wire encoders, or OS filesystem APIs). Technical realization is delegated to `Percolator.Infrastructure`.
  - Zero reference to UI controls, view frameworks, window dialogs, folder pickers, or card renderers. Presentation realization is delegated to `Percolator.Desktop`.
  - Implements `IAppPlugin` (`AppId.FileTransferControl = 0x03`), `IAppIngressFilter`, and `IAppPayloadHandler` from `Percolator.PluginSdk`.

### 1.1 Protocol Separation (In-Band Signaling vs. Out-of-Band Data Streaming)
- **In-Band Control Channel ($\le 64\text{ KB}$)**: Transfer session negotiations, manifest distribution, Merkle root hashes, session encryption keys, candidate network endpoints, and accept/reject decisions flow strictly through domain end-to-end encrypted ratchet channels (`DirectChannel` / `GroupChannel`). **Double Ratchet messages are never used for bulk byte transfers or high-frequency swarm traffic**.
- **Out-of-Band Data Streaming & Swarming**: Binary chunk transfers, `HAVE` announcements, `REQUEST_CHUNK` frames, and peer discoveries (PEX) run entirely out-of-band over **4 to 8 parallel encrypted streams** running over direct connections or relay proxy pipes.

### 1.2 Channel Topologies & Invariant Matrix (1:1 vs. Group, Direct vs. Relayed)
| Topology | In-Band Offer Channel | Out-of-Band Stream Transport | Swarm Discovery Mechanism |
| :--- | :--- | :--- | :--- |
| **1:1 Direct P2P (LAN)** | Pairwise Double Ratchet via `DirectChannel` | 4–8 Parallel Out-of-Band Streams | Direct LAN Candidate Exchange in Offer Payload |
| **1:1 Direct P2P (Opt-In WAN)** | Pairwise Double Ratchet via `DirectChannel` | 4–8 Parallel Direct Streams (UPnP / Relay STUN) | Direct Public Candidate Exchange (Opt-In Only) |
| **1:1 Relayed (Default WAN)** | Pairwise Double Ratchet via Relay Mailbox | 4–8 Relay Pipe Streams | Matched by `TransferSessionId` on Relay Pipe |
| **Group Direct (LAN)** | Sender Key Ratchet via `GroupChannel` | 4–8 Direct Streams between LAN members | Local Peer Announcements in Group Ratchet |
| **Group Relayed (WAN Swarm)** | Sender Key Ratchet via `GroupChannel` | 4–8 Relay Pipe Streams per Peer Pair | `BlindSwarmId` Distributed Tracker + PEX |
| **Mixed Topology** | Sender Key Ratchet via `GroupChannel` | Direct P2P for local peers; Relay Pipe for WAN peers | Hybrid: Local direct candidates + Relay tracker |

### 1.3 Sovereign Privacy & Opt-In WAN Direct Connect (UPnP & Decentralized Relay STUN)
- **Default Posture (Zero Public IP Exposure)**:
  - Direct P2P is enabled **by default only for local LAN endpoints** (verified RFC 1918 private subnets: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, and IPv6 link-local).
  - All remote WAN / internet transfers route strictly through encrypted **Relay Transfer Pipes** (`RelayPipeToken`) by default, completely masking physical IP addresses and geolocation from peers.
- **Opt-In WAN Direct Mode (`WanPolicy == DirectWanPolicy.ExplicitlyAllowed`)**:
  - Users transferring large files to trusted contacts may explicitly opt in to direct WAN connections to maximize throughput (unlimited gigabit speeds bypassing relay pipe caps).
  - **Decentralized Relay-Assisted STUN (Opt-In)**:
    - STUN (RFC 5389/8489) requires an external reflector with a public IP to echo back the client's reflexive public IP/port.
    - Because Percolator has **zero centralized infrastructure**, STUN does **not** rely on centralized third-party servers (e.g. Google/Twilio).
    - Instead, the client's existing connected **Relay node** acts as a decentralized STUN reflector (echoing the observed public UDP/TCP endpoint during rendezvous). Users may also manually configure a custom STUN server URI if desired.
  - **Local UPnP IGD Port Forwarding (Opt-In)**:
    - Requests the local home gateway router via UPnP IGD (`WANIPConnection`) to open a dedicated port mapping. Operates purely locally on the home LAN with **zero external server dependencies**.
  - Public IP candidates discovered via UPnP or Relay STUN are **never advertised** unless the user has explicitly opted into WAN direct connection for that transfer session.
- **Relay Operator Safeguards & Throttling**:
  - Relays advertise capabilities via `RelayFileTransferCapabilities` (`Capabilities: RelayTransferCapabilityFlags`, `MaxPipeBandwidth: TransferBandwidth`, `MaxConcurrentPipes: PipeConcurrencyLimit`).

### 1.4 Push-Based Multi-Peer Initiator
- Senders select one or more recipient peers (`IReadOnlyList<PublicIdentityId>`) and one or more files and/or directory trees.
- Senders dispatch transfer invitations across heterogeneous delivery topologies (Direct P2P or Relayed Store-and-Forward) on a per-peer basis.
- Senders can manually cancel or stop sharing a transfer at any time. Sockets and stream pools remain unallocated until at least one recipient opts in and connects.

### 1.5 Two-Tier Manifest Distribution (Discriminated Union & Clean Manifest Descriptor)
- To prevent leaking chat media descriptors (`BlobReference` with waveforms, thumbnails, blurhashes) and to eliminate illegal state spaces where manifest fields are simultaneously populated or missing, manifest delivery is encapsulated as a strongly typed domain discriminated union `ManifestDistribution`:
  - **Tier 1 (Inline $\le 64\text{ KB}$)**: Small manifests pack directly into the in-band E2EE ratchet offer envelope (`ManifestDistribution.FromInline(InlineManifestData)`).
  - **Tier 2 (Blob Storage > 64 KB)**: Large manifests offload to out-of-band blob storage referenced via a dedicated `ManifestBlobDescriptor` (`ManifestDistribution.FromBlob(ManifestBlobDescriptor)`), carrying strictly file-transfer metadata (`BlobId`, `CiphertextSha256`, `PlaintextSizeBytes`, `CiphertextSizeBytes`, `RelayEndpointUri`).
- Manifests contain torrent-like metadata only (`RootName`, `TotalSize`, `TotalFiles`, `ChunkSize`, `MerkleTree`, `Files`). No rich media previews, waveforms, or thumbnails.

### 1.6 Recipient Sovereign Privacy & PluginSdk Ingress Edge Discard
- **Silent by Default**: Inbound transfer invitations generate zero automatic wire response or acknowledgment (preventing sender probing/tracking).
- **Opt-In Response**: Recipients may explicitly configure whether to emit an acknowledgment upon manual accept/reject.
- **Ingress Kill-Switch & Edge Discard Abstraction (`IAppIngressFilter`)**:\
  - `PluginSdk` defines `IAppIngressFilter` with `IngressDecision Evaluate(InboundPayloadContext context)`.
  - Plugins can declare total edge disablement (`IsEnabled = false`) or evaluate sender/relay blocklists before payload deserialization.
  - When disabled or filtered, the application pipeline discards the raw bytes and returns success without invoking `IAppPayloadHandler.HandleInboundAsync` or parsing a single byte of protobuf/json.
  - This eliminates parser zero-day vulnerabilities, deserialization CPU exhaustion, and unauthorized traffic amplification.
- **Granular Filter Policy Aggregate (`FileTransferFilterPolicy`)**:
  - Modeled as an aggregate root `FileTransferFilterPolicy : AggregateRoot<PublicIdentityId>`.
  - Enforces policy encapsulation:
    - Blocked peers: `IReadOnlySet<PublicIdentityId> BlockedPeers`
    - Blocked relays: `IReadOnlySet<RelayPipeEndpointUri> BlockedRelays`
    - Domain methods: `BlockPeer(PublicIdentityId)`, `UnblockPeer(PublicIdentityId)`, `BlockRelay(RelayPipeEndpointUri)`, `UnblockRelay(RelayPipeEndpointUri)`, `EvaluateDecision(PublicIdentityId, RelayPipeEndpointUri?)`.
  - Persisted and hydrated atomically through `IFileTransferFilterPolicyRepository`.
  - Evaluated in $O(1)$ memory lookup at the ingress boundary.

### 1.7 Selective File Downloads & BitTorrent Global Byte Model
- Continuous global byte layout across the bundle (1 MB chunks).
- Users can select a subset of files from the manifest to download (`SelectedFileIndices`).
- Chunks overlapping only unselected files are skipped. Boundary-spanning chunks are verified against the Merkle tree, but only the sub-spans for selected files are committed to disk.

### 1.8 File Staging (`.percolator-part`) & Antivirus / OS Lock Protection
- Files are written with a `.percolator-part` extension during download to prevent Windows Defender, search indexers, or users from locking or launching incomplete binaries.
- Upon full verification of all chunks in a file, it is atomically renamed to its final target filename.

---

## 2. Inbound Domain State Machine & Privacy Decision Lifecycle

```mermaid
flowchart TD
    InboundPacket["Inbound Packet (AppId: 0x03)"] --> IngressCheck{"IAppIngressFilter.IsEnabled?"}
    IngressCheck -->|No| DiscardSilent["Discard Immediately (Zero Memory, Zero Deserialization)"]
    IngressCheck -->|Yes| FilterCheck{"Sender or Relay Blocked in Policy?"}
    FilterCheck -->|Yes| DropSilent["Drop Silently (Zero Logging, Zero Wire ACK)"]
    FilterCheck -->|No| StageInvite["Stage Inbound Session (State: PendingConsent)"]
    StageInvite --> ZeroWire["SILENT ON WIRE: Zero ACK / Zero Response Sent"]
    ZeroWire --> ExposeReadModel["Expose TransferSessionReadModel to Ports"]
    
    ExposeReadModel --> DomainAction{"Domain Action Command"}
    DomainAction -->|Accept(Destination, SelectedFiles)| StartDownload["Transition to InFlight -> Allocate Staging -> Connect Swarm"]
    DomainAction -->|Decline Silently| PurgeInvite["Purge Session Record (Zero Wire ACK)"]
    DomainAction -->|Decline with Notice| SendReject["Dispatch FileTransferRejectPayload"]
    DomainAction -->|Block Sender / Relay| AddBlock["Persist Filter Rule -> Discard Future Frames"]
```

### 2.1 Domain Aggregate Roots & Entities

#### `TransferSession` Aggregate Root & `SwarmMember` Entity
Following `Percolator.Domain.Common.AggregateRoot<TId>`, `TransferSession` models the transfer lifecycle as an aggregate boundary emitting domain events upon state transitions. Swarm peer states, bitfield tracking, piece verification, and participant boundaries are fully encapsulated within the aggregate root:

```csharp
namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

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

public sealed class SwarmMember : IEntity<PublicIdentityId>
{
    public PublicIdentityId Id => PeerId;
    public PublicIdentityId PeerId { get; }
    public Bitfield Bitfield { get; private set; }
    public PeerChokeStatus ChokeStatus { get; private set; }
    public PeerInterestStatus InterestStatus { get; private set; }
    public DateTimeOffset LastSeenUtc { get; private set; }
    public SwarmPeerRole Role => Bitfield.IsComplete ? SwarmPeerRole.Seeder : SwarmPeerRole.Leecher;

    internal SwarmMember(PublicIdentityId peerId, Bitfield bitfield, DateTimeOffset initialSeenUtc)
    {
        PeerId = peerId;
        Bitfield = bitfield;
        ChokeStatus = PeerChokeStatus.Choked;
        InterestStatus = PeerInterestStatus.NotInterested;
        LastSeenUtc = initialSeenUtc;
    }

    internal void UpdateBitfield(Bitfield bitfield) => Bitfield = bitfield;
    internal void RecordHave(ChunkIndex chunkIndex) => Bitfield = Bitfield.WithChunk(chunkIndex);
    internal void SetChoke(PeerChokeStatus status) => ChokeStatus = status;
    internal void SetInterest(PeerInterestStatus status) => InterestStatus = status;
    internal void Touch(DateTimeOffset timestamp) => LastSeenUtc = timestamp;
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
    public RelayPipeToken RelayPipeToken { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private readonly Dictionary<PublicIdentityId, SwarmMember> _swarmMembers = [];
    public IReadOnlyCollection<SwarmMember> SwarmMembers => _swarmMembers.Values;

    // Encapsulated domain methods returning DomainResult & emitting IDomainEvent:
    // DomainResult AcceptTransfer(RootDestinationPath destinationFolder, SelectedFileIndices selectedFiles, IDateTimeProvider clock)
    // DomainResult StartStreaming() -> zero infrastructure socket/stream parameters
    // DomainResult RegisterSwarmMember(PublicIdentityId peerId, Bitfield initialBitfield, IDateTimeProvider clock)
    // DomainResult RecordPeerBitfield(PublicIdentityId peerId, Bitfield bitfield)
    // DomainResult RecordPeerHave(PublicIdentityId peerId, ChunkIndex chunkIndex)
    // DomainResult SetPeerChoke(PublicIdentityId peerId, PeerChokeStatus status)
    // DomainResult SetPeerInterest(PublicIdentityId peerId, PeerInterestStatus status)
    // DomainResult VerifyAndCommitChunk(ChunkIndex chunkIndex, PlaintextChunkData data, IDateTimeProvider clock)
    // DomainResult CompleteTransfer(IDateTimeProvider clock)
    // DomainResult MarkStalled(IDateTimeProvider clock)
    // DomainResult MarkFailed(TransferTerminationReason reason, IDateTimeProvider clock)
    // DomainResult Pause()
    // DomainResult Resume()
    // DomainResult Cancel(TransferTerminationReason reason, IDateTimeProvider clock)
}
```

#### `FileTransferFilterPolicy` Aggregate Root
```csharp
namespace Percolator.Apps.FileTransfer.Features.IngressControl;

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

    public IngressDecision EvaluateDecision(PublicIdentityId senderId, RelayPipeEndpointUri? relayUri)
    {
        if (_blockedPeers.Contains(senderId)) return IngressDecision.DiscardSilently;
        if (relayUri != null && _blockedRelays.Contains(relayUri)) return IngressDecision.DiscardSilently;
        return IngressDecision.Accept;
    }
}
```

### 2.2 Formal Domain Events Hierarchy (`Percolator.Apps.FileTransfer.Features.TransferNegotiation.Events`)
Every domain transition in `TransferSession` emits an immutable domain event implementing `IDomainEvent` (`Percolator.Domain.Common`):

```csharp
namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation.Events;

public sealed record TransferOfferedDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId InitiatorId,
    TotalBundleBytes TotalSize,
    FileCount TotalFiles,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record TransferAcceptedDomainEvent(
    TransferSessionId SessionId,
    RootDestinationPath DestinationPath,
    SelectedFileIndices SelectedFiles,
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

## 3. Onion Architecture, Multi-Route Representation & Value Objects

### 3.1 Multi-Route Representation Architectural Decision
To support multiple relays, fallback endpoints, and direct routes without violating clean architecture:
1. **Core Domain Purity (`Percolator.Domain.Identities.Model.PeerContact`)**:
   - `PeerContact` in the domain kernel strictly represents cryptographic identity, safety numbers, TOFU trust levels, and device link proofs. It **never** hardcodes physical IP addresses, transient socket ports, or ephemeral routing delivery tokens.
   - Core domain aggregates remain 100% clean and free of transport pollution.
2. **Application-Layer Route Candidate Resolution (`Percolator.Application.Routing`)**:
   - `IPeerReachabilityService` resolves and prioritizes candidate routes for an outgoing transfer job:
     ```csharp
     public enum RoutePriority : byte
     {
         DirectLan = 0x01,
         DirectWanOptIn = 0x02,
         PrimaryRelay = 0x03,
         SecondaryRelay = 0x04
     }

     public sealed record PeerRouteCandidate(
         DeliveryRoute Route,
         RoutePriority Priority,
         DateTimeOffset? LastReachableUtc);
     ```
   - When initiating a file transfer, the sender dispatches invitations across the candidate route set, falling back gracefully if the primary relay is unreachable.

### 3.2 Strongly Typed Value Objects & Source Generators (Zero Primitive Obsession, Zero Cast Operators)
All domain concepts in `Apps.FileTransfer` are modeled as strongly typed Value Objects with **zero implicit/explicit cast operators**, explicit factory methods, and named property accessors. Naked strings, raw byte arrays, and naked integers are strictly forbidden across domain aggregates, payloads, and port interfaces.

#### Source Generator Integration (`Percolator.SourceGenerators`)
To eliminate repetitive defensive-copy boilerplate, range validation, and equality logic, value objects leverage the solution's Roslyn incremental source generators:
- **`[ByteArray(length: N)]`**: Generates a sealed partial record wrapping `ReadOnlyMemory<byte>` with exact length validation, defensive copies via `FromBytes(byte[])`/`FromSpan(ReadOnlySpan<byte>)`, zero-allocation span slicing (`.Span`), sequence equality (`Span.SequenceEqual`), and safe hash code computation.
- **`[ByteArray(minLength: M, maxLength: N)]`**: Enforces byte length bounded between $M$ and $N$.
- **`[GuidId(GuidIdKind.CryptographicRandom)]`**: Generates a readonly partial record struct wrapping an RFC 4122 v4 UUID with parse/format methods and zero cast operators.

#### Value Object Catalog
| Value Object | Source Generator / Structure | Encapsulated Concept & Invariants | Zero Cast Guarantee |
| :--- | :--- | :--- | :--- |
| **`TransferSessionId`** | `[GuidId(GuidIdKind.CryptographicRandom)]` | 16-byte UUID identifying a transfer session | Explicit `FromGuid(Guid)`, `NewUnique()`, `.Value`. Zero cast operators. |
| **`MerkleRoot`** | `[ByteArray(length: 32)]` | 32-byte cryptographic SHA-256 Merkle root | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`ChunkHash`** | `[ByteArray(length: 32)]` | 32-byte SHA-256 leaf hash | Explicit `FromBytes(...)`, `FromSpan(...)`, `.Span`. Zero cast operators. |
| **`TransferKey`** | `[ByteArray(length: 32)]` | 32-byte ephemeral AES-256 session key (`ISensitiveSecret`) | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Implements `Zeroize()`. Zero cast operators. |
| **`BlindSwarmId`** | `[ByteArray(length: 32)]` | 32-byte HMAC-SHA256 rendezvous token | Explicit `Derive(TransferKey)`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`EphemeralSwarmMemberToken`** | `[ByteArray(length: 16)]` | 16-byte ephemeral session token for anonymous swarm registration | Explicit `Derive(TransferKey, PeerId)`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`StreamAuthToken`** | `[ByteArray(length: 32)]` | 32-byte out-of-band stream authentication token | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`BaseNonce`** | `[ByteArray(length: 12)]` | 12-byte base nonce for AES-GCM deterministic derivation | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`RelayPipeToken`** | `[ByteArray(length: 32)]` | 32-byte authentication token for relay pipe rendezvous | Explicit `Generate()`, `FromBytes(...)`, `.Span`. Zero cast operators. |
| **`RelayRendezvousDescriptor`** | `sealed record` | Encapsulated relay endpoint URI and 32-byte auth token | Explicit `Create(RelayPipeEndpointUri, RelayPipeToken)`, `.EndpointUri`, `.Token`. Zero cast operators. |
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
| **`SeedRatio`** | `readonly record struct` | Upload/Download ratio cap: $\ge 0.0$ or Unlimited | Explicit `FromDouble(double)`, `Unlimited`, `.Multiplier`. Zero cast operators. |
| **`Bitfield`** | `sealed record` | Chunk possession bitmask: byte length $\lceil \text{TotalChunks} / 8 \rceil$. **Immutable** | Explicit `WithChunk(ChunkIndex)` returning new instance; `Empty(ChunkCount)`, `HasChunk(ChunkIndex)`, `ToByteArray()`, `.Length`, `.IsComplete`. Zero cast operators. |
| **`RoutePriority`** | `enum : byte` | Candidate route preference hierarchy | Explicit enum values: `DirectLan` (0x01), `DirectWanOptIn` (0x02), `PrimaryRelay` (0x03), `SecondaryRelay` (0x04). |
| **`DirectWanPolicy`** | `enum : byte` | Direct WAN candidate exposure policy | Explicit enum values: `DisabledByDefault` (0x01), `ExplicitlyAllowed` (0x02). |
| **`TransportRoutingPreference`** | `enum : byte` | In-band connection route preference | Explicit enum values: `DirectPreferred` (0x01), `RelayOnly` (0x02). |
| **`SwarmPeerRole`** | `enum : byte` | Swarm participant role for tracker registration | Explicit enum values: `Leecher` (0x01), `Seeder` (0x02). |
| **`PeerChokeStatus`** | `enum : byte` | Out-of-band peer choking flow control state | Explicit enum values: `Choked` (0x01), `Unchoked` (0x02). |
| **`PeerInterestStatus`** | `enum : byte` | Out-of-band peer chunk interest state | Explicit enum values: `NotInterested` (0x01), `Interested` (0x02). |
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

## 4. Vertical Feature Slice Architecture

Organized by autonomous feature capabilities rather than horizontal technical layers:

```
Percolator.Apps.FileTransfer/
├── Features/
│   ├── TransferNegotiation/        # In-band push offers, multi-peer invitations, silent recipient state machine
│   │   ├── TransferSession.cs      # AggregateRoot<TransferSessionId>: Domain session aggregate
│   │   ├── TransferSessionId.cs    # [GuidId] Value Object: RFC 4122 UUID
│   │   ├── TransferRole.cs         # Enum: Sender (0x01), Recipient (0x02)
│   │   ├── TransferSessionState.cs # Enum: Offered, PendingConsent, InFlight, Paused, Completed, Seeding, Stalled, Cancelled, Failed
│   │   ├── DirectWanPolicy.cs      # Enum: DisabledByDefault (0x01), ExplicitlyAllowed (0x02)
│   │   ├── TransportRoutingPreference.cs # Enum: DirectPreferred (0x01), RelayOnly (0x02)
│   │   ├── RoutePriority.cs        # Enum: DirectLan, DirectWanOptIn, PrimaryRelay, SecondaryRelay
│   │   ├── TransferKey.cs          # [ByteArray(32)] Value Object: AES key (ISensitiveSecret)
│   │   ├── BaseNonce.cs            # [ByteArray(12)] Value Object: AES-GCM nonce base
│   │   ├── RelayPipeToken.cs       # [ByteArray(32)] Value Object: Relay pipe rendezvous token
│   │   ├── RelayRendezvousDescriptor.cs # Value Object: Cohesive EndpointUri + Token
│   │   ├── StreamAuthToken.cs      # [ByteArray(32)] Value Object: 32-byte stream auth token
│   │   ├── RootBundleName.cs       # Value Object: Sanitized bundle display title
│   │   ├── RelayPipeEndpointUri.cs # Value Object: Validated relay pipe endpoint URI
│   │   ├── TransferTerminationReason.cs # Value Object: Typed termination / cancellation reason
│   │   ├── DirectEndpointCandidate.cs # Host, Port, DirectCandidateKind
│   │   ├── DirectCandidateKind.cs  # Enum: DirectLan, DirectWanUpnp, DirectWanStun
│   │   ├── RelayTransferCapabilityFlags.cs # [Flags] Enum: TransferPipes, SwarmTracker, StunReflection
│   │   ├── RelayFileTransferCapabilities.cs # Domain record: Capabilities, MaxPipeBandwidth, MaxConcurrentPipes
│   │   ├── PipeConcurrencyLimit.cs # Value Object: Bounded concurrent relay pipe stream limit
│   │   ├── FileTransferOfferPayload.cs
│   │   ├── FileTransferAcceptPayload.cs
│   │   ├── FileTransferRejectPayload.cs
│   │   ├── FileTransferCancelPayload.cs
│   │   ├── Events/                 # Formal domain events (IDomainEvent)
│   │   │   ├── TransferOfferedDomainEvent.cs
│   │   │   ├── TransferAcceptedDomainEvent.cs
│   │   │   ├── TransferStreamingStartedDomainEvent.cs
│   │   │   ├── SwarmMemberJoinedDomainEvent.cs
│   │   │   ├── ChunkVerifiedDomainEvent.cs
│   │   │   ├── FileCompletedDomainEvent.cs
│   │   │   ├── TransferCompletedDomainEvent.cs
│   │   │   ├── TransferPausedDomainEvent.cs
│   │   │   ├── TransferResumedDomainEvent.cs
│   │   │   ├── TransferStalledDomainEvent.cs
│   │   │   ├── TransferFailedDomainEvent.cs
│   │   │   └── TransferCancelledDomainEvent.cs
│   │   ├── ITransferNegotiationWorkflow.cs
│   │   └── TransferNegotiationWorkflow.cs
│   ├── ManifestManagement/         # Two-tier manifest distribution, MerkleTreeIndex, file boundaries
│   │   ├── TransferManifest.cs     # IEntity<TransferSessionId>: Complete manifest bundle entity
│   │   ├── FileEntry.cs            # IEntity<FileIndex>: File record with relative path, size, bundle offsets
│   │   ├── FileIndex.cs            # Value Object: 0-based file index
│   │   ├── ChunkIndex.cs           # Value Object: 0-based chunk sequence
│   │   ├── ChunkSizeBytes.cs       # Value Object: 1 MB bounded
│   │   ├── TotalBundleBytes.cs     # Value Object: Total byte size >= 0
│   │   ├── FileSizeBytes.cs        # Value Object: Single file byte size >= 0
│   │   ├── BundleByteOffset.cs     # Value Object: Bundle offset >= 0 with domain math
│   │   ├── FileByteOffset.cs       # Value Object: File offset >= 0
│   │   ├── FileLastModifiedUtc.cs  # Value Object: Sanitized file timestamp
│   │   ├── FileCount.cs            # Value Object: File count >= 0
│   │   ├── ChunkCount.cs           # Value Object: Chunk count >= 1
│   │   ├── TransferByteLength.cs   # Value Object: Non-negative byte length slice
│   │   ├── SanitizedRelativePath.cs # Value Object: Directory traversal defense
│   │   ├── InlineManifestData.cs   # [ByteArray(1..65536)] Value Object
│   │   ├── ManifestBlobDescriptor.cs # Value Object: Clean out-of-band manifest storage reference
│   │   ├── ManifestDistribution.cs # Value Object: Discriminated union for two-tier manifest
│   │   ├── MerkleRoot.cs           # [ByteArray(32)] Value Object
│   │   ├── ChunkHash.cs            # [ByteArray(32)] Value Object
│   │   ├── MerkleTreeIndex.cs      # Value Object: Encapsulates root and leaf verification
│   │   ├── IMerkleTreeBuilder.cs   # Domain-separated SHA-256 builder
│   │   └── MerkleTreeBuilder.cs
│   ├── MultiStreamEngine/          # Parallel stream scheduling (4-8 streams), framing, chunk dispatch
│   │   ├── StreamIndex.cs          # Value Object: 0..7 stream index
│   │   ├── StreamCount.cs          # Value Object: 0..8 stream count
│   │   ├── ChunkByteLength.cs      # Value Object: Bounded chunk byte length (1..4 MB)
│   │   ├── EncryptedChunkData.cs   # Value Object: AES-GCM ciphertext + auth tag
│   │   ├── IStreamFramePayload.cs  # Strongly typed frame payload contract
│   │   ├── StreamFrameEnvelope.cs  # Typed framing envelope wrapping IStreamFramePayload
│   │   ├── StreamFrameType.cs      # Typed frame discriminator enum
│   │   ├── HandshakeFrame.cs       # StreamAuthToken, SessionId, StreamIndex
│   │   ├── BitfieldFrame.cs        # Bitfield possession frame
│   │   ├── HaveFrame.cs            # ChunkIndex announcement frame
│   │   ├── RequestChunkFrame.cs    # ChunkIndex request frame
│   │   ├── ChunkDataFrame.cs       # ChunkIndex + EncryptedChunkData frame
│   │   ├── CancelChunkFrame.cs     # ChunkIndex cancellation frame
│   │   ├── ChokeFrame.cs           # Choke flow-control frame
│   │   ├── UnchokeFrame.cs         # Unchoke flow-control frame
│   │   ├── KeepAliveFrame.cs       # Stream keep-alive frame
│   │   ├── PexPeersFrame.cs        # Peer candidate exchange frame
│   │   ├── ITransferStreamScheduler.cs
│   │   └── TransferStreamScheduler.cs
│   ├── SwarmCoordination/          # BitTorrent piece tracking, bitfields, rarest-first piece selection, distributed tracker
│   │   ├── BlindSwarmId.cs         # [ByteArray(32)] Value Object: HMAC-SHA256 handle
│   │   ├── EphemeralSwarmMemberToken.cs # [ByteArray(16)] Value Object: Anonymous swarm registration
│   │   ├── SwarmMember.cs          # IEntity<PublicIdentityId>: Encapsulated swarm peer entity
│   │   ├── SwarmPeerRole.cs        # Enum: Leecher (0x01), Seeder (0x02)
│   │   ├── PeerChokeStatus.cs      # Enum: Choked (0x01), Unchoked (0x02)
│   │   ├── PeerInterestStatus.cs   # Enum: NotInterested (0x01), Interested (0x02)
│   │   ├── PeerCount.cs            # Value Object: Peer count >= 0
│   │   ├── Bitfield.cs             # Value Object: Immutable piece possession bitmask
│   │   ├── SeedRatio.cs            # Value Object: Target upload ratio
│   │   ├── ISwarmCoordinator.cs
│   │   ├── SwarmCoordinator.cs
│   │   └── RarestFirstChunkSelector.cs
│   ├── DesktopStorage/             # Multi-file boundary spanning, selective downloads, .percolator-part staging
│   │   ├── PlaintextChunkData.cs   # Value Object: Unencrypted chunk data for disk I/O
│   │   ├── DiskSpacePreflightResult.cs # Domain Result: Pre-flight storage capacity check
│   │   ├── SelectedFileIndices.cs  # Value Object: Checked FileIndex set
│   │   ├── RootDestinationPath.cs  # Value Object: Validated absolute folder path
│   │   ├── ResolvedDestinationFilePath.cs # Value Object: Validated combined absolute destination file path
│   │   ├── IDiskChunkAssembler.cs
│   │   ├── DiskChunkAssembler.cs
│   │   └── BitfieldScanner.cs
│   └── IngressControl/             # Edge kill-switch, relay/peer filtering, zero-deserialization discard
│       ├── FileTransferFilterPolicy.cs # AggregateRoot<PublicIdentityId>: Peer & relay blocklist entity
│       ├── FileTransferIngressFilter.cs # Concrete IAppIngressFilter implementation
│       └── FileTransferPayloadHandler.cs # Ingress dispatcher
├── Ports/                          # Decoupled infrastructure and plugin ports
│   ├── ITransferStreamPool.cs      # Multi-stream network pool port
│   ├── IDistributedTrackerPort.cs  # Swarm peer & bitfield coordination port
│   ├── IDesktopFileStorage.cs      # Abstract block/chunk storage port
│   ├── ITransferRateLimiter.cs     # Bandwidth throttling port (BandwidthLimit)
│   ├── BandwidthLimit.cs           # Value Object: Unlimited vs Capped speed limit
│   ├── ITransferSessionRepository.cs # Encrypted session persistence port (atomic aggregate persistence)
│   ├── IFileTransferFilterPolicyRepository.cs # Filter policy persistence port
│   ├── IFileTransferQueryService.cs  # CQRS transfer manager read queries
│   ├── ManifestFileReadModel.cs    # CQRS manifest file projection record
│   └── IFileTransferActionHandler.cs # CQRS transfer manager action commands (returns DomainResult)
└── FileTransferPlugin.cs           # IAppPlugin (AppId: 0x03)
```

---

## 5. Multi-Stream Scheduling & Out-of-Band Coordination

### 5.1 Bandwidth-Delay Product (BDP) Mitigation
Single-stream transfers across high-latency internet routes suffer severe throughput degradation because single TCP congestion windows scale slowly.
Following **croc**:
1. Every transfer session opens a pool of **4 to 8 parallel multiplexed streams** (`ITransferStreamPool`).
2. Data is divided into uniform **1 MB chunks** (`ChunkSizeBytes`).
3. Chunks are scheduled concurrently across all active streams in the pool via `ITransferStreamScheduler`.
4. Work-stealing scheduler: Detects stalled streams and reassigns in-flight chunk requests to faster streams in the pool.

---

## 6. Cryptographic Security & Chunk Verification

### 6.1 Ephemeral Session Keys & Per-Chunk Nonce Derivation
- **Key Generation**: For every file transfer session, the sender cryptographically generates:
  $$K_{\text{transfer}} \leftarrow \text{CSPRNG}(32\text{ bytes})$$
  $$\text{BaseNonce} \leftarrow \text{CSPRNG}(12\text{ bytes})$$
- **Deterministic Nonce Derivation**:
  To guarantee nonce uniqueness without re-negotiation overhead, chunk nonces are derived deterministically:
  $$\text{ChunkNonce}(i) = \text{BaseNonce} \oplus \text{BigEndianUInt96}(i)$$
- **Payload Ciphertext**:
  Each 1 MB chunk is encrypted using AES-256-GCM:
  $$C_i, T_i \leftarrow \text{AES-256-GCM-Encrypt}(K_{\text{transfer}}, \text{ChunkNonce}(i), \text{PlaintextChunk}_i, \text{AAD}_i)$$
  where $\text{AAD}_i = \text{SessionId} \parallel \text{BigEndianUInt32}(i)$.
  The 16-byte authentication tag $T_i$ is appended directly to the chunk ciphertext.

### 6.2 Merkle Tree Verification & Domain Separation
To defend against chunk substitution or second-preimage attacks:
- **Leaf Hashing**:
  $$H_{\text{leaf}}(i) = \text{SHA-256}(0x00 \parallel \text{PlaintextChunk}_i)$$
- **Interior Node Hashing**:
  $$H_{\text{interior}}(\text{Left}, \text{Right}) = \text{SHA-256}(0x01 \parallel \text{Left} \parallel \text{Right})$$
- **Root Verification**:
  - The root hash of the tree is included in the in-band `FileTransferOfferPayload`.
  - The complete list of leaf hashes (`ChunkHashes`) is encapsulated in `MerkleTreeIndex` inside the manifest.
  - Before committing any chunk to disk, the recipient verifies:
    1. AES-256-GCM authentication tag verifies successfully.
    2. `Manifest.MerkleTree.VerifyLeaf(chunkIndex, plaintextSpan)` verifies successfully.
  - Corrupt or tampered chunks are immediately discarded; the stream requests a re-transmission.

---

## 7. Directory Recursion, Manifest Distribution & Storage Abstraction

### 7.1 Path Sanitization & Traversal Defense
- All relative paths are strictly validated before acceptance.
- Paths containing `..`, absolute paths (e.g. `/etc/passwd` or `C:\Windows`), null bytes, or Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`) are rejected immediately.
- Paths are normalized using forward slashes (`/`).
- File permissions are **not** transmitted. Only file size, relative path, start offset, and sanitized last modified timestamp (`FileLastModifiedUtc`) are preserved.

### 7.2 Manifest Data Structure (Domain Entities)
```csharp
namespace Percolator.Apps.FileTransfer.Features.ManifestManagement;

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

### 7.3 Selective File Downloading Architecture
1. Recipient selects `SelectedFileIndices SelectedFiles`.
2. Required chunk union calculation:
   $$\text{RequiredChunks} = \bigcup_{f \in \text{SelectedFiles}} \left\{ k \;\middle|\; k \times \text{ChunkSize} < \text{EndOffset}_f \;\land\; (k + 1) \times \text{ChunkSize} > \text{StartOffset}_f \right\}$$
3. Chunks not in $\text{RequiredChunks}$ are marked `Ignored` in the scheduler bitfield.
4. Boundary-spanning chunks are downloaded and verified against the Merkle tree, but only sub-spans belonging to selected files are committed to disk.

### 7.4 Multi-File Boundary Spanning Math
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

---

## 8. Swarming Mechanics, Distributed Trackers & Teardown

### 8.1 Multi-Seeder Swarm Independence
- As recipients finish downloading individual 1 MB chunks, they become seeders for those chunks to the rest of the swarm.
- If the original sender disconnects or stops sharing, the remaining peers continue swarming pieces among themselves as long as the full set of chunks is distributed across the swarm.

### 8.2 Distributed Swarm Tracker Architecture
- **Blind Swarm Identifier**:
  $$\text{BlindSwarmId} = \text{HMAC-SHA256}(K_{\text{transfer}}, \text{"swarm-rendezvous"})$$
- **Rendezvous Posting**:
  - Peers register their ephemeral direct candidates and bitfields under `BlindSwarmId` using an anonymous session `EphemeralSwarmMemberToken`.
  - Relays store ephemeral 90-second sliding records without learning file names, channels, or peer cryptographic identities (`PublicIdentityId`).
- **Out-of-Band Peer Exchange (PEX)**:
  - Peers exchange known active candidate addresses directly over out-of-band streams via `PexPeers` frames.

### 8.3 Graceful Teardown & Disconnect State Machines
1. **Sender Stops Sharing**:
   - Sender invokes cancel command. Broadcasts `Choke` frame and disconnects stream pool. Unregisters from `BlindSwarmId`.
   - Swarm survival: If 100% of pieces exist across remaining peers, swarming continues. If pieces are missing, peers transition to `Stalled` state waiting for seeders to reconnect. If storage quota is exhausted or unrecoverable verification errors occur, the session transitions to `Failed`.
2. **Recipient Stops / Pauses**:
   - Recipient pauses or cancels. Broadcasts `CancelChunk` for in-flight requests and closes stream pool. Other peers update bitfield records.
3. **Tracker Expiration**:
   - All tracker records expire after 90 seconds of silence without keep-alive pings. Zero disk persistence.

---

## 9. Typed Application Payloads & Framing Contracts

*(Note: Raw big-endian binary framing layouts, Protobuf schemas, and socket multiplexing are strictly encapsulated in `Percolator.Infrastructure`)*.

### 9.1 In-Band Control Channel Payloads (`Percolator.Apps.FileTransfer.Features.TransferNegotiation`)
Carried inside the Double Ratchet envelope with `AppId.FileTransferControl = 0x03`:

```csharp
public enum FileTransferMessageType : byte
{
    Offer = 0x01,
    Accept = 0x02,
    Reject = 0x03,
    Cancel = 0x04
}

public enum DirectCandidateKind : byte
{
    DirectLan = 0x01,
    DirectWanUpnp = 0x02,
    DirectWanStun = 0x03
}

public sealed record DirectEndpointCandidate(
    EndpointHost Host,
    PortNumber Port,
    DirectCandidateKind Kind);

public sealed record FileTransferOfferPayload(
    TransferSessionId TransferSessionId,
    RootBundleName RootName,
    TotalBundleBytes TotalSize,
    FileCount TotalFiles,
    ChunkSizeBytes ChunkSizeBytes,
    ChunkCount TotalChunks,
    MerkleRoot MerkleRoot,
    ManifestDistribution Manifest,              // Discriminated union: InlineManifest vs RemoteManifestBlob
    TransferKey SessionEncryptionKey,           // 32-byte AES key
    BaseNonce BaseNonce,                        // 12-byte base nonce
    StreamAuthToken StreamAuthToken,            // 32-byte auth token for out-of-band streams
    RelayRendezvousDescriptor? RelayRendezvous, // Encapsulated endpoint URI + relay token
    IReadOnlyList<DirectEndpointCandidate> DirectCandidates);

public sealed record FileTransferAcceptPayload(
    TransferSessionId TransferSessionId,
    IReadOnlyList<DirectEndpointCandidate> ReceiverCandidates,
    TransportRoutingPreference RoutingPreference,
    Bitfield ResumptionBitfield);                // Bitmap of chunks already possessed

public sealed record FileTransferRejectPayload(
    TransferSessionId TransferSessionId,
    TransferTerminationReason Reason);

public sealed record FileTransferCancelPayload(
    TransferSessionId TransferSessionId,
    TransferTerminationReason Reason);
```

### 9.2 Out-of-Band Stream Frame Envelopes (`Percolator.Apps.FileTransfer.Features.MultiStreamEngine`)
Typed frame abstraction for out-of-band data streams:

```csharp
public enum StreamFrameType : byte
{
    Handshake = 0x01,
    Bitfield = 0x02,
    Have = 0x03,
    RequestChunk = 0x04,
    ChunkData = 0x05,
    CancelChunk = 0x06,
    Choke = 0x07,
    Unchoke = 0x08,
    KeepAlive = 0x09,
    PexPeers = 0x0A
}

public interface IStreamFramePayload
{
    StreamFrameType FrameType { get; }
}

public sealed record HandshakeFrame(StreamAuthToken AuthToken, TransferSessionId SessionId, StreamIndex StreamIndex) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Handshake;
}

public sealed record BitfieldFrame(Bitfield Bitfield) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Bitfield;
}

public sealed record HaveFrame(ChunkIndex ChunkIndex) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Have;
}

public sealed record RequestChunkFrame(ChunkIndex ChunkIndex) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.RequestChunk;
}

public sealed record ChunkDataFrame(ChunkIndex ChunkIndex, EncryptedChunkData Data) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.ChunkData;
}

public sealed record CancelChunkFrame(ChunkIndex ChunkIndex) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.CancelChunk;
}

public sealed record ChokeFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Choke;
}

public sealed record UnchokeFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Unchoke;
}

public sealed record KeepAliveFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.KeepAlive;
}

public sealed record PexPeersFrame(IReadOnlyList<DirectEndpointCandidate> Candidates) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.PexPeers;
}

public sealed record StreamFrameEnvelope(IStreamFramePayload Payload);
```

---

## 10. Required Outbound Port Definitions (`Percolator.Apps.FileTransfer.Ports`)

### 10.1 PluginSdk Ingress Edge Pre-Filter Contracts
```csharp
namespace Percolator.PluginSdk;

public enum IngressDecision : byte
{
    Accept = 0x01,
    DiscardSilently = 0x02
}

public interface IAppIngressFilter
{
    AppId TargetAppId { get; }
    bool IsEnabled { get; }
    IngressDecision Evaluate(InboundPayloadContext context);
}
```

### 10.2 Network Stream Pool & Distributed Tracker Ports
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferStream : IAsyncDisposable
{
    StreamIndex Index { get; }
    ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default);
    ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default);
}

public interface ITransferStreamPool : IAsyncDisposable
{
    StreamCount ActiveStreams { get; }
    ValueTask ConnectDirectAsync(IReadOnlyList<DirectEndpointCandidate> candidates, StreamAuthToken authToken, CancellationToken ct = default);
    ValueTask ConnectRelayPipeAsync(RelayRendezvousDescriptor rendezvous, TransferSessionId sessionId, CancellationToken ct = default);
    ITransferStream GetStream(StreamIndex index);
    ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default);
}

public interface ITransferStreamPoolFactory
{
    ITransferStreamPool CreatePool(TransferSessionId sessionId, StreamCount streamCount);
    ITransferStreamPool CreatePool(TransferSessionId sessionId); // Defaults to StreamCount.Default
}

public interface IDistributedTrackerPort
{
    ValueTask<IReadOnlyList<DirectEndpointCandidate>> AnnounceAsync(
        BlindSwarmId blindSwarmId,
        EphemeralSwarmMemberToken memberToken,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        SwarmPeerRole role,
        CancellationToken ct = default);
}
```

### 10.3 Desktop File Storage Port
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface IDesktopFileStorage
{
    ValueTask PreAllocateFilesAsync(RootDestinationPath destinationRoot, IReadOnlyList<FileEntry> files, SelectedFileIndices selectedIndices, CancellationToken ct = default);
    ValueTask WriteChunkAsync(ResolvedDestinationFilePath targetPath, FileByteOffset fileOffset, PlaintextChunkData data, CancellationToken ct = default);
    ValueTask<PlaintextChunkData> ReadChunkAsync(ResolvedDestinationFilePath targetPath, FileByteOffset fileOffset, TransferByteLength length, CancellationToken ct = default);
    ValueTask<Bitfield> ScanAndComputeBitfieldAsync(RootDestinationPath rootPath, TransferManifest manifest, CancellationToken ct = default);
    ValueTask FinalizeFileAsync(ResolvedDestinationFilePath targetPath, FileLastModifiedUtc lastModifiedUtc, CancellationToken ct = default);
    ValueTask<DiskSpacePreflightResult> CheckDiskSpaceAsync(RootDestinationPath rootPath, TotalBundleBytes requiredBytes);
}
```

### 10.4 Rate Limiter Port
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferRateLimiter
{
    void SetGlobalSpeedCaps(BandwidthLimit downloadCap, BandwidthLimit uploadCap);
    void SetSessionSpeedCaps(TransferSessionId sessionId, BandwidthLimit downloadCap, BandwidthLimit uploadCap);
    ValueTask ThrottleDownloadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default);
    ValueTask ThrottleUploadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default);
}
```

### 10.5 Transfer Session & Filter Policy Repository Ports (Atomic Aggregate Persistence)
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferSessionRepository
{
    ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default);
    ValueTask<TransferSession?> GetSessionByIdAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TransferSession>> GetActiveSessionsAsync(CancellationToken ct = default);
}

public interface IFileTransferFilterPolicyRepository
{
    ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default);
    ValueTask<FileTransferFilterPolicy> GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default);
}
```

### 10.6 CQRS Transfer Manager Ports
```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public sealed record ManifestFileReadModel(
    FileIndex Index,
    SanitizedRelativePath RelativePath,
    FileSizeBytes FileSize,
    BundleByteOffset StartOffset,
    BundleByteOffset EndOffset,
    FileLastModifiedUtc LastModifiedUtc);

public sealed record TransferSessionReadModel(
    TransferSessionId SessionId,
    RootBundleName RootName,
    TotalBundleBytes TotalBytes,
    TotalBundleBytes TransferredBytes,
    TotalBundleBytes UploadedBytes,
    SeedRatio CurrentSeedRatio,
    SeedRatio TargetSeedRatio,
    ChunkCount CompletedChunks,
    ChunkCount TotalChunks,
    TransferBandwidth DownloadSpeed,
    TransferBandwidth UploadSpeed,
    StreamCount ConnectedStreams,
    PeerCount ConnectedPeers,
    TransferSessionState Status,
    TransferRole Role,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public interface IFileTransferQueryService
{
    ValueTask<IReadOnlyList<TransferSessionReadModel>> GetAllTransfersAsync(CancellationToken ct = default);
    ValueTask<TransferSessionReadModel?> GetTransferByIdAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ManifestFileReadModel>> GetManifestFilesAsync(TransferSessionId sessionId, CancellationToken ct = default);
}

public interface IFileTransferActionHandler
{
    ValueTask<DomainResult> AcceptTransferAsync(TransferSessionId sessionId, RootDestinationPath destinationFolder, CancellationToken ct = default);
    ValueTask<DomainResult> AcceptTransferAsync(TransferSessionId sessionId, RootDestinationPath destinationFolder, SelectedFileIndices selectedFiles, CancellationToken ct = default);
    ValueTask<DomainResult> PauseTransferAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<DomainResult> ResumeTransferAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<DomainResult> StopSeedingAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<DomainResult> CancelTransferAsync(TransferSessionId sessionId, TransferTerminationReason reason, CancellationToken ct = default);
    ValueTask<DomainResult> SetSessionSpeedCapAsync(TransferSessionId sessionId, BandwidthLimit downloadCap, BandwidthLimit uploadCap, CancellationToken ct = default);
    ValueTask<DomainResult> SetSessionSeedRatioAsync(TransferSessionId sessionId, SeedRatio targetRatio, CancellationToken ct = default);
}
```

---

## 11. Threat Model & Privacy Invariants

| Threat / Vulnerability | Protocol Defense | Verification Mechanism |
| :--- | :--- | :--- |
| **Recipient Tracking / Probing** | Silent by default: Inbound transfer offers generate zero wire response or ACK unless explicitly opted in. | Unit test asserts zero outbound messages dispatched upon receiving offer. |
| **Parser / Deserialization Exploits** | Edge kill switch (`IAppIngressFilter`): When disabled, raw packets with `AppId = 0x03` are discarded before deserialization. | Unit test asserts parser is uninvoked when plugin is disabled. |
| **Directory Traversal** | Path sanitization rejecting `..`, absolute paths, null bytes, and Windows device names via `SanitizedRelativePath`. | Test suite fuzzes malicious path entries in manifests. |
| **In-Band Ratchet Bloat** | Control-only ratchet messages; bulk data runs strictly over out-of-band parallel streams. | Architecture enforces maximum ratchet offer size $\le 64\text{ KB}$. |
| **Disk Exhaustion** | Upfront disk space verification (`CheckDiskSpaceAsync`) before accepting. | Test asserts rejection if disk quota is exceeded. |
| **IP Address Leakage** | Default posture routes WAN transfers strictly via blind relay pipes; direct WAN endpoints (UPnP / Relay STUN) require explicit user opt-in (`WanPolicy == DirectWanPolicy.ExplicitlyAllowed`). | Test asserts WAN IP endpoints are never generated or dispatched unless opted in. |
| **Chunk Tampering / Substitution** | Dual cryptographic verification: AES-256-GCM authentication tag + SHA-256 leaf hash against Merkle root. | Test asserts corrupted chunk bytes are discarded and requested again. |
| **Relay Snooping** | E2EE per-chunk encryption ($K_{\text{transfer}}$); relay acts as blind socket proxy without keys. | Test verifies relay cannot decrypt chunks with zero knowledge of session key. |
| **Network Bufferbloat / Choking** | High-precision token bucket bandwidth throttle (`ITransferRateLimiter`). | Unit test asserts throughput throttles accurately to configured caps. |

---

## 12. High-Value, Low-Brittle Unit Testing Matrix

| Test Category | Target Feature / State | Verifies (Observable Behavior) | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **Ingress Control** | Plugin Disabled (`IsEnabled = false`) | Packet discarded with zero memory allocation or handler invocation | Asserts payload handler was uninvoked; zero parser exception. |
| **Ingress Control** | Peer / Relay Filter Blocked | Packet from blocked source dropped silently via `IAppIngressFilter` | Asserts no state change in transfer repository. |
| **Silent Recipient** | Inbound Offer Received | Inbound offer staged locally; zero outbound ACK transmitted | Asserts outbound sender queue length == 0. |
| **Manifest Tiering** | Manifest Size $\le 64\text{ KB}$ | Serialized manifest packed inline in offer envelope | Asserts `ManifestDistribution.Kind == Inline` and inline bytes populated. |
| **Manifest Tiering** | Manifest Size > 64 KB | Offloaded to `IBlobStorageService`; `ManifestBlobDescriptor` attached | Asserts blob store was called; `ManifestDistribution.Kind == RemoteBlob`. |
| **Boundary Math** | Chunk spanning across 2 files | Chunk data split accurately and written to both file offsets | Verifies bytes written to simulated disk matches expected slices. |
| **Selective Download** | Subset of files checked | Only chunks overlapping selected files are scheduled | Asserts unselected chunk indices are marked Ignored in bitfield. |
| **Merkle Verification** | Single bit flip in 1 MB chunk | Fails SHA-256 leaf check; chunk discarded | Asserts chunk status remains unverified and retry scheduled. |
| **Multi-Peer Push** | Multiple recipients with mixed routes | Sender issues separate offers across direct and relay routes | Asserts outbound dispatch calls match recipient route count. |
| **WAN Opt-In Gating** | Sender creates WAN transfer with opt-in disabled | Dispatches relay pipe endpoints only; zero UPnP/STUN candidates | Asserts `DirectCandidates` contains zero WAN IPs. |
| **Rarest-First Scheduling**| Peer bitfields declare rare chunk | Scheduler prioritizes requesting the rarest chunk index first | Asserts requested chunk sequence begins with the rarest index. |
| **Endgame Mode** | Last 3 chunks remaining in flight | Broadcasts duplicate requests across streams | Asserts request frame broadcast and subsequent cancel dispatch. |
| **Path Traversal Defense** | Manifest contains `../../etc/passwd` | Path validator throws validation failure and rejects file | Asserts rejection result with zero disk I/O attempted. |

---

## 13. Cross-Layer Infrastructure Alignment

| Application Port / Contract | Infrastructure Adapter / Concrete Artifact | Target Persistence / Transport |
| :--- | :--- | :--- |
| `ITransferStreamPool` | `TcpMultiStreamPool` | 4–8 multiplexed TCP/QUIC sockets (`TCP_NODELAY = true`) |
| `IDistributedTrackerPort` | Distributed Relay Swarm Tracker / PEX Client | Blind swarm rendezvous on relay or out-of-band PEX |
| `IDesktopFileStorage` | `PortableDesktopFileStore` | Staging with `.percolator-part`, pre-allocation, atomic rename |
| `ITransferSessionRepository` | `SqliteTransferSessionRepository` | `ft_transfer_sessions`, `ft_manifest_files`, `ft_swarm_members` |
| `IFileTransferFilterPolicyRepository` | `SqliteFilterPolicyRepository` | `ft_filter_policies`, `ft_blocked_peers`, `ft_blocked_relays` |
| `ITransferRateLimiter` | `TokenBucketRateLimiter` | High-precision token bucket bandwidth throttle |
| `IFileTransferQueryService` | `SqlFileTransferQueryService` | Fast-path Dapper/SQL projection for transfer dashboard |
| Ingress Filtering (`IAppIngressFilter`) | `InboundIngressPipeline` Edge Interceptor | Pre-dispatch drop before deserialization or app routing |

---

## 14. Phased Implementation Milestones (Post-Refinement Execution Roadmap)

### Milestone 1: Plugin Registration, Manifest Models, Value Objects & Ingress Edge Discard
1. Configure `Percolator.Apps.FileTransfer.csproj` with Roslyn incremental source generator analyzer reference to `Percolator.SourceGenerators`.
2. Implement `FileTransferPlugin` implementing `IAppPlugin` (`AppId.FileTransferControl = 0x03`).
3. Implement Value Objects & Enums via `Percolator.SourceGenerators` and explicit domain contracts:
   - `[GuidId(GuidIdKind.CryptographicRandom)]`: `TransferSessionId`
   - `[ByteArray(32)]`: `MerkleRoot`, `ChunkHash`, `TransferKey`, `BlindSwarmId`, `StreamAuthToken`, `RelayPipeToken`
   - `[ByteArray(16)]`: `EphemeralSwarmMemberToken`
   - `[ByteArray(12)]`: `BaseNonce`
   - `[ByteArray(1..65536)]`: `InlineManifestData`
   - Enums: `TransferRole`, `TransferSessionState`, `RoutePriority`, `DirectWanPolicy`, `TransportRoutingPreference`, `SwarmPeerRole`, `PeerChokeStatus`, `PeerInterestStatus`, `DirectCandidateKind`, `StreamFrameType`, `ManifestTierKind`, `RelayTransferCapabilityFlags`.
   - Invariant-enforced records & structs: `ChunkByteLength`, `EncryptedChunkData`, `PlaintextChunkData`, `DiskSpacePreflightResult`, `ManifestBlobDescriptor`, `ManifestDistribution`, `MerkleTreeIndex`, `FileLastModifiedUtc`, `RootBundleName`, `RelayPipeEndpointUri`, `RelayRendezvousDescriptor`, `RelayFileTransferCapabilities`, `PipeConcurrencyLimit`, `BandwidthLimit`, `TransferTerminationReason`, `TotalBundleBytes`, `FileSizeBytes`, `BundleByteOffset`, `FileByteOffset`, `FileCount`, `ChunkCount`, `ChunkIndex`, `ChunkSizeBytes`, `FileIndex`, `StreamCount`, `PeerCount`, `TransferByteLength`, `SanitizedRelativePath`, `DirectEndpointCandidate`, `EndpointHost`, `PortNumber`, `TransferBandwidth`, `SeedRatio`, `Bitfield` (immutable, `WithChunk`), `SelectedFileIndices`, `StreamIndex`, `RootDestinationPath`, `ResolvedDestinationFilePath`.
   - Domain events implementing `IDomainEvent`: `TransferOfferedDomainEvent`, `TransferAcceptedDomainEvent`, `TransferStreamingStartedDomainEvent`, `SwarmMemberJoinedDomainEvent`, `ChunkVerifiedDomainEvent`, `FileCompletedDomainEvent`, `TransferCompletedDomainEvent`, `TransferPausedDomainEvent`, `TransferResumedDomainEvent`, `TransferStalledDomainEvent`, `TransferFailedDomainEvent`, `TransferCancelledDomainEvent`.
   - Typed frame payloads: `IStreamFramePayload`, `HandshakeFrame`, `BitfieldFrame`, `HaveFrame`, `RequestChunkFrame`, `ChunkDataFrame`, `CancelChunkFrame`, `ChokeFrame`, `UnchokeFrame`, `KeepAliveFrame`, `PexPeersFrame`, `StreamFrameEnvelope`.
   - Strictly verify **zero implicit/explicit cast operators** across all Value Objects.
4. Implement Domain Aggregates & Entities:
   - `TransferSession : AggregateRoot<TransferSessionId>` encapsulating participants (`InitiatorId`, `RemotePeerId`, `TargetRecipients`), swarm members (`SwarmMember`), security policy (`WanPolicy`), piece verification (`VerifyAndCommitChunk`), and session secrets (`SessionKey`, `BaseNonce`, `StreamAuthToken`, `RelayPipeToken`).
   - `SwarmMember : IEntity<PublicIdentityId>` managing per-peer bitfields, choke status, and interest status.
   - `FileTransferFilterPolicy : AggregateRoot<PublicIdentityId>` managing peer and relay blocklists.
   - `TransferManifest : IEntity<TransferSessionId>` encapsulating `MerkleTreeIndex` and invariant-enforced `Create(...)` factory.
   - `FileEntry : IEntity<FileIndex>` encapsulating `FileLastModifiedUtc` and `EndOffset`.
5. Implement `MerkleTreeBuilder` (SHA-256 domain-separated hashing producing `MerkleTreeIndex`).
6. Implement two-tier manifest distribution (`ManifestDistribution.FromInline` vs. `ManifestDistribution.FromBlob`).
7. Implement in-band payloads: `FileTransferOfferPayload`, `FileTransferAcceptPayload`, `FileTransferCancelPayload`, `FileTransferRejectPayload`.
8. Implement `FileTransferIngressFilter` and `FileTransferFilterPolicy` implementing `IAppIngressFilter` (`PluginSdk`) for ingress pre-filtering before deserialization.
9. Implement `FileTransferPayloadHandler` for ingress processing.

### Milestone 2: Multi-Stream Coordinator & Out-of-Band Engine
1. Define ports: `ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`.
2. Implement `TransferSessionCoordinator` managing active 4–8 streams.
3. Implement out-of-band typed frame dispatching via `IStreamFramePayload` and `StreamFrameEnvelope`.
4. Implement concurrent chunk request pipeline (keeping 4–8 requests in flight per stream).
5. Implement opt-in direct candidate discovery adapter hooks (local UPnP port forwarder and relay-assisted STUN reflexive address resolver).

### Milestone 3: Desktop Disk I/O, Sparse Allocation & Selective Downloading
1. Define `IDesktopFileStorage` port using `PlaintextChunkData`, `FileLastModifiedUtc`, `RootDestinationPath`, `ResolvedDestinationFilePath`, and `DiskSpacePreflightResult`.
2. Implement `DiskChunkAssembler` handling multi-file boundary spanning, chunk writes, and atomic finalization with domain arithmetic (`BundleByteOffset`, `FileByteOffset`, `ChunkByteLength`).
3. Implement selective file downloading chunk range mapping (`SelectedFileIndices`).
4. Implement `BitfieldScanner` for resumption of partially completed downloads.

### Milestone 4: Swarm Coordination, Distributed Tracker & Seeding Ratios
1. Implement `SwarmCoordinator` updating `SwarmMember` entities within `TransferSession`.
2. Implement `RarestFirstChunkSelector` and `EndgameMode`.
3. Implement `IDistributedTrackerPort` and `PEX_PEERS` out-of-band frame handling using `SwarmPeerRole` and `EphemeralSwarmMemberToken`.
4. Implement seeding ratio tracking ($\text{Uploaded} / \text{Total}$) and automatic disconnect on target ratio.

### Milestone 5: CQRS Transfer Manager Ports & Rate Limiter
1. Implement rate limiter adapter integration with `ITransferRateLimiter` using `BandwidthLimit`.
2. Implement `IFileTransferFilterPolicyRepository` port for filter policy persistence taking `ownerId`.
3. Implement `IFileTransferQueryService` and `IFileTransferActionHandler` returning `ManifestFileReadModel` and `ValueTask<DomainResult>`.
4. Expose state machine transitions and event streams for presentation consumers.

### Milestone 6: Infrastructure Adapter Integration & End-to-End Verification
1. Wire `FileTransferPlugin` with `TcpMultiStreamPool` and `PortableDesktopFileStore` from `Percolator.Infrastructure`.
2. Test local LAN P2P multi-stream gigabit transfer.
3. Test opt-in WAN direct connections (simulated UPnP and relay STUN reflection).
4. Test blind relay transfer pipe streaming with simulated latency and packet drop.
5. Test swarm multi-peer piece sharing with 3+ desktop nodes.
