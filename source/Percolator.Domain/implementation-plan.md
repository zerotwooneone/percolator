# Percolator.Domain Implementation Plan

## 1. Architectural Principles & Core Constraints

### 1.1 Pure Onion Architecture / Core Domain Rules
- **Zero External Infrastructure/Transport Dependencies:** `Percolator.Domain` has zero references to EF Core, gRPC, Protobuf, SQLite, or networking sockets.
- **Strict Rule – No `Percolator.Contracts` Reference:** Under no circumstances may `Percolator.Domain` reference `Percolator.Contracts` or generated Protobuf classes. Wire serialization models belong strictly to the Infrastructure layer. The domain models business invariants and cryptographic state using strongly typed domain primitives.
- **Strict Rule – Context Isolation via Port Boundaries:** Bounded contexts within `Percolator.Domain` communicate across context borders strictly via value IDs (`PublicIdentityId`, `ConversationId`, `RoutingToken`) or through explicit domain ports (interfaces). Direct object-graph coupling across bounded contexts is prohibited.
- **Strict Rule – Time and Determinism:** The domain never references `DateTime.UtcNow` or `DateTimeOffset.UtcNow` directly. All domain components consume the domain port `IDateTimeProvider`. All persistent and transient timestamps are strictly `DateTimeOffset` in UTC.
- **Strict Rule – Allocation-Conscious Hot Paths:** Relays process high-throughput message envelopes and fan-outs. Identifiers are `readonly record struct`, envelope structures avoid unnecessary allocations, and ciphertext buffers pass as `ReadOnlyMemory<byte>` rather than copied arrays.
- **Code-Generating Attributes over Inherited Type Bloat:** Avoid large abstract base classes with reflection-based equality. Value objects use `readonly record struct` or the `[ByteArray]` source generator attribute (via `Percolator.SourceGenerators`) for fixed-size and constrained byte arrays with built-in constant-time equality.

---

## 2. Ubiquitous Language & Core Terminology

| Term | Scope | Definition |
| :--- | :--- | :--- |
| **`PublicIdentityId`** | Universal | Canonical, wire-safe GUID representing an Account UUID. Used across direct messaging, contact directories, and device bindings. |
| **`DeviceId`** | Identity / Security | Strongly typed 32-bit unsigned integer (`uint`) identifying a physical device instance under a specific `PublicIdentityId`. |
| **`IdentityRole`** | Identity | Enum defining whether an identity operates as a `UserPersona` (Work, Personal) or an autonomous `RelayHost`. |
| **`IdentityState`** | Identity | Lifecycle state: `Active`, `Disabled`, or `Suspended`. Governs zero-leakage dormancy. |
| **`BlindedRoutingToken`**| Delivery / Relay | Ephemeral 16-byte/Guid token representing a blinded mailbox or stream route on a relay. Never linkable by the relay to a `PublicIdentityId`. |
| **`EpochNumber`** | Conversations / Relay | Strictly monotonic 32-bit unsigned integer tracking Signal Group V2 state transitions. |
| **`ZkPresentationBytes`**| Security / Delivery | Zero-Knowledge proof token proving group membership authorization to a relay without disclosing the sender's identity. |

---

## 3. High-Level Bounded Context Architecture

```
Percolator.Domain/
│
├── Common/                          <-- Core DDD Primitives, Result types & System Ports
│   ├── IEntity.cs
│   ├── IAggregateRoot.cs
│   ├── IDomainEvent.cs
│   ├── IDateTimeProvider.cs
│   └── DomainResult.cs
│
├── Identities/                      <-- Bounded Context 1: Multi-Tenancy, Personas & Devices
│   ├── Model/                       // IdentityProfile (Aggregate), DeviceRecord, PeerContact, PreKeyBundleState
│   ├── ValueObjects/                // PublicIdentityId, DeviceId, IdentityRole, IdentityState
│   ├── Events/                      // IdentityCreatedEvent, IdentityEnabledEvent, IdentityDisabledEvent
│   └── Ports/                       // IIdentityProfileRepository, IPeerContactRepository
│
├── Security/                        <-- Bounded Context 2: E2EE Ratchets, Keys & ZK Credentials
│   ├── Model/                       // DirectRatchetSession (Aggregate), GroupSenderKeyRatchet (Aggregate)
│   ├── ValueObjects/                // ChainKey, MessageKey, ZkPresentationBytes, AuthCredentialMacBytes
│   ├── Events/                      // RatchetAdvancedEvent, SenderKeyRotatedEvent
│   └── Ports/                       // ICryptoEngine (Pure math), IZkProofEngine (ZK validation)
│
├── Conversations/                   <-- Bounded Context 3: Semantic Messaging & Group Invariants
│   ├── Model/                       // DirectConversation (Aggregate), GroupConversation (Aggregate), Message
│   ├── ValueObjects/                // ConversationId, MessageId, GroupRole, EpochNumber
│   ├── Events/                      // MessageAppendedEvent, GroupEpochAdvancedEvent, MemberJoinedEvent
│   └── Ports/                       // IConversationRepository
│
└── Delivery/                        <-- Bounded Context 4: Routing, Client Outbox & Relay Hosting
    ├── Client/                      // OutboxJob (Aggregate), DeliveryRoute, RoutingPreference
    ├── Hosting/                     // RelayGroupLedger (Aggregate), RelayMailboxQueue (Aggregate), MailboxEnvelope
    ├── ValueObjects/                // BlindedRoutingToken, OutboxStatus, PurgePolicy
    ├── Events/                      // OutboxJobEnqueuedEvent, EnvelopeBufferedEvent, EpochCommittedEvent
    └── Ports/                       // IOutboxRepository, IRelayLedgerRepository, IRelayMailboxRepository
```

