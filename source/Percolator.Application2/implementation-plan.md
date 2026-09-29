# Percolator.Application2 Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Application2` (Application microkernel: high-performance ingress pipeline, outbox worker, transport routing, and profile coordination).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no gRPC, SQLite, EF Core, or socket APIs).
  - Pure onion architecture: All external interactions are abstracted behind outbound ports (interfaces).
  - Strict serialization boundary: Application layer handles pure C# DTOs and delegates serialization to `IPayloadSerializer`. Concrete Protobuf contracts (`.proto`) and Google Protobuf code live strictly in `Percolator.Infrastructure2.Serialization`.
  - Clean boundary with application plugins: App-specific payloads and logic (`Apps.Chat`, `Apps.Discovery`, `Apps.FileTransfer`) reside in their respective plugin projects, not in `Application2`.
  - High-performance, NGINX-inspired design: Phase-based sequential processing, $O(1)$ zero-branching jump table dispatching, and zero-allocation hot paths (`readonly record struct` contexts, `IBufferWriter<byte>`).
  - Symmetrical High-Performance Ingress & Egress: Ingress uses a 5-phase zero-allocation inbound pipeline; Egress uses a 5-phase sequential egress pipeline with a dual-path dispatcher (zero-allocation memory stream channel fast-path + persistent outbox spillover slow-path).
  - Test-first implementation: All behaviors must have corresponding unit tests using test doubles.

---

## Milestone 1: High-Performance Ingress Pipeline & App Host (`Percolator.Application2`)

### 1.1 NGINX-Style Phased Ingress Pipeline
The ingress pipeline processes inbound packets through strictly ordered, unbranching phases, avoiding nested conditional sprawl:

```
[ Inbound Wire Envelope ]
         │
         ▼
┌─────────────────────────┐
│ Phase 1: Frame Parse    │ ──> Fast validation (<64KB payload cap, version header check, frame slicing)
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 2: Ingress Filter │ ──> Black-hole check (IdentityDisabledEvent), rate-limit check, quota validation
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 3: Cryptography   │ ──> Double Ratchet step with Associated Data (AD) verification (RatchetWireFrame)
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 4: Direct Jump    │ ──> O(1) jump table dispatch to registered IAppPayloadHandler[AppId.Value]
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 5: Post-Action    │ ──> Domain event publication, telemetry metrics, ephemeral span zeroization
└─────────────────────────┘
```

### 1.2 Direct $O(1)$ Jump-Table Router
- **Zero-Branching Dispatch Table (`AppRouter`)**:
  - `AppId` is an 8-bit value (`byte Value` $\in [0, 255]$).
  - The router maintains a fixed 256-slot array `IAppPayloadHandler?[256]`.
  - Handler registration assigns directly by slot: `_handlers[handler.TargetAppId.Value] = handler`.
  - Dispatching performs an immediate $O(1)$ indexed jump (`_handlers[context.AppId.Value]`) without dictionary lookups, LINQ scans, or branching trees.
  - If a slot is null, immediately returns `DomainResult.Failure(new DomainError("UNKNOWN_APP_ID", ...))`.

### 1.3 Wire Framing & Associated Data (AD) Binding
- **`RatchetWireFrame` Structure**:
  - Enforces Double Ratchet wire header serialization (`DhPublicKey`, `MessageCounter`, `PreviousChainLength`).
  - Feeds the serialized header bytes into `ICryptoEngine.EncryptAesGcm` / `DecryptAesGcm` as Associated Data (AD) to guarantee header authenticity.

### 1.4 Test Doubles & Unit Tests (`Percolator.Application2.Tests/Ingress`)
- **`InMemoryAppPluginRegistry`**: Test double implementing plugin registration.
- **`FakeAppPayloadHandler`**: Test double capturing handled payloads.
- `AppHostPipelineTests.DispatchAsync_WithRegisteredPlugin_InvokesHandlerDirectly`: asserts direct jump table dispatch.
- `AppHostPipelineTests.DispatchAsync_WithUnregisteredAppId_ReturnsUnknownAppIdFailure`: asserts $O(1)$ lookup fallback.
- `AppHostPipelineTests.DispatchAsync_PayloadExceedingSizeLimit_ReturnsPayloadTooLargeError`: verifies Phase 1 max payload constraint.
- `AppHostPipelineTests.DispatchAsync_SenderDisabled_AbortsInIngressFilterPhase`: verifies Phase 2 black-hole filtering.
- `AppHostPipelineTests.DispatchAsync_CorruptWireFrame_FailsAssociatedDataValidation`: verifies Phase 3 cryptographic AD rejection.

