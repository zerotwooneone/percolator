# Percolator.Domain Implementation Plan

## 1. Architectural Principles & Boundaries

### 1.1 Strict Onion Architecture Invariants
- **Innermost Core Isolation:** `Percolator.Domain` has zero references to EF Core, gRPC, Protobuf, SQLite, or networking sockets.
- **Zero Reference to `Percolator.Contracts`:** Wire serialization and Protobuf definitions are strictly forbidden in `Percolator.Domain`. The domain communicates solely via strongly typed Domain Entities and Value Objects.
- **Port Isolation Across Contexts:** Bounded contexts interact across boundaries strictly via value identifiers (`PublicIdentityId`, `ConversationId`, `BlindedRoutingToken`) or explicit domain port interfaces. Direct object graph traversal across contexts is prohibited.
- **Strict Temporal Determinism:** Direct calls to `DateTime.UtcNow` or `DateTimeOffset.UtcNow` are prohibited. Time is accessed exclusively through `IDateTimeProvider`. All timestamps are stored and manipulated as `DateTimeOffset` in UTC.
- **Allocation-Conscious Hot Paths:** Scalar IDs are `readonly record struct`. Wire ciphertexts pass as `ReadOnlyMemory<byte>` slices to avoid defensive array copies.
- **Secret Hygiene:** Any domain object retaining cryptographic keys, root keys, or chain keys implements `ISensitiveSecret : IDisposable`, utilizing `CryptographicOperations.ZeroMemory` to wipe key material upon disposal or ratchet progression.
- **Code-Generating Attributes:** Cryptographic byte arrays are generated via `[ByteArray]` source generators (`Percolator.SourceGenerators`) to avoid deep inheritance hierarchies and reflection-based equality.

---

## 2. Decided Domain Specifications (No Unanswered Questions)

1. **1:1 vs. Group Delivery Modalities:**
   - **1:1 Relayed Messaging:** Senders drop off Sealed Sender envelopes addressed to the recipient's `PublicIdentityId`. The relay knows the recipient but has zero knowledge of the sender. The recipient drains envelopes over an **authenticated gRPC bidirectional stream** (`ConnectRelayStream`).
   - **Group Messaging:** Senders submit anonymous unary requests verified exclusively by `ZkPresentationBytes` against `RelayGroupLedger.CurrentEpoch`. Group recipients receive messages over an **anonymous stream** bound to ephemeral `BlindedRoutingToken`s. The relay learns neither the sender nor recipient identity.
2. **Multi-Device Cryptographic Hierarchy:**
   - Master account identity is established by the primary device (`DeviceId = 1`) holding `IdentityKeyring`.
   - Secondary devices (`DeviceId > 1`) generate their own identity keys and must present a `DeviceLinkProof` signed by the primary identity key.
   - Double Ratchet sessions are strictly **pairwise per physical device**: `(OwnerIdentityId, OwnerDeviceId) <-> (PeerIdentityId, PeerDeviceId)`.
3. **Group Genesis Invariants:**
   - Groups are provisioned via `RelayGroupLedger.CreateGenesis(...)` with `EpochNumber(0)`, an initial encrypted roster blob, and an initial set of member routing tokens. Subsequent mutations advance epochs monotonically via ZK presentation proofs.
4. **Zero-Leakage Inactivity ("Black Hole"):**
   - Disabling an identity transitions `IdentityState` to `Disabled`, zeroizes volatile in-memory ratchet secrets, and emits `IdentityDisabledEvent`. Outer layers sever relay streams immediately and silently drop incoming packets without emitting protocol errors.
5. **Allocation-Free Result Pattern:**
   - All expected domain rule rejections return `DomainResult` or `DomainResult<T>` (`readonly record struct`). Exceptions are reserved strictly for non-recoverable system corruptions.

---

## 3. Step-by-Step TDD Implementation Plan

### Milestone 1: Core Primitives, Test Doubles & Result Types

#### 1.1 Test Fixtures (RED)
- **Path:** `Percolator.Domain.Tests/Common/DomainResultTests.cs`
  - `Success_ReturnsExpectedValue_AndIsSuccessTrue()`
  - `Failure_CarriesErrorCodeAndDescription_AndIsFailureTrue()`
  - `ImplicitConversion_FromValue_CreatesSuccessResult()`
- **Path:** `Percolator.Domain.Tests/Common/FakeDateTimeProviderTests.cs`
  - `Advance_IncrementsVirtualTimeAccurately()`

#### 1.2 Production Implementation (GREEN)
- **Path:** `Percolator.Domain/Common/IEntity.cs`
  ```csharp
  namespace Percolator.Domain.Common;
  public interface IEntity<TId> where TId : IEquatable<TId> { TId Id { get; } }
  ```
