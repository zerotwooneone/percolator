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
- `OutboxWorkerTests.ProcessBatchAsync_WhenTransportSucceeds_MarksJobDelivered`: asserts status delta to `Delivered`.\n- `OutboxWorkerTests.ProcessBatchAsync_WhenTransientFailure_SchedulesBackoff`: asserts retry counter increment and future `NextAttemptUtc`.
- `OutboxWorkerTests.ProcessBatchAsync_WhenMaxRetriesExceeded_MarksJobFailed`: asserts transition to terminal `Failed` state.
- `OutboxRetryPolicyTests.CalculateBackoff_IncreasesExponentiallyWithJitter`: asserts backoff calculation bounds.
- `DormancyEventListenerTests.OnIdentityDisabled_TransitionsAllIdentityJobsToPausedDormant`: verifies zero network leakage for disabled identities.
- `InMemoryOutboxRepositoryTests.EnqueueAndFetch_AdheresToFifoAndStatusFilters`: verifies test double repository invariants.

---

## Milestone 3: Application Workflows (`Percolator.Application2`)

### 3.1 Signal Encrypted Profiles (`Percolator.Application2.Profiles`)
- **`ProfileKey` & `ProfileCiphertextPackage`**: 32-byte symmetric key and AES-GCM ciphertext container protecting profile metadata (display name, avatar bytes, status bio, revision number).
- **`IProfileManager` & `ProfileManager`**:
  - Encrypts local profile data upon update and increments `ProfileRevision`.
  - Securely reveals `ProfileKey` to approved peer contacts.
  - Decrypts and caches remote peer profiles when their `ProfileKey` is received.
- **Unit Tests (`Percolator.Application2.Tests/Profiles`)**:
  - `ProfileManagerTests.UpdateProfile_EncryptsPayload_AndIncrementsRevision`: asserts AES-GCM encryption and revision advance.
  - `ProfileManagerTests.DecryptPeerProfile_WithValidKey_ExtractsCleartext`: asserts successful decryption of peer name and avatar.
  - `ProfileManagerTests.DecryptPeerProfile_WithMismatchedKey_ReturnsDecryptionError`: asserts rejection when MAC check fails.

### 3.2 Contact Request & Inbound Handshake Approval (`Percolator.Application2.Contacts`)
- **`PendingContactRequest` Entity**: Captures unsolicited initial session requests from unknown peers (`RequestId`, `RemotePeerId`, `InitialMessageSnippet`, `CreatedAtUtc`, `State`: `AwaitingApproval`, `Approved`, `Rejected`, `Expired`).
- **`IContactRequestService` & `ContactRequestService`**:
  - Intercepts inbound initial messages from unknown peers: performs authenticated ratchet decryption so user can review the request, but quarantines the message in a pending state.
  - `ApproveAsync(requestId)`: Promotes peer to active `PeerContact`, establishes reciprocal `DirectRatchetSession`, reveals local `ProfileKey`, and moves message to active conversation.
  - `RejectAsync(requestId, blockPeer)`: Purges cached session or updates trust to `PeerTrustLevel.Blocked`.
- **Unit Tests (`Percolator.Application2.Tests/Contacts`)**:
  - `ContactRequestServiceTests.ReceiveUnsolicitedMessage_QuarantinesInPendingState`: asserts message is not delivered to active conversation prior to approval.
  - `ContactRequestServiceTests.ApproveRequest_CreatesPeerContact_AndDeliversPendingMessage`: asserts transition to active contact and message delivery.
  - `ContactRequestServiceTests.RejectRequest_PurgesSession_AndIgnoresSubsequentTraffic`: asserts suppression of rejected traffic.

### 3.3 Transport Routing Coordinator (`Percolator.Application2.Routing`)
- **`DeliveryRoutingMode` Enum**: `DirectPeer` (direct gRPC socket) vs. `RelayMailbox` (store-and-forward relay queue).
- **`IRoutingCoordinator` & `RoutingCoordinator`**:
  - Queries active peer presence and reachability tickets from `Apps.Discovery`.
  - Determines optimal outbound routing path: routes direct when peer socket is verified and reachable; falls back automatically to `RelayMailbox` if direct delivery fails or peer is marked offline/dormant.
- **Unit Tests (`Percolator.Application2.Tests/Routing`)**:
  - `RoutingCoordinatorTests.ResolveRoute_WhenPeerOnlineAndReachable_SelectsDirect`: asserts direct P2P routing selection.
  - `RoutingCoordinatorTests.ResolveRoute_WhenPeerOfflineOrSuspect_SelectsRelay`: asserts graceful fallback to relay mailbox.

---

## Milestone 4: Chat Application Plugin (`Percolator.Apps.Chat`)

### 4.1 Components & Ports
- **`ChatPlugin`**: Implements `IAppPlugin` (`AppId.Chat = 0x01`).
- **Data Models**: `TextMessageDto`, `ReactionDto`, `ReceiptDto`.
- **`SenderKeyDistributionPayload`**: Structured application payload (`ConversationId`, `ChainKey`, `Iteration`, `Epoch`) distributed across 1:1 pairwise sessions to bootstrap group chat sender-key ratchets.
- **`ChatPayloadHandler`**: Implements `IAppPayloadHandler`. Deserializes inbound payload via `IPayloadSerializer` and invokes domain conversation methods (`DirectConversation.AppendMessage`, `GroupConversation.AppendMessage`).
- **`ILinkPreviewFetcher` Port**: Abstraction for fetching raw HTML to prevent direct network I/O in the application layer.
- **`LinkPreviewParser`**: Extracts OpenGraph metadata from provided HTML and generates compact preview thumbnail data (< 32KB).