---

## Milestone 2: High-Performance Egress Pipeline & Stream Orchestration

### 2.1 Symmetrical 5-Phase Sequential Egress Pipeline
Outgoing packets from applications (`IPayloadSender`) or relay services are processed through a strictly ordered 5-phase sequential pipeline:

```
[ Outbound Application Frame (AppId, Payload, ChannelId, RecipientId/Group) ]
         │
         ▼
┌─────────────────────────┐
│ Phase 1: Payload Pack   │ ──> Serialize DTO via IPayloadSerializer to IBufferWriter (Zero Heap Alloc)
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 2: Crypto Ratchet │ ──> Advance Double Ratchet / Sender Key; construct RatchetWireFrame & AD
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 3: Route Resolve  │ ──> Query IRoutingCoordinator (Direct P2P Stream vs Relay Mailbox vs Group Ledger)
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 4: Dual Dispatch  │ ──> Fast Path: Non-blocking try-write to IStreamRegistry open stream
│                         │     Slow Path: On stream offline/backpressure, spill to IOutboxRepository
└─────────────────────────┘
         │
         ▼
┌─────────────────────────┐
│ Phase 5: Post-Action    │ ──> Telemetry metrics, ephemeral key span zeroization, event notification
└─────────────────────────┘
```

### 2.2 Stream Registry & Stream Orchestration Ports
A relay node or high-throughput client holds large numbers of open gRPC streams (duplex client streams, server streams). The application layer orchestrates stream availability and backpressure without coupling to gRPC or network protocols:

- **`IStreamRegistry` Port**:
  ```csharp
  public interface IStreamRegistry
  {
      bool HasActiveStream(PublicIdentityId targetId);
      bool HasActiveRelayStream(PublicIdentityId relayId);
      ValueTask<StreamWriteResult> TryWriteAsync(
          PublicIdentityId targetId,
          ReadOnlyMemory<byte> framedPayload,
          CancellationToken ct = default);
  }
  ```
- **`StreamWriteResult` Value Object**:
  - `Success`: Envelope written to active HTTP/2 stream buffer with zero disk writes.
  - `StreamClosed`: Stream disconnected; triggers route failover or outbox spillover.
  - `Backpressured`: Stream HTTP/2 flow control window full; triggers outbox spillover to prevent memory bloat.
- **Relay 1-to-$N$ Group Broadcast Fan-Out**:
  - When committing mutations or publishing group channel payloads to a shared relay, `Application2` packages **one** encrypted payload bound to the active blinded routing tokens or zero-knowledge proof.
  - The relay stream dispatcher dispatches a single multi-recipient envelope across the upstream relay stream rather than serializing $N$ distinct outbox jobs.

### 2.3 Dual-Path Outbox Worker & Retry State Machine
- **Fast-Path vs. Slow-Path (NGINX "Buffer on Spill")**:
  - **Fast-Path**: If `IStreamRegistry` holds an active stream, the payload writes directly to the stream's memory channel without disk I/O.
  - **Slow-Path**: If the stream is closed, offline, or backpressured, the packet spills seamlessly into `IOutboxRepository`.
- **`DeliveryChannelType` Enum**:
  - `PeerDirectAuthenticated`: Direct peer-to-peer authenticated stream.
  - `RelayAnonymousDelivery`: Store-and-forward mailbox delivery token.
  - `RelayGroupBroadcast`: Multi-party broadcast via relay group ledger.
- **`OutboxJob` Value Object/Entity**: Tracks outgoing messages (`JobId`, `ChannelId`, `RecipientId`, `DeliveryChannelType`, `PayloadBytes`, `Attempts`, `MaxAttempts`, `NextAttemptUtc`, `Status`: `Pending`, `InFlight`, `Delivered`, `Failed`, `PausedDormant`).
- **`IOutboxRepository` Port**: Abstract persistence interface for outbox jobs.
- **`ITransportDispatcher` Port**: Abstract outbound transport contract (`SendAsync(OutboxJob job, CancellationToken ct)`).
- **`OutboxWorker` Hosted Process / Background Engine**:
  - Fetches due `Pending` jobs.
  - Dispatches via `ITransportDispatcher` based on `DeliveryChannelType`.
  - Advances state to `Delivered` on success; applies exponential backoff with jitter on transient failures (`NextAttemptUtc = Now + 2^attempts * baseInterval + jitter`).
  - Marks job `Failed` once `MaxAttempts` is reached.