- **Path:** `Percolator.Domain/Common/IDomainEvent.cs`
  ```csharp
  namespace Percolator.Domain.Common;
  public interface IDomainEvent
  {
      Guid EventId { get; }
      DateTimeOffset OccurredOnUtc { get; }
  }
  ```
- **Path:** `Percolator.Domain/Common/IAggregateRoot.cs` & `AggregateRoot.cs`
  ```csharp
  namespace Percolator.Domain.Common;
  public interface IAggregateRoot<TId> : IEntity<TId> where TId : IEquatable<TId>
  {
      IReadOnlyList<IDomainEvent> DomainEvents { get; }
      void ClearDomainEvents();
  }
  public abstract class AggregateRoot<TId> : IAggregateRoot<TId> where TId : IEquatable<TId>
  {
      public abstract TId Id { get; }
      private readonly List<IDomainEvent> _domainEvents = [];
      public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
      protected void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
      public void ClearDomainEvents() => _domainEvents.Clear();
  }
  ```
- **Path:** `Percolator.Domain/Common/IDateTimeProvider.cs`
  ```csharp
  namespace Percolator.Domain.Common;
  public interface IDateTimeProvider { DateTimeOffset UtcNow { get; } }
  ```
- **Path:** `Percolator.Domain/Common/ISensitiveSecret.cs`
  ```csharp
  namespace Percolator.Domain.Common;
  public interface ISensitiveSecret : IDisposable { void Zeroize(); }
  ```
- **Path:** `Percolator.Domain/Common/DomainError.cs` & `DomainResult.cs`
  ```csharp
  namespace Percolator.Domain.Common;
  public readonly record struct DomainError(string Code, string Description);
  public readonly record struct DomainResult
  {
      public bool IsSuccess { get; }
      public bool IsFailure => !IsSuccess;
      public DomainError Error { get; }
      public static DomainResult Success() => new(true, default);
      public static DomainResult Failure(DomainError error) => new(false, error);
  }
  public readonly record struct DomainResult<T>
  {
      public bool IsSuccess { get; }
      public bool IsFailure => !IsSuccess;
      public T Value { get; }
      public DomainError Error { get; }
      public static DomainResult<T> Success(T value) => new(true, value, default);
      public static DomainResult<T> Failure(DomainError error) => new(false, default!, error);
      public static implicit operator DomainResult<T>(T value) => Success(value);
  }
  ```
- **Path:** `Percolator.Domain.Tests/TestDoubles/FakeDateTimeProvider.cs`
  - In-memory time provider implementing `IDateTimeProvider` with `.Advance(TimeSpan)`.

#### 1.3 Verification Criteria
- Run `dotnet test Percolator.Domain.Tests\Percolator.Domain.Tests.csproj`. All tests pass.

---

### Milestone 2: Identities Bounded Context (Multi-Tenancy, Personas & Devices)

#### 2.1 Test Fixtures (RED)
- **Path:** `Percolator.Domain.Tests/Identities/IdentityProfileTests.cs`
  - `Create_WithValidParameters_ReturnsActiveProfile()`
  - `Disable_WhenActive_TransitionsToDisabled_AndEmitsIdentityDisabledEvent()`
  - `Disable_WhenAlreadyDisabled_IsIdempotent_AndEmitsNoDuplicateEvent()`
  - `Enable_WhenDisabled_TransitionsToActive_AndEmitsIdentityEnabledEvent()`
- **Path:** `Percolator.Domain.Tests/Identities/DeviceRecordTests.cs`
  - `CreatePrimaryDevice_HasDeviceIdOne_AndRequiresNoProof()`
  - `CreateSecondaryDevice_WithoutLinkProof_ReturnsValidationError()`
  - `CreateSecondaryDevice_WithLinkProof_Succeeds()`
- **Path:** `Percolator.Domain.Tests/Identities/PeerContactTests.cs`
  - `TrustPeer_UpdatesTrustLevel_AndRecordsTimestamp()`
  - `RegisterDevice_AddsNewDeviceIdToPeerRecord()`

#### 2.2 Production Implementation (GREEN)
- **Path:** `Percolator.Domain/Identities/ValueObjects/PublicIdentityId.cs`
  ```csharp
  namespace Percolator.Domain.Identities.ValueObjects;
  public readonly record struct PublicIdentityId(Guid Value)
  {
      public static PublicIdentityId New() => new(Guid.NewGuid());
  }
  ```
- **Path:** `Percolator.Domain/Identities/ValueObjects/DeviceId.cs`
  ```csharp
  namespace Percolator.Domain.Identities.ValueObjects;
  public readonly record struct DeviceId(uint Value)
  {
      public static DeviceId Primary => new(1);
      public bool IsPrimary => Value == 1;
  }
  ```