### 4.2 Unit Tests (`Percolator.Apps.Chat.Tests`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsMessageToConversation`: asserts domain message appended.
- `ChatPayloadHandlerTests.HandleInboundAsync_SenderKeyDistribution_InitializesGroupReceiverSession`: asserts sender key ratchet setup.
- `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReaction`: asserts reaction state delta.
- `ChatPayloadHandlerTests.HandleInboundAsync_ReadReceipt_UpdatesLastReadMessageId`: asserts read receipt marker advance.
- `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedPayload_ReturnsDeserializationError`: asserts rejection on malformed bytes.
- `LinkPreviewParserTests.ParsePreview_ValidHtml_GeneratesThumbnailUnder32KB`: asserts compact privacy preview generation.

---

## Milestone 5: Peer Discovery Plugin (`Percolator.Apps.Discovery`)

### 5.1 Components & Ports
- **`DiscoveryPlugin`**: Implements `IAppPlugin` (`AppId.Discovery = 0x02`).
- **Data Models**: `DhtPingPayload`, `DhtPongPayload`, `BlindedLocator`.
- **`BlindedLocatorService`**: Pure cryptographic calculation of `SHA256(PublicIdentityId || Salt)` for contact discovery.
- **`DiscoveryPayloadHandler`**: Implements `IAppPayloadHandler`. Handles inbound ping/lookup payloads and returns active routing descriptor.
- **`RendezvousStateMachine`**: Tracks peer presence tickets and prunes expired registrations using `IDateTimeProvider`.

### 5.2 Unit Tests (`Percolator.Apps.Discovery.Tests`)
- `BlindedLocatorTests.ComputeLocator_IsDeterministicAndMatchesSharedSecret`: verifies zero linkability for non-contacts.
- `DiscoveryPayloadHandlerTests.HandleInboundAsync_Ping_ReturnsPongWithRelayDescriptor`: asserts rendezvous ping/pong response.
- `RendezvousStateMachineTests.Register_WhenTtlExpired_PurgesExpiredTickets`: asserts ticket expiration pruning with virtual time.

---

## Milestone 6: Out-of-Band File Transfer Plugin (`Percolator.Apps.FileTransfer`)

### 6.1 Components & Ports
- **`FileTransferPlugin`**: Implements `IAppPlugin` (`AppId.FileTransferControl = 0x03`).
- **Control Plane Models**: `FileManifestDto` (`InfoHash`, `MerkleRoot`, `TotalSizeBytes`, `ChunkSizeBytes`, `EphemeralSymmetricKey`).
- **`MerkleTreeBuilder` & `MerkleProofVerifier`**: Builds Merkle trees from chunk hashes and verifies individual chunk proofs.
- **`IFileChunkStorage` Port**: Application port for reading/writing chunks, keeping physical disk I/O out of the plugin.
- **`FileTransferCoordinator`**: Coordinates out-of-band chunk transfers and verifies chunk integrity against manifest Merkle roots.

### 6.2 Unit Tests (`Percolator.Apps.FileTransfer.Tests`)
- `FileManifestTests.CreateManifest_DerivesAccurateMerkleRootAndKey`: asserts manifest generation.
- `MerkleProofVerifierTests.VerifyChunk_ValidChunk_ReturnsTrue`: asserts valid proof verification.
- `MerkleProofVerifierTests.VerifyChunk_TamperedChunk_ReturnsFalse`: asserts corrupted chunk rejection.
- `FileTransferCoordinatorTests.ReceiveChunk_ValidatesMerkleProofBeforeStorage`: asserts chunk is verified against tree before calling `IFileChunkStorage`.

---

## Milestone 7: End-to-End Cryptographic Integration Test Suite (`Percolator.Application2IntegrationTests`)

### 7.1 Integration Scenarios
- **`DirectOneToOneEncryptionIntegrationTests`**:
  - Full X3DH handshake $\rightarrow$ Double Ratchet steps $\rightarrow$ out-of-order message caching $\rightarrow$ chat payload demuxing $\rightarrow$ memory zeroization verification.
- **`RelayedOneToOneEncryptionIntegrationTests`**:
  - Outbox packing $\rightarrow$ relay queue buffering via `DeliveryToken` $\rightarrow$ recipient drain via `BlindedRoutingToken` $\rightarrow$ decryption $\rightarrow$ purge expired envelopes.
- **`GroupCommunicationEncryptionIntegrationTests`**:
  - Group genesis $\rightarrow$ pairwise sender key distribution $\rightarrow$ ZK-proof group broadcast verification by `RelayGroupLedger` $\rightarrow$ fan-out to members $\rightarrow$ out-of-order decryption by `GroupReceiverSession` $\rightarrow$ epoch rekeying.