- **`DormancyEventListener`**: Domain event subscriber for `IdentityDisabledEvent` transitioning pending jobs for dormant personas to `PausedDormant` ("Black Hole" rule).

### 2.4 Test Doubles & Unit Tests (`Percolator.Application2.Tests/Delivery`)
- **`InMemoryStreamRegistry`**: Test double capturing active stream states and simulating backpressure/disconnects.
- **`InMemoryOutboxRepository`**: Test double defined strictly within `Percolator.Application2.Tests/TestDoubles`.
- `EgressPipelineTests.DispatchAsync_WhenStreamOnline_DispatchesViaFastPathWithoutDbAlloc`: verifies zero-disk fast path.
- `EgressPipelineTests.DispatchAsync_WhenStreamDisconnected_SpillsToOutbox`: verifies automatic outbox spillover.
- `EgressPipelineTests.DispatchAsync_WhenStreamBackpressured_SpillsToOutboxWithoutDropping`: verifies backpressure protection.
- `EgressPipelineTests.DispatchGroupBroadcast_DispatchesSingleFramedPayloadWithRosterTokens`: verifies fan-out optimization.
- `OutboxWorkerTests.ProcessBatchAsync_WhenTransportSucceeds_MarksJobDelivered`: asserts status delta to `Delivered`.
- `OutboxWorkerTests.ProcessBatchAsync_WhenTransientFailure_SchedulesBackoff`: asserts retry counter increment and future `NextAttemptUtc`.
- `OutboxWorkerTests.ProcessBatchAsync_WhenMaxRetriesExceeded_MarksJobFailed`: asserts transition to terminal `Failed` state.
- `OutboxRetryPolicyTests.CalculateBackoff_IncreasesExponentiallyWithJitter`: asserts backoff calculation bounds.
- `DormancyEventListenerTests.OnIdentityDisabled_TransitionsAllIdentityJobsToPausedDormant`: verifies zero network leakage for disabled identities.

---

## Milestone 3: Transport Routing Coordinator

### 3.1 Routing Coordinator Engine
- **`DeliveryRoutingMode` Enum**: `DirectPeer` (direct gRPC socket) vs. `RelayMailbox` (store-and-forward relay queue).
- **`IRoutingCoordinator` & `RoutingCoordinator`**:
  - Queries active peer presence and reachability tickets from discovery ports and stream liveness from `IStreamRegistry`.
  - Determines optimal outbound routing path: routes direct when peer stream or socket is verified and reachable; falls back automatically to `RelayMailbox` if direct delivery fails or peer is marked offline/dormant.
  - Emits routing telemetry on transport failover.

### 3.2 Test Doubles & Unit Tests (`Percolator.Application2.Tests/Routing`)\n- `RoutingCoordinatorTests.ResolveRoute_WhenPeerOnlineAndReachable_SelectsDirect`: asserts direct P2P routing selection.
- `RoutingCoordinatorTests.ResolveRoute_WhenPeerOfflineOrSuspect_SelectsRelay`: asserts graceful fallback to relay mailbox.

---

## Milestone 4: Profile & Contact Request Coordination

### 4.1 Signal Encrypted Profiles
- **`ProfileKey` & `ProfileCiphertextPackage`**: 32-byte symmetric key and AES-GCM ciphertext container protecting profile metadata (display name, avatar bytes, status bio, revision number).
- **`IProfileManager` & `ProfileManager`**:
  - Encrypts updated profile packages using current `ProfileKey`.
  - Rotates `ProfileKey` and distributes to approved contacts upon identity update.
  - Decrypts remote contact profiles using cached keys.

### 4.2 Contact Request Approval Flow
- **`ContactRequestCoordinator`**:
  - Listens to inbound contact requests and checks `PeerContact.State`.
  - If state is `PendingApproval`: prompts UI and buffers handshake until approved.
  - Once approved: transitions `PeerContact.Approve()`, saves repository, and unlocks Double Ratchet handshake payload dispatch.
  - If rejected: marks `PeerContact.Reject()` and suppresses future outbox transmissions.

### 4.3 Test Doubles & Unit Tests (`Percolator.Application2.Tests/Profiles`)
- `ProfileManagerTests.UpdateProfile_EncryptsProfilePackage_AndDistributesKey`: asserts profile package generation.
- `ContactRequestCoordinatorTests.InboundRequest_WhenPending_BlocksRatchetHandshake`: asserts contact gating.
- `ContactRequestCoordinatorTests.ApproveRequest_TransitionsContactToActive_AndResumesOutbox`: asserts approval flow.
