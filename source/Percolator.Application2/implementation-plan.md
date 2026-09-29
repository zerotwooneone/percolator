# Percolator.Application2 Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Application2` (Application microkernel: host pipeline, outbox worker, transport routing, and profile coordination).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no gRPC, SQLite, EF Core, or socket APIs).
  - Pure onion architecture: All external interactions are abstracted behind outbound ports (interfaces).
  - Clean boundary with application plugins: App-specific payloads and logic (`Apps.Chat`, `Apps.Discovery`, `Apps.FileTransfer`) reside in their respective plugin projects, not in `Application2`.
  - Test-first implementation: All behaviors must have corresponding unit tests using test doubles.

---

## Milestone 1: Application Ingress & App Host Pipeline

### 1.1 Ingress Dispatcher & Pipeline Architecture
- **`IAppPlugin` & `IAppPayloadHandler` Ports**: Contracts for registered application modules (`AppId`, `Version`, `CanHandle(byte appType)`).
- **`AppHostPipeline`**:
  - Validates authenticated framing and routes decrypted payloads to the correct registered `IAppPayloadHandler`.
  - Enforces envelope size limits (< 64KB per uncompressed payload) and checks payload version headers.
  - Emits telemetry and logging hooks for unrecognized application IDs.
- **`IPayloadSerializer` Port**: Abstract binary serializer interface allowing application plugins to unpack protobuf/binary DTOs without coupling to concrete wire libraries.
- **Wire Framing & Associated Data (AD) Binding**:
  - Enforces Double Ratchet wire header serialization (`RatchetWireFrame`: `DhPublicKey`, `MessageCounter`, `PreviousChainLength`).
  - Feeds the serialized header bytes into `ICryptoEngine.EncryptAesGcm` / `DecryptAesGcm` as Associated Data (AD) to guarantee header authenticity.

### 1.2 Test Doubles & Unit Tests (`Percolator.Application2.Tests/Ingress`)
- **`InMemoryAppPluginRegistry`**: Test double implementing plugin registration.
- **`FakeAppPayloadHandler`**: Test double capturing handled payloads.
- `AppHostPipelineTests.DispatchAsync_WithRegisteredPlugin_InvokesHandler`: asserts successful payload dispatch.
- `AppHostPipelineTests.DispatchAsync_WithUnregisteredAppId_LogsWarningAndDrops`: asserts graceful rejection of unknown AppIds.
- `AppHostPipelineTests.DispatchAsync_PayloadExceedingSizeLimit_ReturnsPayloadTooLargeError`: verifies max payload constraint.
- `AppHostPipelineTests.DispatchAsync_CorruptWireFrame_FailsAssociatedDataValidation`: verifies rejection if header AD has been altered.

---

## Milestone 2: Outbox Worker & Transport Dispatcher

### 2.1 Outbox Queue & Retry State Machine
- **`DeliveryChannelType` Enum**:
  - `PeerDirectAuthenticated`: Sent directly to peer endpoint with mutual TLS/session authentication.
  - `RelayAnonymousDelivery`: Sent to Relay store-and-forward mailbox without sender identity linking.
  - `RelayGroupBroadcast`: Sent to Relay group ledger accompanied by Zero-Knowledge presentation.
- **`OutboxJob` Value Object/Entity**: Tracks outgoing messages (`JobId`, `ConversationId`, `RecipientId`, `DeliveryChannelType`, `PayloadBytes`, `Attempts`, `MaxAttempts`, `NextAttemptUtc`, `Status`: `Pending`, `InFlight`, `Delivered`, `Failed`, `PausedDormant`).
- **`IOutboxRepository` Port**: Abstract persistence interface for outbox jobs.
- **`ITransportDispatcher` Port**: Abstract outbound transport contract (`SendAsync(OutboxJob job, CancellationToken ct)`).
- **`OutboxWorker` Hosted Process / Background Engine**:
  - Fetches due `Pending` jobs.
  - Dispatches via `ITransportDispatcher` based on `DeliveryChannelType`.
  - Advances state to `Delivered` on success; applies exponential backoff with jitter on transient failures (`NextAttemptUtc = Now + 2^attempts * baseInterval + jitter`).
  - Marks job `Failed` once `MaxAttempts` is reached.
