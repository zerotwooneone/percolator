# Percolator.Application2 Implementation Plan
## Event-Driven Microkernel / Plugin Pipeline Architecture

---

## 1. Architectural Vision & Scope

`Percolator.Application2` acts as the **application orchestration core** sitting directly on top of `Percolator.Domain` and `Percolator.PluginSdk`. In strict DDD and Onion Architecture:
- `Percolator.Domain` is pure, isolated, and agnostic to transport, storage, and concrete applications.
- `Percolator.PluginSdk` is the **lightweight shared abstraction library** defining plugin contracts, application contexts, and serializable payload abstractions (`AppId`, `IAppPlugin`, `IAppPayloadHandler`, `IPayloadSerializer`, `IPayloadSender`).
- Applications (`Percolator.Apps.Chat`, `Percolator.Apps.Discovery`, `Percolator.Apps.FileTransfer`) depend *only* on `Percolator.PluginSdk` and `Percolator.Domain`. They have **zero** knowledge of the host pipeline engine, outbox worker, or database infrastructure.
- `Percolator.Application2` hosts the **Event-Driven Microkernel & Plugin Pipeline**, manages outbox jobs, routes messages, and handles cryptographic orchestration across sessions.
- Enforces the strict duality between **Control Plane** (in-band E2EE signaling via domain ratchets) and **Data Plane** (out-of-band high-throughput P2P streaming).
- Declares clear **ports** for external concerns: `IPayloadSerializer` (Protobuf adapter in Infrastructure), `IOutboxRepository` (SQLite in Infrastructure), and `ITransportDispatcher` (gRPC/P2P sockets in Infrastructure).