- **Path:** `Percolator.Domain/Identities/ValueObjects/IdentityRole.cs` (`UserPersona`, `RelayHost`).
- **Path:** `Percolator.Domain/Identities/ValueObjects/IdentityState.cs` (`Active`, `Disabled`, `Suspended`).
- **Path:** `Percolator.Domain/Identities/ValueObjects/DeviceLinkProof.cs`
  - `[ByteArray(64)] public sealed partial record DeviceLinkProof;`
- **Path:** `Percolator.Domain/Identities/ValueObjects/IdentityPublicKey.cs`
  - `[ByteArray(32)] public sealed partial record IdentityPublicKey;`
- **Path:** `Percolator.Domain/Identities/Events/IdentityCreatedEvent.cs`, `IdentityEnabledEvent.cs`, `IdentityDisabledEvent.cs`.
- **Path:** `Percolator.Domain/Identities/Model/IdentityProfile.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Identities/Model/DeviceRecord.cs` (Entity).
- **Path:** `Percolator.Domain/Identities/Model/PeerContact.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Identities/Model/PreKeyBundleState.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Identities/Ports/IIdentityProfileRepository.cs`, `IPeerContactRepository.cs`, `IPreKeyStore.cs`.

#### 2.3 Verification Criteria
- Run `dotnet test Percolator.Domain.Tests\Percolator.Domain.Tests.csproj`. All tests pass.

---

### Milestone 3: Security Bounded Context (E2EE Ratchets & ZK Credentials)

#### 3.1 Test Fixtures (RED)
- **Path:** `Percolator.Domain.Tests/Security/DirectRatchetSessionTests.cs`
  - `StepRatchet_AdvancesSendingCounter_AndDerivesNewMessageKey()`
  - `ReceiveMessage_WithSkippedCounter_CachesSkippedKeys()`
  - `ReceiveMessage_WhenSkipThresholdExceeded_ReturnsError()`
  - `Dispose_ZeroizesActiveRootAndChainKeys()`
- **Path:** `Percolator.Domain.Tests/Security/GroupSenderKeyRatchetTests.cs`
  - `AdvanceIteration_IncrementsIterationCounter_AndDerivesNextKey()`
  - `Step_RejectsSteppingBackwards()`
  - `Dispose_ZeroizesCurrentChainKey()`

#### 3.2 Production Implementation (GREEN)
- **Path:** `Percolator.Domain/Security/ValueObjects/ChainKey.cs`
  - `[ByteArray(32)] public sealed partial record ChainKey;`
- **Path:** `Percolator.Domain/Security/ValueObjects/MessageKey.cs`
  - `[ByteArray(32)] public sealed partial record MessageKey;`
- **Path:** `Percolator.Domain/Security/ValueObjects/ZkPresentationBytes.cs`
  - `[ByteArray(minLength: 1, maxLength: 1000)] public sealed partial record ZkPresentationBytes;`
- **Path:** `Percolator.Domain/Security/ValueObjects/AuthCredentialMacBytes.cs`
  - `[ByteArray(minLength: 1, maxLength: 1000)] public sealed partial record AuthCredentialMacBytes;`
- **Path:** `Percolator.Domain/Security/Ports/ICryptoEngine.cs` (Pure computation interface).
- **Path:** `Percolator.Domain/Security/Ports/IZkProofEngine.cs` (Pure ZK presentation verification interface).
- **Path:** `Percolator.Domain.Tests/TestDoubles/DeterministicCryptoEngine.cs` (Fast in-memory test double).
- **Path:** `Percolator.Domain/Security/Model/DirectRatchetSession.cs` (Aggregate Root implementing `ISensitiveSecret`).
- **Path:** `Percolator.Domain/Security/Model/GroupSenderKeyRatchet.cs` (Aggregate Root implementing `ISensitiveSecret`).

#### 3.3 Verification Criteria
- Run `dotnet test Percolator.Domain.Tests\Percolator.Domain.Tests.csproj`. All tests pass.

---

### Milestone 4: Conversations Bounded Context (Semantic Chat & Group Invariants)

#### 4.1 Test Fixtures (RED)
- **Path:** `Percolator.Domain.Tests/Conversations/GroupConversationTests.cs`
  - `AddMember_ByAdmin_AddsMember_IncrementsEpoch_AndEmitsGroupEpochAdvancedEvent()`
  - `AddMember_ByNonAdmin_ReturnsUnauthorizedRoleError()`
  - `AppendMessage_AppendsMessage_WithoutChangingEpoch()`
  - `Rebase_WhenEpochConflictOccurs_ReplacesLocalEpochAndRoster()`