---

## 4. Phase-by-Phase Technical Specification

### Phase 1: Common Primitives & Domain Infrastructure (`Common`)

#### 1.1 `IEntity<TId>` & `IAggregateRoot<TId>`
- `IEntity<TId>`: Defines `TId Id { get; }` where `TId : IEquatable<TId>`.
- `IAggregateRoot<TId>`: Implements `IEntity<TId>`, exposes `IReadOnlyList<IDomainEvent> DomainEvents`, and provides `AddDomainEvent(IDomainEvent evt)` and `ClearDomainEvents()`.
- Implementation: Lightweight abstract base class `AggregateRoot<TId>` containing an internal `List<IDomainEvent> _domainEvents`.

#### 1.2 `IDomainEvent`
- Pure domain event interface:
  ```csharp
  public interface IDomainEvent
  {
      Guid EventId { get; }
      DateTimeOffset OccurredOnUtc { get; }
  }
  ```

#### 1.3 `IDateTimeProvider`
- Defines the temporal abstraction for the domain:
  ```csharp
  public interface IDateTimeProvider
  {
      DateTimeOffset UtcNow { get; }
  }
  ```

#### 1.4 `DomainResult` & `DomainResult<T>`
- Allocation-free `readonly record struct` representing operation success or failure with a typed domain error code, eliminating exception-driven flow control on hot domain validation paths.

---

### Phase 2: Identities Bounded Context (`Identities`)

Supports multiple active or dormant personas (Work, Friends, Family) and autonomous self-hosted relay identities inside a single SQLite database.

#### 2.1 Value Objects
- **`PublicIdentityId`**: `readonly record struct PublicIdentityId(Guid Value)` – Canonical Account UUID.
- **`DeviceId`**: `readonly record struct DeviceId(uint Value)` – Device number (e.g., 1 for primary, 2..N for linked devices).
- **`IdentityRole`**: Enum:
  - `UserPersona`: Standard communication persona.
  - `RelayHost`: Autonomous relay daemon with its own keys and storage partition.
- **`IdentityState`**: Enum:
  - `Active`: Normal operation.
  - `Disabled`: Dormant/Black-hole state. Outbox frozen; streaming severed; zero responses.
  - `Suspended`: Administrative lock or corruption.
- **`IdentityPublicKey`**: Generated via `[ByteArray(32)] public sealed partial record IdentityPublicKey;` (Ed25519/Curve25519 public key).

#### 2.2 Aggregates & Entities
- **`IdentityProfile` (Aggregate Root)**:
  - Properties: `PublicIdentityId Id`, `string DisplayName`, `IdentityRole Role`, `IdentityState State`, `DateTimeOffset CreatedAtUtc`, `DateTimeOffset? DisabledAtUtc`.
  - Invariants:
    - Calling `Disable(IDateTimeProvider)` transitions state to `Disabled`, sets `DisabledAtUtc`, and emits `IdentityDisabledEvent(Id, DisabledAtUtc)`.
    - Calling `Enable(IDateTimeProvider)` transitions state to `Active`, clears `DisabledAtUtc`, and emits `IdentityEnabledEvent(Id)`.
    - Cannot transition from `Suspended` without explicit administrative action.
- **`DeviceRecord` (Entity)**:
  - Properties: `DeviceId Id`, `string DeviceName`, `DateTimeOffset RegisteredAtUtc`, `DateTimeOffset LastSeenAtUtc`.
- **`PeerContact` (Aggregate Root)**:
  - Scoped by owner: Composite identifier or `PublicIdentityId OwnerIdentityId` + `PublicIdentityId RemotePeerId`.
  - Properties: Nickname, trust status (Untrusted, TOFU, Verified, Blocked), known public keys, set of known registered `DeviceId`s.
- **`PreKeyBundleState` (Aggregate Root)**:
  - Scoped by `(OwnerIdentityId, DeviceId)`. Tracks available one-time pre-keys, current signed pre-key, and expiration timestamps.