```
 ┌────────────────────────────────────────────────────────────────────────────────────┐
 │                                   APPLICATIONS / PLUGINS                           │
 │   ┌───────────────────────┐  ┌──────────────────────────┐  ┌───────────────────────┐  │
 │   │  Percolator.Apps.Chat │  │ Percolator.Apps.Discovery│  │ Percolator.Apps.File- │  │
 │   │  - Text, Reactions    │  │ - DHT Blinded Locators   │  │   Transfer            │  │
 │   │  - Read/Delivered Ack │  │ - Rendezvous & Lookup    │  │ - Manifests (Control) │  │
 │   │  - Local Link Preview │  │ - Presence Tickets       │  │ - P2P Swarm (Data)    │  │
 │   └───────────┬───────────┘  └────────────┬─────────────┘  └───────────┬───────────┘  │
 └───────────────┼───────────────────────────┼────────────────────────────┼──────────────┘
                 │                           │                            │
                 ▼                           ▼                            ▼
 ┌────────────────────────────────────────────────────────────────────────────────────┐
 │                                Percolator.PluginSdk                                │
 │       (IAppPlugin, IAppPayloadHandler, AppId, ApplicationFrame,                    │
 │        InboundPayloadContext, OutboundPayloadContext, IPayloadSerializer,          │
 │        IPayloadSender, DeliveryRoute)                                              │
 └────────────────────────────────────────┬───────────────────────────────────────────┘
                                          │
                                          ▼
 ┌────────────────────────────────────────────────────────────────────────────────────┐
 │                         Percolator.Application2 (Microkernel Core)                 │
 │   ┌────────────────────────────────────────────────────────────────────────────┐   │
 │   │                      Message Pipeline & App Multiplexer                    │   │
 │   │   [Inbound Pipeline]  : De-duplication ➔ Rate Limiter ➔ App Dispatcher     │   │
 │   │   [Outbound Pipeline] : App Serializer ➔ AppId Tagging ➔ Domain E2EE Encrypt │   │
 │   └────────────────────────────────────┬───────────────────────────────────────┘   │
 │                                        │                                           │
 │   ┌────────────────────────────────────▼───────────────────────────────────────┐   │
 │   │                      Store-and-Forward Outbox Worker                       │   │
 │   │   - Job Queue & Transactional State Transitions                            │   │
 │   │   - Exponential Backoff & Jitter Retry Scheduler                           │   │
 │   │   - Dormancy Watcher (Zero-Leakage "Black Hole" Rule)                      │   │
 │   └────────────────────────────────────┬───────────────────────────────────────┘   │
 └────────────────────────────────────────┼───────────────────────────────────────────┘
                                          │ Envelopes
                                          ▼
 ┌────────────────────────────────────────────────────────────────────────────────────┐
 │                                   Percolator.Domain                                │
 │       (DirectRatchetSession, GroupSenderKeyRatchet, GroupReceiverSession,          │
 │        RelayGroupLedger, RelayMailboxQueue, RatchetHeader, PreKeyBundle)           │
 └────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Core Architectural Boundaries: Application vs. Infrastructure

### 2.1 Logical Application Framing vs. Infrastructure Wire Framing
To prevent infrastructure concerns (sockets, packet magic bytes, network byte order, chunk framing) from polluting the application layer, framing is strictly segregated:

1. **Logical Application Frame (`Percolator.PluginSdk`):**
   - Models the clean domain/application data structure:
     ```csharp
     public readonly record struct AppId(byte Value)
     {
         public static readonly AppId SystemControl = new(0x00);
         public static readonly AppId Chat = new(0x01);
         public static readonly AppId Discovery = new(0x02);
         public static readonly AppId FileTransferControl = new(0x03);
     }

     public sealed record ApplicationFrame(
         AppId AppId,
         ReadOnlyMemory<byte> Payload);
     ```
   - Application multiplexer tags and strips the 1-byte `AppId`.
2. **Infrastructure Wire Layout (`Percolator.Infrastructure`):**
   - The physical network socket wire framing (magic bytes `0x50 0x01`, 32-bit packet lengths, TLS framing, gRPC stream framing) lives entirely in `Percolator.Infrastructure` behind transport interfaces (`ITransportConnection`, `IPacketCodec`).

### 2.2 Payload Serialization: Port & Adapter Pattern (Protobuf Strategy)
To benefit from Protocol Buffers' robust forward/backwards compatibility without taking a hard dependency on protobuf packages inside the core application library:
- **Shared Plugin Port (`Percolator.PluginSdk`):**
  ```csharp
  public interface IPayloadSerializer
  {
      ReadOnlyMemory<byte> Serialize<T>(T payload);
      DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data);
  }
  ```
- **Infrastructure Adapter (`Percolator.Infrastructure.Serialization`):**
  - Implements `ProtobufPayloadSerializer : IPayloadSerializer` using `Google.Protobuf` or `protobuf-net`.
- **Unit Testing Adapter (`Percolator.Application2.Tests`):**
  - A lightweight test serializer double is provided in the test project so tests execute rapidly (< 50ms) without native dependencies or protobuf compilation overhead.

### 2.3 Outbox Persistence Boundary
- **Port:** `IOutboxRepository` is declared in `Percolator.Application2.Delivery.Ports`.
- **Strict Separation Mandate:** `InMemoryOutboxRepository` must **not** be defined in `Percolator.Application2`. Production persistence (SQLite) belongs in `Percolator.Infrastructure`.
- **Test Doubles:** `InMemoryOutboxRepository` is defined strictly within the test suite (`Percolator.Application2.Tests/TestDoubles`) to support unit and integration testing.

### 2.4 Dependency Injection
- Standard Microsoft DI extension methods are exposed in `Percolator.Application2.DependencyInjection`:
  ```csharp
  public static class ServiceCollectionExtensions
  {
      public static IServiceCollection AddPercolatorApplication(this IServiceCollection services);
      public static IServiceCollection AddAppPlugin<TPlugin>(this IServiceCollection services) 
          where TPlugin : class, IAppPlugin;
  }
  ```

---

## 3. C# Pipeline & Microkernel Interface Contracts

### 3.1 Plugin & Handler Contracts (in `Percolator.PluginSdk`)
```csharp
namespace Percolator.PluginSdk;