- **Path:** `Percolator.Domain.Tests/Conversations/DirectConversationTests.cs`
  - `AppendMessage_AppendsMessage_AndUpdatesLastActivityTimestamp()`
  - `MarkAsRead_UpdatesLastReadMessageId()`

#### 4.2 Production Implementation (GREEN)
- **Path:** `Percolator.Domain/Conversations/ValueObjects/ConversationId.cs`
  ```csharp
  namespace Percolator.Domain.Conversations.ValueObjects;
  public readonly record struct ConversationId(Guid Value) { public static ConversationId New() => new(Guid.NewGuid()); }
  ```
- **Path:** `Percolator.Domain/Conversations/ValueObjects/MessageId.cs`
  ```csharp
  namespace Percolator.Domain.Conversations.ValueObjects;
  public readonly record struct MessageId(Guid Value) { public static MessageId New() => new(Guid.NewGuid()); }
  ```
- **Path:** `Percolator.Domain/Conversations/ValueObjects/EpochNumber.cs`
  ```csharp
  namespace Percolator.Domain.Conversations.ValueObjects;
  public readonly record struct EpochNumber(uint Value) { public EpochNumber Next() => new(Value + 1); }
  ```
- **Path:** `Percolator.Domain/Conversations/ValueObjects/GroupRole.cs` (`Member`, `Admin`).
- **Path:** `Percolator.Domain/Conversations/Model/Message.cs` (Entity).
- **Path:** `Percolator.Domain/Conversations/Model/GroupMember.cs` (Entity).
- **Path:** `Percolator.Domain/Conversations/Model/DirectConversation.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Conversations/Model/GroupConversation.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Conversations/Ports/IConversationRepository.cs`.

#### 4.3 Verification Criteria
- Run `dotnet test Percolator.Domain.Tests\Percolator.Domain.Tests.csproj`. All tests pass.

---

### Milestone 5: Delivery Bounded Context (Routing, Outbox & Relay Hosting)

#### 5.1 Test Fixtures (RED)
- **Path:** `Percolator.Domain.Tests/Delivery/OutboxJobTests.cs`
  - `Enqueue_WhenIdentityIsActive_StatusIsPending()`
  - `Pause_WhenIdentityIsDisabled_TransitionsToPausedDormant()`
  - `Resume_WhenIdentityIsEnabled_TransitionsBackToPending()`
- **Path:** `Percolator.Domain.Tests/Delivery/RelayGroupLedgerTests.cs`
  - `CreateGenesis_WithEpochZeroAndNonEmptyRoster_Succeeds()`
  - `CommitMutation_WithMatchingBaseEpochAndValidZkProof_AdvancesEpoch_AndUpdatesRoster()`
  - `CommitMutation_WithMismatchedBaseEpoch_ReturnsEpochConflictError()`
  - `VerifyDispatch_WithInvalidZkProof_RejectsFanOut()`
- **Path:** `Percolator.Domain.Tests/Delivery/RelayMailboxQueueTests.cs`
  - `Enqueue_StoresEnvelopeUnderRecipientToken()`
  - `PurgeExpired_RemovesEnvelopesPastExpiresAtUtc()`
  - `PruneQuota_WhenLimitExceeded_PrunesOldestUnacknowledgedEnvelopesFirst()`

#### 5.2 Production Implementation (GREEN)
- **Path:** `Percolator.Domain/Delivery/ValueObjects/BlindedRoutingToken.cs`
  ```csharp
  namespace Percolator.Domain.Delivery.ValueObjects;
  public readonly record struct BlindedRoutingToken(Guid Value) { public static BlindedRoutingToken New() => new(Guid.NewGuid()); }
  ```
- **Path:** `Percolator.Domain/Delivery/ValueObjects/DeliveryRoute.cs` (`DirectP2P`, `RelayedOneToOne`, `RelayedGroup`).
- **Path:** `Percolator.Domain/Delivery/ValueObjects/OutboxStatus.cs` (`Pending`, `InFlight`, `Delivered`, `Failed`, `PausedDormant`).
- **Path:** `Percolator.Domain/Delivery/ValueObjects/MailboxEnvelope.cs` (`readonly record struct` with `ReadOnlyMemory<byte>`).
- **Path:** `Percolator.Domain/Delivery/ValueObjects/PurgePolicy.cs`.
- **Path:** `Percolator.Domain/Delivery/Client/OutboxJob.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Delivery/Hosting/RelayGroupLedger.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Delivery/Hosting/RelayMailboxQueue.cs` (Aggregate Root).
- **Path:** `Percolator.Domain/Delivery/Ports/IOutboxRepository.cs`, `IRelayLedgerRepository.cs`, `IRelayMailboxRepository.cs`.

#### 5.3 Verification Criteria
- Run `dotnet test Percolator.Domain.Tests\Percolator.Domain.Tests.csproj`. All tests pass.
