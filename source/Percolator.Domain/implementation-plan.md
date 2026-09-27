# Percolator.Domain Implementation Plan

## 1. Architectural Principles & Boundaries

### 1.1 Strict Onion Architecture Invariants
- **Innermost Core Isolation:** `Percolator.Domain` has zero references to EF Core, gRPC, Protobuf, SQLite, or networking sockets.
- **Zero Reference to `Percolator.Contracts`:** Wire serialization and Protobuf definitions are strictly forbidden in `Percolator.Domain`. The domain communicates solely via strongly typed Domain Entities and Value Objects.
- **Port Isolation Across Contexts:** Bounded contexts interact across boundaries strictly via value identifiers (`PublicIdentityId`, `ConversationId`, `BlindedRoutingToken`) or explicit domain port interfaces. Direct object graph traversal across contexts is prohibited.
- **Strict Temporal Determinism:** Direct calls to `DateTime.UtcNow` or `DateTimeOffset.UtcNow` are prohibited. Time is accessed exclusively through `IDateTimeProvider`. All timestamps are stored and manipulated as `DateTimeOffset` in UTC.
- **Allocation-Conscious Hot Paths:** Scalar IDs are `readonly record struct`. Wire ciphertexts pass as `ReadOnlyMemory<byte>` slices to avoid defensive array copies.
- **Secret Hygiene:** Any domain object retaining cryptographic keys, root keys, or chain keys implements `ISensitiveSecret : IDisposable`, utilizing `CryptographicOperations.ZeroMemory` to wipe key material upon disposal or ratchet progression.
- **Code-Generating Attributes:** Cryptographic byte arrays are generated via `[ByteArray]` and strongly typed GUID identifiers via `[GuidId(GuidIdKind)]` source generators (`Percolator.SourceGenerators`) to eliminate boilerplate, guarantee value equality, and preserve zero-allocation struct layout.

---

## 2. Decided Domain Specifications & Cryptographic Security Hardening

1. **1:1 vs. Group Delivery Modalities:**
   - **1:1 Relayed Messaging:** Senders drop off Sealed Sender envelopes addressed to the recipient's `PublicIdentityId`. The relay knows the recipient but has zero knowledge of the sender. The recipient drains envelopes over an **authenticated gRPC bidirectional stream** (`ConnectRelayStream`).
   - **Group Messaging:** Senders submit anonymous unary requests verified exclusively by `ZkPresentationBytes` against `RelayGroupLedger.CurrentEpoch`. Group recipients receive messages over an **anonymous stream** bound to ephemeral `BlindedRoutingToken`s. The relay learns neither the sender nor recipient identity.
2. **Multi-Device Cryptographic Hierarchy & Anti-Phantom Injection:**
   - Master account identity is established by the primary device (`DeviceId = 1`) holding `IdentityKeyring`.\n   - Secondary devices (`DeviceId > 1`) generate their own identity keys and must present a `DeviceLinkProof` signed by the primary identity key. Both `DeviceRecord.CreateSecondary` and `PeerContact.RegisterSecondaryDevice` cryptographically verify this signature to prevent phantom device injection.
   - Double Ratchet sessions are strictly **pairwise per physical device**: `(OwnerIdentityId, OwnerDeviceId) <-> (PeerIdentityId, PeerDeviceId)`.
3. **ZK Proof Transcript Binding (Anti-Replay / Anti-Hijacking):**
   - In accordance with Signal Group V2 algebraic MAC / Fiat-Shamir credential schemes, `IZkProofEngine.VerifyGroupPresentation` binds a 32-byte transcript challenge:
     - Group Mutations bind `SHA256(newBlob || sorted(newTokens))`.\n     - Message Dispatches bind `SHA256(envelopeCiphertext)`.
   - Replaying a valid presentation proof against a modified roster or an unauthorized message fails cryptographic verification.
4. **Post-Compromise Security (Asymmetric DH Ratchet Turn):**
   - `DirectRatchetSession` implements the full Signal Double Ratchet turn (`StepDhRatchet`), deriving fresh root keys and sending/receiving chains from new remote ephemeral public keys via `KdfRk` and ECDH.
5. **Memory Hygiene & Upper Bounds (LRU Eviction):**
   - `MessageKey`, `ChainKey`, and `SharedSecret` implement `ISensitiveSecret : IDisposable` to zeroize managed buffers in-place.
   - `DirectRatchetSession` enforces `MaxTotalSkippedKeys = 1000` with LRU eviction and zeroization of evicted message keys.
   - `RelayGroupLedger` enforces `MaxGroupMembers = 1000`.
   - `PeerContact` enforces `MaxRegisteredDevicesPerPeer = 32`.
6. **Denial-of-Service Defense & Delivery Tokens (Signal UDC):**
   - Mailboxes require an authorized `DeliveryToken` (Signal Unidentified Delivery Credential model) on `Enqueue` to eliminate anonymous flooding.
   - `RelayMailboxQueue` enforces `PurgePolicy.MaxRetainedEnvelopes` alongside automatic TTL expirations and discretionary pruning.