- **`DormancyEventListener`**: Domain event subscriber for `IdentityDisabledEvent` transitioning pending jobs for dormant personas to `PausedDormant` ("Black Hole" rule).

### 2.2 Test Doubles & Unit Tests (`Percolator.Application2.Tests/Delivery`)
- **`InMemoryOutboxRepository`**: Test double defined strictly within `Percolator.Application2.Tests/TestDoubles`.
- `OutboxWorkerTests.ProcessBatchAsync_WhenTransportSucceeds_MarksJobDelivered`: asserts status delta to `Delivered`.
- `OutboxWorkerTests.ProcessBatchAsync_WhenTransientFailure_SchedulesBackoff`: asserts retry counter increment and future `NextAttemptUtc`.
- `OutboxWorkerTests.ProcessBatchAsync_WhenMaxRetriesExceeded_MarksJobFailed`: asserts transition to terminal `Failed` state.
- `OutboxRetryPolicyTests.CalculateBackoff_IncreasesExponentiallyWithJitter`: asserts backoff calculation bounds.
- `DormancyEventListenerTests.OnIdentityDisabled_TransitionsAllIdentityJobsToPausedDormant`: verifies zero network leakage for disabled identities.
- `InMemoryOutboxRepositoryTests.EnqueueAndFetch_AdheresToFifoAndStatusFilters`: verifies test double repository invariants.

---

## Milestone 3: Transport Routing Coordinator

### 3.1 Routing Coordinator Engine
- **`DeliveryRoutingMode` Enum**: `DirectPeer` (direct gRPC socket) vs. `RelayMailbox` (store-and-forward relay queue).
- **`IRoutingCoordinator` & `RoutingCoordinator`**:
  - Queries active peer presence and reachability tickets from discovery ports.
  - Determines optimal outbound routing path: routes direct when peer socket is verified and reachable; falls back automatically to `RelayMailbox` if direct delivery fails or peer is marked offline/dormant.
  - Emits routing telemetry on transport failover.

### 3.2 Test Doubles & Unit Tests (`Percolator.Application2.Tests/Routing`)
- `RoutingCoordinatorTests.ResolveRoute_WhenPeerOnlineAndReachable_SelectsDirect`: asserts direct P2P routing selection.
- `RoutingCoordinatorTests.ResolveRoute_WhenPeerOfflineOrSuspect_SelectsRelay`: asserts graceful fallback to relay mailbox.

---

## Milestone 4: Profile & Contact Request Coordination

### 4.1 Signal Encrypted Profiles
- **`ProfileKey` & `ProfileCiphertextPackage`**: 32-byte symmetric key and AES-GCM ciphertext container protecting profile metadata (display name, avatar bytes, status bio, revision number).
- **`IProfileManager` & `ProfileManager`**:
  - Encrypts local profile data upon update and increments `ProfileRevision`.
  - Securely reveals `ProfileKey` to approved peer contacts.
  - Decrypts and caches remote peer profiles when their `ProfileKey` is received.

### 4.2 Contact Request Workflow Coordination
- **`IContactRequestCoordinator` & `ContactRequestCoordinator`**:
  - Application use case service coordinating unsolicited inbound session handshakes.
  - Interfaces with domain `PeerContact.CreateInboundRequest`, `Approve`, `Reject`, and `Block`.
  - On approval: transitions contact to active, reveals local `ProfileKey`, and emits application event for UI.
  - On rejection/block: purges session caches and flags peer in `IPeerContactRepository`.

### 4.3 Test Doubles & Unit Tests (`Percolator.Application2.Tests/ProfilesAndContacts`)
- `ProfileManagerTests.UpdateProfile_EncryptsPayload_AndIncrementsRevision`: asserts AES-GCM encryption and revision advance.
- `ProfileManagerTests.DecryptPeerProfile_WithValidKey_ExtractsCleartext`: asserts successful decryption of peer name and avatar.
- `ProfileManagerTests.DecryptPeerProfile_WithMismatchedKey_ReturnsDecryptionError`: asserts rejection when MAC check fails.
- `ContactRequestCoordinatorTests.ApproveRequest_UpdatesDomainContact_AndRevealsProfileKey`: asserts coordinated domain transition and key reveal.
- `ContactRequestCoordinatorTests.RejectRequest_UpdatesDomainContact_AndPurgesCachedSession`: asserts clean session purge.
