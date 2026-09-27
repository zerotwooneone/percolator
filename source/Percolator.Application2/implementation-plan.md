# Percolator.Application2 Implementation Plan

## Milestone 1: Microkernel Pipeline & Plugin Contracts (`Percolator.Application2`)

### 1.1 Ingress Gateway & Edge Protection
- **`IInboundIngressService` Port**: Inbound gateway entry point called by transport endpoints (gRPC `DeliverOpaqueMessage`, etc.). Enforces edge packet limits (e.g. max 64KB), parses outer envelope/session headers, invokes domain ratchet authenticated decryption, and forwards decrypted `ApplicationFrame` into the pipeline.
- **Inbound Pipeline Sanitization Behaviors (`IPipelineBehavior<InboundPayloadContext>`)**:
  - **`FrameSanityBehavior`**: Validates frame boundaries (size between 1 byte and 64KB limit) and checks that `AppId` is a recognized registered application ID before deserialization.
  - **`DeduplicationBehavior`**: Idempotency filter using sliding-window / Bloom filter caching to drop duplicated or replayed packets.
  - **`InboundRateLimitingBehavior`**: Anti-flood throttling per peer identity/device, rejecting spam bursts with `RATE_LIMIT_EXCEEDED`.
- **`IPipelineBehavior<TContext>` & `PipelineDelegate<TContext>`**: Generic middleware delegate chain for inbound and outbound contexts.
- **`PayloadDispatcher`**: Implements `IPayloadDispatcher`. Routes inbound decrypted payloads to registered `IAppPayloadHandler` based on `InboundPayloadContext.AppId`.
- **`OutboundPipeline`**: Implements `IPayloadSender` and `IOutboundPipeline`. Validates non-empty payloads, executes outbound middleware pipeline, attaches `ApplicationFrame` header, and packages payload into an `OutboxJob`.
- **`ServiceCollectionExtensions`**: DI extension methods `AddPercolatorApplication(this IServiceCollection services)` and `AddAppPlugin<TPlugin>(this IServiceCollection services)`.

### 1.2 Unit Tests (`Percolator.Application2.Tests/Pipeline`)
- **Sanitization & Edge Defense Tests:**
  - `FrameSanityBehaviorTests.HandleAsync_WithPayloadExceedingMaxLimit_ReturnsPayloadTooLargeError`: asserts rejection when frame exceeds 64KB.
  - `FrameSanityBehaviorTests.HandleAsync_WithZeroLengthPayload_ReturnsMalformedFrameError`: asserts rejection on empty payload.
  - `FrameSanityBehaviorTests.HandleAsync_WithUnrecognizedAppId_ReturnsUnknownAppIdError`: asserts rejection before handler dispatch.
  - `DeduplicationBehaviorTests.HandleAsync_DuplicateMessage_SuppressesDownstreamExecution`: asserts duplicate dropped without invoking next delegate.
  - `InboundRateLimitingBehaviorTests.HandleAsync_ExceedsBurstQuota_ReturnsRateLimitExceededError`: asserts throttling when quota exceeded.
- **Dispatcher & Outbound Pipeline Tests:**
  - `PayloadDispatcherTests.DispatchAsync_WithRegisteredHandler_RoutesPayloadCorrectly`: asserts handler is invoked with matching context.
  - `PayloadDispatcherTests.DispatchAsync_WithUnregisteredAppId_ReturnsHandlerNotFoundError`: asserts `HANDLER_NOT_FOUND` domain error.
  - `PipelineBehaviorTests.OutboundPipeline_ExecutesMiddlewareInRegisteredOrder`: asserts sequential middleware execution order.
  - `PipelineBehaviorTests.OutboundPipeline_WhenMiddlewareFails_ShortCircuitsPipeline`: asserts execution halts when middleware returns error.
  - `DependencyInjectionTests.AddPercolatorApplication_RegistersCoreServices`: asserts resolving `IPayloadDispatcher`, `IPayloadSender`, and `IInboundIngressService`.

---

## Milestone 2: Outbox Worker & Delivery Orchestrator (`Percolator.Application2`)

### 2.1 Outbox & Delivery Components
- **`OutboxJob` & `OutboxJobId`**: Aggregate entity tracking queued delivery items (`Pending`, `InFlight`, `Delivered`, `Failed`, `PausedDormant`).
- **`IOutboxRepository` Port**: Defined in `Percolator.Application2.Delivery.Ports` (`EnqueueAsync`, `FetchPendingJobsAsync`, `UpdateStatusAsync`, `PauseJobsForIdentityAsync`).
- **`ITransportDispatcher` Port**: Defined in `Percolator.Application2.Delivery.Ports` (`DispatchJobAsync`).
- **`OutboxRetryPolicy`**: Calculates exponential backoff with jitter and enforces maximum retry limits.
- **`OutboxWorker`**: Push-based worker using `System.Threading.Channels.Channel<OutboxJobId>` for reactive job notifications from `IOutboxRepository` and dispatching via `ITransportDispatcher`.
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

## Milestone 3: Chat Application Plugin (`Percolator.Apps.Chat`)