public interface IAppPlugin
{
    AppId Id { get; }
    string Name { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public interface IAppPayloadHandler
{
    AppId TargetAppId { get; }
    ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default);
}

public sealed record InboundPayloadContext(
    ConversationId ConversationId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    AppId AppId,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset ReceivedAtUtc);

public sealed record OutboundPayloadContext(
    ConversationId ConversationId,
    PublicIdentityId RecipientIdentityId,
    AppId AppId,
    ReadOnlyMemory<byte> Payload,
    DeliveryRoute Route);

public interface IPayloadSender
{
    ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default);
}
```

### 3.2 Dispatcher & Pipeline Behaviors (in `Percolator.Application2.Pipeline`)
```csharp
namespace Percolator.Application2.Pipeline;

public delegate ValueTask<DomainResult> PipelineDelegate<TContext>(TContext context, CancellationToken ct);

public interface IPipelineBehavior<TContext>
{
    ValueTask<DomainResult> HandleAsync(
        TContext context, 
        PipelineDelegate<TContext> next, 
        CancellationToken ct);
}

public interface IPayloadDispatcher
{
    void RegisterHandler(IAppPayloadHandler handler);
    ValueTask<DomainResult> DispatchAsync(InboundPayloadContext context, CancellationToken ct = default);
}

public interface IOutboundPipeline : IPayloadSender
{
    ValueTask<DomainResult<OutboxJob>> SendAndQueuePayloadAsync(
        OutboundPayloadContext context, 
        CancellationToken ct = default);
}
```

### 3.3 Outbox & Delivery Ports (in `Percolator.Application2.Delivery.Ports`)
```csharp
namespace Percolator.Application2.Delivery.Ports;