#### 2.3 Ports
- `IIdentityProfileRepository`: Query and mutate identities.
- `IPeerContactRepository`: Query and persist contacts per `OwnerIdentityId`.
- `IPreKeyStore`: Inventory management of ephemeral pre-keys.

---

### Phase 3: Cryptography & Security Bounded Context (`Security`)

Encapsulates stateful E2EE ratchets and zero-knowledge presentation tokens. Mathematical computations are delegated to pure engine ports.

#### 3.1 Value Objects
- **`ChainKey`**: `[ByteArray(32)] public sealed partial record ChainKey;`
- **`MessageKey`**: `[ByteArray(32)] public sealed partial record MessageKey;`
- **`SharedSecret`**: `[ByteArray(32)] public sealed partial record SharedSecret;`
- **`ZkPresentationBytes`**: `[ByteArray(minLength: 1, maxLength: 1000)] public sealed partial record ZkPresentationBytes;`
- **`AuthCredentialMacBytes`**: `[ByteArray(minLength: 1, maxLength: 1000)] public sealed partial record AuthCredentialMacBytes;`

#### 3.2 Aggregates
- **`DirectRatchetSession` (Aggregate Root)**:
  - Partitioned by: `(OwnerIdentityId, OwnerDeviceId, RemotePeerId, RemoteDeviceId)`.
  - Enforces Double Ratchet invariants:
    - Root key, sending chain key, receiving chain key.
    - Sending counter, receiving counter, previous chain counter.
    - Skipped message key cache with max threshold to prevent DoS.
    - Forward ratchet step updates root key and zeroes ephemeral chain steps.
- **`GroupSenderKeyRatchet` (Aggregate Root)**:
  - Partitioned by: `(OwnerIdentityId, ConversationId, AuthorPublicIdentityId, AuthorDeviceId)`.
  - Tracks the cryptographic ratchet for Signal Group V2 sender keys:
    - Current sender key iteration.
    - Chain key progression.
    - Monotonic step enforcement (cannot step backwards).
- **`UnknownMessageTank` (Aggregate Root)**:
  - Caches out-of-order ciphertexts awaiting delayed pre-key or ratchet progression. Enforces maximum buffer size and TTL purging.

#### 3.3 Ports (Pure Computation Interfaces)
- **`ICryptoEngine`**:
  ```csharp
  public interface ICryptoEngine
  {
      (ChainKey NextChainKey, MessageKey MessageKey) StepRatchet(ChainKey currentChainKey);
      SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey);
      byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);
      byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData);
  }
  ```
- **`IZkProofEngine`**:
  ```csharp
  public interface IZkProofEngine
  {
      bool VerifyGroupPresentation(EpochNumber epoch, ZkPresentationBytes presentation, ReadOnlySpan<byte> publicParams);
  }
  ```

---

### Phase 4: Conversations Bounded Context (`Conversations`)

Models user-facing chat interactions, membership invariants, and epoch mutations.

#### 4.1 Value Objects
- **`ConversationId`**: `readonly record struct ConversationId(Guid Value);`
- **`MessageId`**: `readonly record struct MessageId(Guid Value);`
- **`EpochNumber`**: `readonly record struct EpochNumber(uint Value)` with monotonic helper `Next() => new(Value + 1);`.
- **`GroupRole`**: Enum `Member`, `Admin`.

#### 4.2 Aggregates & Entities
- **`DirectConversation` (Aggregate Root)**:
  - Properties: `ConversationId Id`, `PublicIdentityId OwnerIdentityId`, `PublicIdentityId RemotePeerId`, `DateTimeOffset CreatedAtUtc`, `MessageId? LastReadMessageId`.
- **`GroupConversation` (Aggregate Root)**:
  - Properties: `ConversationId Id`, `PublicIdentityId OwnerIdentityId`, `string Title`, `EpochNumber CurrentEpoch`, `IReadOnlyList<GroupMember> Members`.
  - Invariants:
    - Standard chat messages **never** advance the group epoch.
    - Only users with `GroupRole.Admin` can invoke `AddMember`, `RemoveMember`, or `ChangeTitle`.
    - Roster mutations advance `CurrentEpoch` monotonically and emit `GroupEpochAdvancedEvent`.
- **`Message` (Entity)**:
  - Properties: `MessageId Id`, `PublicIdentityId SenderId`, `DeviceId SenderDeviceId`, `DateTimeOffset TimestampUtc`, `ReadOnlyMemory<byte> EncryptedPayload`, `DeliveryStatus Status`.

#### 4.3 Ports
- `IConversationRepository`: Storage and querying of 1:1 and group conversations partitioned by `OwnerIdentityId`.

---

### Phase 5: Delivery Bounded Context (`Delivery`)

Segregates client outbox routing from the self-hosted blind relay fabric.