### 3.1 Components & Ports
- **`ChatPlugin`**: Implements `IAppPlugin` (`AppId.Chat = 0x01`).
- **Data Models**: `TextMessageDto`, `ReactionDto`, `ReceiptDto`.
- **`ChatPayloadHandler`**: Implements `IAppPayloadHandler`. Deserializes inbound payload via `IPayloadSerializer` and invokes domain conversation methods (`DirectConversation.AppendMessage`, `GroupConversation.AppendMessage`).
- **`ILinkPreviewFetcher` Port**: Abstraction for fetching raw HTML to prevent direct network I/O in the application layer.
- **`LinkPreviewParser`**: Extracts OpenGraph metadata from provided HTML and generates compact preview thumbnail data (< 32KB).

### 3.2 Unit Tests (`Percolator.Apps.Chat.Tests`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsMessageToConversation`: asserts domain message appended.
- `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReaction`: asserts reaction state delta.
- `ChatPayloadHandlerTests.HandleInboundAsync_ReadReceipt_UpdatesLastReadMessageId`: asserts read receipt marker advance.
- `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedPayload_ReturnsDeserializationError`: asserts rejection on malformed bytes.
- `LinkPreviewParserTests.ParsePreview_ValidHtml_GeneratesThumbnailUnder32KB`: asserts compact privacy preview generation.

---

## Milestone 4: Peer Discovery Plugin (`Percolator.Apps.Discovery`)

### 4.1 Components & Ports
- **`DiscoveryPlugin`**: Implements `IAppPlugin` (`AppId.Discovery = 0x02`).
- **Data Models**: `DhtPingPayload`, `DhtPongPayload`, `BlindedLocator`.
- **`BlindedLocatorService`**: Pure cryptographic calculation of `SHA256(PublicIdentityId || Salt)` for contact discovery.
- **`DiscoveryPayloadHandler`**: Implements `IAppPayloadHandler`. Handles inbound ping/lookup payloads and returns active routing descriptor.
- **`RendezvousStateMachine`**: Tracks peer presence tickets and prunes expired registrations using `IDateTimeProvider`.

### 4.2 Unit Tests (`Percolator.Apps.Discovery.Tests`)
- `BlindedLocatorTests.ComputeLocator_IsDeterministicAndMatchesSharedSecret`: verifies zero linkability for non-contacts.
- `DiscoveryPayloadHandlerTests.HandleInboundAsync_Ping_ReturnsPongWithRelayDescriptor`: asserts rendezvous ping/pong response.
- `RendezvousStateMachineTests.Register_WhenTtlExpired_PurgesExpiredTickets`: asserts ticket expiration pruning with virtual time.

---

## Milestone 5: Out-of-Band File Transfer Plugin (`Percolator.Apps.FileTransfer`)

### 5.1 Components & Ports
- **`FileTransferPlugin`**: Implements `IAppPlugin` (`AppId.FileTransferControl = 0x03`).
- **Control Plane Models**: `FileManifestDto` (`InfoHash`, `MerkleRoot`, `TotalSizeBytes`, `ChunkSizeBytes`, `EphemeralSymmetricKey`).
- **`MerkleTreeBuilder` & `MerkleProofVerifier`**: Builds Merkle trees from chunk hashes and verifies individual chunk proofs.
- **`IFileChunkStorage` Port**: Application port for reading/writing chunks, keeping physical disk I/O out of the plugin.
- **`FileTransferCoordinator`**: Coordinates out-of-band chunk transfers and verifies chunk integrity against manifest Merkle roots.

### 5.2 Unit Tests (`Percolator.Apps.FileTransfer.Tests`)
- `FileManifestTests.CreateManifest_DerivesAccurateMerkleRootAndKey`: asserts manifest generation.
- `MerkleProofVerifierTests.VerifyChunk_ValidChunk_ReturnsTrue`: asserts valid proof verification.
- `MerkleProofVerifierTests.VerifyChunk_TamperedChunk_ReturnsFalse`: asserts corrupted chunk rejection.
- `FileTransferCoordinatorTests.ReceiveChunk_ValidatesMerkleProofBeforeStorage`: asserts chunk is verified against tree before calling `IFileChunkStorage`.

---

## Milestone 6: End-to-End Cryptographic Integration Test Suite (`Percolator.Application2IntegrationTests`)

### 6.1 Integration Scenarios
- **`DirectOneToOneEncryptionIntegrationTests`**:
  - Full X3DH handshake $\rightarrow$ Double Ratchet steps $\rightarrow$ out-of-order message caching $\rightarrow$ chat payload demuxing $\rightarrow$ memory zeroization verification.
- **`RelayedOneToOneEncryptionIntegrationTests`**:
  - Outbox packing $\rightarrow$ relay queue buffering via `DeliveryToken` $\rightarrow$ recipient drain via `BlindedRoutingToken` $\rightarrow$ decryption $\rightarrow$ purge expired envelopes.
- **`GroupCommunicationEncryptionIntegrationTests`**:
  - Group genesis $\rightarrow$ pairwise sender key distribution $\rightarrow$ ZK-proof group broadcast verification by `RelayGroupLedger` $\rightarrow$ fan-out to members $\rightarrow$ out-of-order decryption by `GroupReceiverSession` $\rightarrow$ epoch rekeying.