7. **Strongly Typed GUID Classification (`GuidIdKind`):**
   - `CryptographicRandom`: Security-sensitive identifiers (`PublicIdentityId`, `ConversationId`, `BlindedRoutingToken`) generated from CSPRNG without leaking timestamps.
   - `SequentialTimeBased`: Time-ordered identifiers (`MessageId`) generated via UUIDv7 (`Guid.CreateVersion7()`), optimizing B-Tree index cache locality and eliminating page splits in SQLite.
8. **Zero-Leakage Inactivity ("Black Hole"):**
   - Disabling an identity transitions `IdentityState` to `Disabled`, zeroizes volatile in-memory ratchet secrets, and emits `IdentityDisabledEvent`. Outer layers sever relay streams immediately and silently drop incoming packets without emitting protocol errors.
9. **Allocation-Free Result Pattern:**
   - All expected domain rule rejections return `DomainResult` or `DomainResult<T>` (`readonly record struct`). Exceptions are reserved strictly for non-recoverable system corruptions.

---

## 3. Step-by-Step TDD Implementation Plan

### Milestone 1: Core Primitives, Test Doubles & Result Types
- Production: `IEntity<TId>`, `IDomainEvent`, `IAggregateRoot<TId>`, `AggregateRoot<TId>`, `IDateTimeProvider`, `ISensitiveSecret`, `DomainError`, `DomainResult`, `DomainResult<T>`.
- Test Double: `FakeDateTimeProvider`.
- Tests: `DomainResultTests`, `FakeDateTimeProviderTests`.

### Milestone 2: Identities Bounded Context (Multi-Tenancy, Personas & Devices)
- Production: `PublicIdentityId` (`[GuidId(CryptographicRandom)]`), `DeviceId`, `IdentityRole`, `IdentityState`, `DeviceLinkProof`, `IdentityPublicKey`, `IdentityProfile`, `DeviceRecord` (with signature verification), `PeerContact` (with phantom device defense and cap), `PreKeyBundleState`, repository ports (`IIdentityProfileRepository`, `IPeerContactRepository`, `IPreKeyStore`).
- Events: `IdentityCreatedEvent`, `IdentityEnabledEvent`, `IdentityDisabledEvent`.
- Tests: `IdentityProfileTests`, `DeviceRecordTests`, `PeerContactTests`.

### Milestone 3: Security Bounded Context (E2EE Ratchets & ZK Credentials)
- Production: `ChainKey`, `MessageKey`, `SharedSecret` (all implementing `ISensitiveSecret`), `ZkPresentationBytes`, `AuthCredentialMacBytes`, `ZkGroupPublicParams`, ports (`ICryptoEngine`, `IZkProofEngine`).
- Models: `DirectRatchetSession` (Double Ratchet with `StepDhRatchet` and LRU eviction of skipped keys), `GroupSenderKeyRatchet`.
- Test Doubles: `DeterministicCryptoEngine`, `FakeZkProofEngine`.
- Tests: `DirectRatchetSessionTests`, `GroupSenderKeyRatchetTests`.

### Milestone 4: Conversations Bounded Context (Semantic Chat & Group Invariants)
- Production: `ConversationId` (`[GuidId(CryptographicRandom)]`), `MessageId` (`[GuidId(SequentialTimeBased)]`), `EpochNumber`, `GroupRole`, `Message`, `GroupMember`, `DirectConversation`, `GroupConversation`, `IConversationRepository`.
- Events: `MessageAppendedEvent`, `GroupEpochAdvancedEvent`, `MemberJoinedEvent`, `MemberRemovedEvent`.
- Tests: `GroupConversationTests`, `DirectConversationTests`.

### Milestone 5: Delivery Bounded Context (Routing, Outbox & Relay Hosting)
- Production: `BlindedRoutingToken` (`[GuidId(CryptographicRandom)]`), `DeliveryRoute`, `OutboxStatus`, `EncryptedEntriesBlob`, `MailboxEnvelope`, `PurgePolicy`, `DeliveryToken`.
- Models: `OutboxJob`, `RelayGroupLedger` (with Fiat-Shamir transcript binding and group capacity caps), `RelayMailboxQueue` (with `DeliveryToken` verification and quota enforcement).
- Ports: `IOutboxRepository`, `IRelayLedgerRepository`, `IRelayMailboxRepository`.
- Events: `OutboxJobEnqueuedEvent`, `OutboxJobPausedEvent`, `OutboxJobDeliveredEvent`, `EnvelopeBufferedEvent`, `EpochCommittedEvent`.
- Tests: `OutboxJobTests`, `RelayGroupLedgerTests`, `RelayMailboxQueueTests`, `GuidIdTests`.