#### 5.1 Value Objects
- **`BlindedRoutingToken`**: `readonly record struct BlindedRoutingToken(Guid Value);`
- **`DeliveryRoute`**: `readonly record struct DeliveryRoute(RouteType Type, Uri? DirectEndpoint, BlindedRoutingToken? TargetToken);`
- **`OutboxStatus`**: Enum `Pending`, `InFlight`, `Delivered`, `Failed`, `PausedDormant`.
- **`MailboxEnvelope`**:
  ```csharp
  public readonly record struct MailboxEnvelope(
      Guid AckId,
      BlindedRoutingToken RecipientToken,
      ReadOnlyMemory<byte> Ciphertext,
      DateTimeOffset EnqueuedAtUtc,
      DateTimeOffset ExpiresAtUtc);
  ```

#### 5.2 Sub-Context A: Client Outbox (`Delivery.Client`)
- **`OutboxJob` (Aggregate Root)**:
  - Partitioned by: `(OwnerIdentityId, JobId)`.
  - Invariants:
    - If owning `IdentityState == Disabled`, job status is forced to `PausedDormant` and cannot be processed until re-enabled.
    - Tracks exponential backoff retries without blocking threads.

#### 5.3 Sub-Context B: Hosted Relay Fabric (`Delivery.Hosting`)
Operates as an untrusted, blind intermediary. **Zero knowledge of conversations, user identities, or chat text.**
- **`RelayGroupLedger` (Aggregate Root)**:
  - Properties: `ConversationId GroupId`, `EpochNumber CurrentEpoch`, `EncryptedEntriesBlob CurrentRosterBlob`, `IReadOnlySet<BlindedRoutingToken> ActiveMemberTokens`.
  - Invariants:
    - Atomically replaces the blinded roster upon valid admin mutation.
    - Validates `ZkPresentationBytes` against `CurrentEpoch` before fan-out.
    - Rejects mutations whose base epoch does not match `CurrentEpoch` (forces client speculative rebase).
- **`RelayMailboxQueue` (Aggregate Root)**:
  - Storage for offline peer 1:1 envelopes.
  - Invariants:
    - Enforces TTL: Envelopes past `ExpiresAtUtc` are marked expired.
    - Enforces Discretionary Purge: When partition exceeds storage quota, oldest non-acknowledged envelopes are pruned.

#### 5.4 Ports
- `IOutboxRepository`: Client outbox queue operations.
- `IRelayLedgerRepository`: Relay group ledger persistence.
- `IRelayMailboxRepository`: Relay store-and-forward mailbox persistence.

---

## 6. Implementation Sequence & Milestones

```
Milestone 1: Domain Primitives & Common Infrastructure
├── 1.1 Create Percolator.Domain project structure
├── 1.2 Implement Common/ (IEntity, IAggregateRoot, IDomainEvent, IDateTimeProvider, DomainResult)
└── 1.3 Verify build with zero external package dependencies

Milestone 2: Identities & Multi-Tenancy
├── 2.1 Implement ValueObjects (PublicIdentityId, DeviceId, IdentityRole, IdentityState)
├── 2.2 Implement IdentityProfile Aggregate with Enable/Disable lifecycle & events
├── 2.3 Implement PeerContact & PreKeyBundleState
└── 2.4 Define IIdentityProfileRepository & IPeerContactRepository ports

Milestone 3: Cryptography & Security
├── 3.1 Generate binary value objects via [ByteArray] attribute
├── 3.2 Define pure computation ports (ICryptoEngine, IZkProofEngine)
├── 3.3 Implement DirectRatchetSession aggregate (Double Ratchet invariants)
└── 3.4 Implement GroupSenderKeyRatchet aggregate

Milestone 4: Conversations & Semantic Messaging
├── 4.1 Implement ConversationId, MessageId, EpochNumber
├── 4.2 Implement DirectConversation aggregate
├── 4.3 Implement GroupConversation aggregate with role/epoch mutation rules
└── 4.4 Define IConversationRepository port

Milestone 5: Delivery & Blind Relay Hosting
├── 5.1 Implement Client OutboxJob with dormancy state guards
├── 5.2 Implement RelayGroupLedger with atomic blinded roster replacement
├── 5.3 Implement RelayMailboxQueue with TTL & discretionary purge policies
└── 5.4 Define IOutboxRepository, IRelayLedgerRepository, IMailboxRepository ports

Milestone 6: Domain Unit Testing Suite (Percolator.Domain.Tests)
├── 6.1 Identities: Verify dormancy state machine and zero-leakage event emissions
├── 6.2 Security: Verify pairwise multi-device ratchet stepping and key skipping
├── 6.3 Conversations: Verify admin-only epoch advancement and invariant enforcement
└── 6.4 Delivery: Verify relay roster swap, ZK presentation gates, and mailbox pruning
```
