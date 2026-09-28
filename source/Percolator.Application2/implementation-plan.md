# Percolator.Application2 & App Plugins Implementation Plan

## Summary & Architectural Constraints
- **Target Projects**:
  - `Percolator.Application2` (Core application routing, dispatching, outbox worker, and session coordination).
  - `Percolator.Apps.Chat` (Messaging plugin for 1:1 and group chats, sender key distribution, read receipts, and link previews).
  - `Percolator.Apps.Discovery` (DHT rendezvous, blind locator queries, and presence tracking).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no gRPC, SQLite, EF Core, or socket APIs).
  - Pure onion architecture: All external interactions are abstracted behind outbound ports (interfaces).
  - Test-first implementation: All behaviors must have corresponding unit tests using test doubles.

---

## Milestone 1: Application Ingress & App Host Pipeline (`Percolator.Application2`)

### 1.1 Ingress Dispatcher & Pipeline Architecture
- **`IAppPlugin` & `IAppPayloadHandler` Ports**: Contracts for registered application modules (`AppId`, `Version`, `CanHandle(byte appType)`).
- **`AppHostPipeline`**:
  - Validates authenticated framing and routes decrypted payloads to the correct `IAppPayloadHandler`.
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

## Milestone 2: Outbox Worker & Transport Dispatcher (`Percolator.Application2`)

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
- **Group Metadata Profile Encryption**:
  - Encrypts and decrypts group metadata payloads (title, avatar, and membership roster) using symmetric key derived from `GroupMasterKey`.
- **`SenderKeyDistributionPayload`**: Structured application payload (`ConversationId`, `ChainKey`, `Iteration`, `Epoch`) distributed across 1:1 pairwise sessions to bootstrap group chat sender-key ratchets.
- **`PendingGroupInvitation` State Machine**:
  - Model representing incoming invitations to join group conversations (`InvitationId`, `ConversationId`, `InviterId`, `InitialMembers`, `ReceivedAtUtc`, `Status`: `Pending`, `Accepted`, `Declined`, `Expired`).
  - Ensures local user consent before joining a group or deriving group sender keys.
- **`IUnknownGroupMessageCache` Port & Cache**:
  - Bounded FIFO cache (capacity 100 messages) that holds out-of-order group messages received before the author's `SenderKeyDistributionPayload` has arrived.
  - Automatically replays and decrypts buffered messages when the sender key distribution arrives.
- **`ChatPayloadHandler`**: Implements `IAppPayloadHandler`. Deserializes inbound payload via `IPayloadSerializer` and invokes domain conversation methods (`DirectConversation.AppendMessage`, `GroupConversation.AppendMessage`).
- **`ILinkPreviewFetcher` Port**: Abstraction for fetching raw HTML to prevent direct network I/O in the application layer.
- **`LinkPreviewParser`**: Extracts OpenGraph metadata from provided HTML and generates compact preview thumbnail data (< 32KB).

### 4.2 Unit Tests (`Percolator.Apps.Chat.Tests`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsMessageToConversation`: asserts domain message appended.
- `ChatPayloadHandlerTests.HandleInboundAsync_SenderKeyDistribution_InitializesGroupReceiverSession`: asserts sender key ratchet setup.
- `ChatPayloadHandlerTests.HandleInboundAsync_SenderKeyDistribution_FlushesUnknownMessageCache`: asserts buffered messages decrypted upon key arrival.
- `PendingGroupInvitationTests.AcceptInvitation_InitializesGroupConversation_AndTransitionsStatus`: asserts consent workflow.
- `UnknownMessageCacheTests.Enqueue_WhenLimitExceeded_EvictsOldestMessage`: asserts bounded FIFO invariant.
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