public interface IOutboxRepository
{
    Task<DomainResult> EnqueueAsync(OutboxJob job, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxJob>> FetchPendingJobsAsync(int batchSize, CancellationToken ct = default);
    Task<DomainResult> UpdateStatusAsync(OutboxJobId jobId, OutboxStatus newStatus, CancellationToken ct = default);
    Task<DomainResult> PauseJobsForIdentityAsync(PublicIdentityId identityId, CancellationToken ct = default);
}

public interface ITransportDispatcher
{
    Task<DomainResult> DispatchJobAsync(OutboxJob job, CancellationToken ct = default);
}
```

---

## 4. Cryptographic Orchestration Flows (Application Layer)

1. **Direct 1:1 Flow (X3DH & Double Ratchet):**
   - Outbound: Application checks for existing `DirectRatchetSession`. If absent, retrieves `PreKeyBundle` and calls `DirectRatchetSession.InitiateOutbound()`.
   - Steps sending chain, derives `MessageKey`, encrypts payload (AES-GCM), attaches `RatchetHeader`, envelopes into `OutboxJob`.
   - Inbound: Recipient receives packet, initializes `DirectRatchetSession.InitiateInbound()` via `RatchetHeader`, derives key, decrypts, and passes `ApplicationFrame` to `IPayloadDispatcher`.
2. **Store-and-Forward Relay Flow:**
   - Outbound: Encrypted payload + `RatchetHeader` wrapped into `MailboxEnvelope`. Dispatched to `RelayMailboxQueue` using `DeliveryToken`.
   - Inbound: Recipient authenticates to relay using `BlindedRoutingToken`, drains envelopes, unpacks `RatchetHeader`, steps receiving ratchet, and dispatches decrypted payload.
3. **Group Flow (Sender Keys & ZK Relay Dispatch):**
   - Group Author initializes `GroupSenderKeyRatchet`, distributes initial `ChainKey` pairwise to members via 1:1 direct sessions.
   - Members instantiate `GroupReceiverSession`.
   - Author advances sender key, encrypts message, attaches ZK presentation proof, and submits envelope to relay.
   - Relay verifies proof via `RelayGroupLedger.VerifyDispatch()` and broadcasts to active member routing tokens.
   - Recipients decrypt via `GroupReceiverSession.AdvanceTo()`.

---

## 5. TDD Implementation Plan: Milestones & Unit Tests

*Adhering strictly to `unit-testing.md`: Red-Green-Refactor sequence, tests written as code is written, asserting invariants and failure modes with virtualized time.*

### Milestone 1: Microkernel Pipeline & Plugin Contracts (`Percolator.Application2`)
- **Components to Implement:**
  - `IPipelineBehavior<TContext>`.
  - `PayloadDispatcher`: multiplexes inbound payloads by `AppId`.
  - `OutboundPipeline`: implements `IPayloadSender`, runs middleware behaviors, validates non-empty payloads, and encapsulates logical frame.
  - DI registration: `AddPercolatorApplication()`, `AddAppPlugin<T>()`.
- **Unit Tests Written (`Percolator.Application2.Tests`):**
  - `PayloadDispatcherTests.DispatchAsync_WithRegisteredHandler_RoutesPayloadCorrectly`: verifies handler invocation.
  - `PayloadDispatcherTests.DispatchAsync_WithUnregisteredAppId_ReturnsHandlerNotFoundError`: asserts `HANDLER_NOT_FOUND` error.
  - `PayloadDispatcherTests.DispatchAsync_WithEmptyPayload_ReturnsMalformedFrameError`: boundary guard against 0-byte frames.
  - `PipelineBehaviorTests.OutboundPipeline_ExecutesMiddlewareInRegisteredOrder`: asserts pipeline order.
  - `PipelineBehaviorTests.OutboundPipeline_WhenMiddlewareFails_ShortCircuitsPipeline`: verifies failure propagation without calling next.
  - `DependencyInjectionTests.AddPercolatorApplication_RegistersCoreServices`: asserts required services resolve.

### Milestone 2: Outbox Worker & Delivery Orchestrator (`Percolator.Application2`)
- **Components to Implement:**
  - `OutboxJob`, `OutboxJobId`, `OutboxStatus` (`Pending`, `InFlight`, `Delivered`, `Failed`, `PausedDormant`).
  - `IOutboxRepository` (port), `ITransportDispatcher` (port).
  - `InMemoryOutboxRepository` (implemented in `Percolator.Application2.Tests/TestDoubles` for testing).
  - `OutboxRetryPolicy`: exponential backoff with jitter calculation.
  - `OutboxWorker`: background channel processing pending jobs.
  - `DormancyEventListener`: listens to `IdentityDisabledEvent` and transitions jobs to `PausedDormant` ("Black Hole" rule).
- **Unit Tests Written (`Percolator.Application2.Tests`):**
  - `OutboxWorkerTests.ProcessBatchAsync_WhenTransportSucceeds_MarksJobDelivered`: asserts status delta to `Delivered`.
  - `OutboxWorkerTests.ProcessBatchAsync_WhenTransientFailure_SchedulesBackoff`: asserts retry count increment and future `NextAttemptUtc`.
  - `OutboxWorkerTests.ProcessBatchAsync_WhenMaxRetriesExceeded_MarksJobFailed`: asserts terminal `Failed` state.
  - `OutboxRetryPolicyTests.CalculateBackoff_IncreasesExponentiallyWithJitter`: boundary calculation tests.
  - `DormancyEventListenerTests.OnIdentityDisabled_TransitionsAllIdentityJobsToPausedDormant`: verifies zero leakage for dormant personas.
  - `InMemoryOutboxRepositoryTests.EnqueueAndFetch_AdheresToFifoAndStatusFilters`: verifies repository invariants using the test double.

### Milestone 3: Chat Application Plugin (`Percolator.Apps.Chat`)
- **Components to Implement:**
  - `ChatPlugin` (`IAppPlugin`, `AppId = 0x01`).
  - DTOs: `TextMessageDto`, `ReactionDto`, `ReceiptDto`.
  - `ChatPayloadHandler` (`IAppPayloadHandler`): deserializes chat payload via `IPayloadSerializer` and emits domain conversation commands/events.
  - `LinkPreviewExtractor`: sender-side OpenGraph metadata and compressed thumbnail packager.
- **Unit Tests Written (`Percolator.Apps.Chat.Tests`):**
  - `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsMessageToConversation`: asserts domain message appended.
  - `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReaction`: asserts reaction state delta.
  - `ChatPayloadHandlerTests.HandleInboundAsync_ReadReceipt_UpdatesLastReadMessageId`: asserts read marker advance.
  - `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedPayload_ReturnsDeserializationError`: asserts error handling.
  - `LinkPreviewExtractorTests.ExtractPreview_ValidHtml_GeneratesThumbnailUnder32KB`: asserts compact privacy preview generation.

### Milestone 4: Peer Discovery Plugin (`Percolator.Apps.Discovery`)
- **Components to Implement:**
  - `DiscoveryPlugin` (`IAppPlugin`, `AppId = 0x02`).
  - `DiscoveryPayloadHandler`: handles `DhtPing`, `DhtFindNode`, `DhtNodeAdvertisement`.
  - `BlindedLocatorService`: computes `SHA256(PublicIdentityId || Salt)` for contact discovery.
  - `RendezvousStateMachine`: manages rendezvous registration and expiration.
- **Unit Tests Written (`Percolator.Apps.Discovery.Tests`):**
  - `BlindedLocatorTests.ComputeLocator_IsDeterministicAndMatchesSharedSecret`: verifies zero linkability for non-contacts.
  - `DiscoveryPayloadHandlerTests.HandleInboundAsync_Ping_ReturnsPongWithRelayDescriptor`: asserts rendezvous ping/pong.
  - `RendezvousStateMachineTests.Register_WhenTtlExpired_PurgesExpiredRendezvousTickets`: asserts TTL purging with `IDateTimeProvider`.

### Milestone 5: Out-of-Band File Transfer Plugin (`Percolator.Apps.FileTransfer`)
- **Components to Implement:**
  - `FileTransferPlugin` (`IAppPlugin`, `AppId = 0x03`).
  - Control Plane: `FileManifestDto` (`InfoHash`, `MerkleRoot`, `TotalSizeBytes`, `ChunkSizeBytes`, `EphemeralSymmetricKey`).
  - `MerkleTreeBuilder` & `MerkleProofVerifier`.
  - Data Plane: `ChunkTransferAdapter` (transfers raw encrypted blocks directly out-of-band, verified against Merkle leaves).
- **Unit Tests Written (`Percolator.Apps.FileTransfer.Tests`):**
  - `FileManifestTests.CreateManifest_DerivesAccurateMerkleRootAndKey`: asserts manifest generation.
  - `MerkleProofVerifierTests.VerifyChunk_ValidChunk_ReturnsTrue`: asserts valid proof verification.
  - `MerkleProofVerifierTests.VerifyChunk_TamperedChunk_ReturnsFalse`: asserts corrupted chunk rejection.
  - `ChunkTransferAdapterTests.Transfer_TransfersDataPlaneDirectlyWithoutRatchetInvolvement`: asserts zero Double Ratchet involvement.

### Milestone 6: End-to-End Cryptographic Integration Test Suite (`Percolator.Application2IntegrationTests`)
- **Tests Implemented (Run upon completion of Milestones 1–5):**
  - **`DirectOneToOneEncryptionIntegrationTests`:**
    - Full X3DH handshake $\rightarrow$ Double Ratchet steps $\rightarrow$ out-of-order message caching $\rightarrow$ chat payload demuxing $\rightarrow$ memory zeroization verification.
  - **`RelayedOneToOneEncryptionIntegrationTests`:**
    - Outbox packing $\rightarrow$ relay queue buffering via `DeliveryToken` $\rightarrow$ recipient drain via `BlindedRoutingToken` $\rightarrow$ decryption $\rightarrow$ purge expired.
  - **`GroupCommunicationEncryptionIntegrationTests`:**
    - Group genesis $\rightarrow$ pairwise sender key distribution $\rightarrow$ ZK-proof group broadcast verification by `RelayGroupLedger` $\rightarrow$ fan-out to members $\rightarrow$ out-of-order decryption by `GroupReceiverSession` $\rightarrow$ epoch rekeying.
