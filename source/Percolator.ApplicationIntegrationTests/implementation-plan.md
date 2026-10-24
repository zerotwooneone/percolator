# Implementation Plan: `Percolator.ApplicationIntegrationTests` (Phased Multi-Identity Suite)

## 1. Overview & Architectural Principles
Build a progressive, 7-phase integration test suite in `Percolator.ApplicationIntegrationTests` (`RootNamespace: Percolator.Application2IntegrationTests`) where a fluent **Identity & Cluster Builder** spins up isolated nodes bound to distinct `localhost:<port>` endpoints (`localhost:15001`..`localhost:15005`) over an in-memory loopback network switch.

### Strict DDD, Hexagonal (Ports & Adapters), and CQRS Rules
1. **No Repository Reads in Tests (Strict CQRS)**:
   - Tests must **never** query write-side domain repositories (`IChatMessageRepository`, `IPeerContactRepository`, `IGroupInvitationRepository`, `IOutboxRepository`, `ITransferSessionRepository`, `IPeerProfileRepository`) to assert state.
   - All state verification in Phases 1–7 must go through **CQRS Query Ports** returning immutable `*ReadModel` projections:
     - `IChatMessageQueryService` (`ChatMessageReadModel`)
     - `IConversationListQueryService` (`ConversationSummaryReadModel`)
     - `IGroupInvitationQueryService` (`GroupInvitationSummaryReadModel`)
     - `IPeerContactQueryService` (`PeerContactSummaryReadModel`, `PeerContactDetailReadModel`)
     - `IPeerProfileQueries` (`PeerProfileReadModel`)
     - `IOutboxQueryService` (`OutboxJobSummaryReadModel`)
     - `IFileTransferQueryService` (`TransferSessionReadModel`, `ManifestFileReadModel`)
2. **Drive Actions Exclusively Through Driving Ports**:
   - Tests must invoke application and plugin behavior through driving interfaces (`IChatService`, `IHandshakeService`, `IContactRequestCoordinator`, `IGroupKeyDistributionService`, `IFileTransferActionHandler`, `ProfileManager`) rather than mutating domain aggregates or repositories directly during test execution.
3. **No Primitive Obsession or Ad-Hoc Delegate Injection**:
   - Use domain value objects (`PublicIdentityId`, `DeviceId`, `ChannelId`, `EndpointHost`, `EndpointPort`, `DirectEndpointCandidate`, `RelayPipeEndpointUri`, `BlindedRoutingToken`, `DeliveryToken`, `BlindedProfileLocator`, `ProfileWriteToken`, `ProfileKey`, `BlobId`, `BlobReference`, `TransferSessionId`, `RootDirectoryPath`, `SanitizedRelativePath`) instead of raw primitives.
   - Never inject `Func<...>` callbacks into application services to bypass hexagonal ports; always depend on domain/application port interfaces (`IRoutingCoordinator`, `ISessionWirePacker`, `ISealedEnvelopeUnwrapper`, `IProfileDeliveryClient`).

### Signal Protocol, Privacy & Hot-Path Invariants
1. **Asynchronous X3DH (`X3dhAgreement` + `PreKeyBundle`)**:
   - Initiator verifies the responder's Ed25519 signature over `SignedPreKey` (`VerifyEd25519Signature`), generates an ephemeral X25519 keypair (`EK_A`), and computes 3 or 4 DH shared secrets (`DH1 = DH(IK_A, SPK_B)`, `DH2 = DH(EK_A, IK_B)`, `DH3 = DH(EK_A, SPK_B)`, optional `DH4 = DH(EK_A, OPK_B)`) to derive the 32-byte `MasterSecret`.
   - Initial ciphertexts in `HandshakeService.InitiateHandshakeAsync` step the sending chain (`counter = 0`) and encrypt `[AppId (1B)][Payload]` under `MessageKey.Expand(0)`.
2. **Double Ratchet (`DirectRatchetSession`)**:
   - Each 1:1 message steps the KDF sending chain (`StepSendingChain`) to yield a single-use `MessageKey` and `RatchetHeader(EphemeralPublicKey, Counter, PreviousChainLength)`.\
   - `MessageKey.Expand(counter)` deterministically derives a 32-byte AES-256-GCM key and 12-byte nonce via HKDF-SHA256 (`Percolator.Domain.Security.ValueObjects.AeadNonce`).
   - AES-GCM Associated Data (AAD) for 1:1 messages is `header.EphemeralPublicKey.Span` (32 bytes).
   - When a peer replies, `InboundIngressPipeline.ProcessDirectAsync` detects `session.RemoteEphemeralPublicKey != direct.Header.EphemeralPublicKey` and executes `session.StepDhRatchet(...)`, advancing the root key and generating a fresh local ephemeral keypair.
   - **Hot-path**: All ratchet state mutations happen inside `IChannelLockService.AcquireLockAsync(channelId)`, `MessageKey` is disposed immediately after AEAD encryption/decryption, and `InboundIngressPipeline` zeroizes the decrypted plaintext buffer (`Array.Clear`) in `DispatchPlaintextAsync`.
3. **Signal Sender Keys + Groups v2 (`GroupSenderKeyRatchet`, `GroupReceiverSession`, `RelayGroupLedger`)**:
   - Each group member holds their own `GroupSenderKeyRatchet` (`ChainKey`, `Iteration`, `KeyId`, `AuthorSigningPublicKey`) and distributes it via `IGroupKeyDistributionService.DistributeSenderKeyAsync` over **pairwise 1:1 Double Ratchet sessions** (`AppId.SystemControl = 0x00`, 89-byte big-endian payload).
   - Outbound group broadcasts step the sender's symmetric KDF chain (`ratchet.Advance`), encrypt `[AppId][Payload]` with `MessageKey.Expand(iteration)` (empty AAD), and Ed25519-sign the ciphertext (`ratchet.SignPayload`).
   - **Privacy**: The relay (`RelayGroupLedger`) never sees group plaintext, sender identities, or member rosters; it verifies a zero-knowledge membership presentation (`RelayGroupLedger.VerifyDispatch(ZkPresentationBytes, envelopeCiphertext, IZkProofEngine)`) and fans out to the ledger's `ActiveRoutingTokens` (`IReadOnlySet<BlindedRoutingToken>`).
4. **Sealed / Blinded Relay Queuing (`RelayMailboxQueue` & `ISealedEnvelopeUnwrapper`)**:
   - Recipients register `(BlindedRoutingToken, DeliveryToken, DrainToken)` on `RelayMailboxQueue`.
   - Senders enqueue `MailboxEnvelope(EnvelopeId, BlindedRoutingToken, Ciphertext, ExpiresAtUtc)` using only `BlindedRoutingToken` + `DeliveryToken`. The Relay sees **zero** sender identity or `ChannelId` metadata on `MailboxEnvelope`.
   - Only the recipient holding `DrainToken` can call `TryDrainForToken` and unwrap the inner sender/channel envelope via `ISealedEnvelopeUnwrapper`.
5. **Zero-Knowledge Profile Capabilities & Unauthenticated Hosting (`RelayHostedProfileSlot`, `ProfileKey`, `ProfileWriteToken`)**:
   - **Symmetric Capability Model**: Profile encryption and authentication use symmetric AES-256-GCM under a 32-byte `ProfileKey` distributed exclusively over pairwise Double Ratchet sessions. No public asymmetric signatures are used, guaranteeing cryptographic deniability and zero identity linkability.
   - **Zero-Knowledge Relay Hosting**: Relay hosts store profile packages indexed by `BlindedProfileLocator = HKDF(IdentityKey, ProfileKey, "PercolatorBlindedProfileLocatorV1", 32)` and authorize slot creation, updates, and deletion via `SHA256(ProfileWriteToken)`. The relay never learns user identities or plaintext bearer tokens.
   - **Traffic Analysis Resistance & Padding**: Relay hosted profile slots rigidly enforce `ExactPackageSizeBytes = 1040` (1024-byte padded payload + 16-byte AEAD tag) to eliminate size-based metadata leaks.
   - **Multi-Source Snapshot Storage & Local Nickname Immutability**: Clients track profile snapshots across distinct sources (`DirectP2P` vs `Relay`). Remote profile sync never overwrites user-assigned local contact nicknames in `IPeerContactRepository` / `IPeerContactQueryService`.
   - **Ephemeral Memory Hygiene**: All bearer keys and tokens (`ProfileKey`, `ProfileWriteToken`, `DeliveryToken`) implement `ISensitiveSecret : IDisposable` and zero out underlying memory buffers via `CryptographicOperations.ZeroMemory`.

---

## 2. Vertical Feature Slice Architecture

```
Percolator.ApplicationIntegrationTests/
├── Features/
│   ├── DirectMessaging/                     # Phase 1: 1:1 Direct Loopback Chat
│   │   ├── DirectChatScenarioTests.cs
│   │   └── DirectStreamTestFixtures.cs
│   ├── RelayedMessaging/                    # Phase 2: Relayed Handshake & Chat
│   │   ├── RelayedChatScenarioTests.cs
│   │   ├── RelayNode.cs                     # Dedicated autonomous relay node
│   │   ├── RelayMailboxQueueAdapter.cs
│   │   └── RelayedTransportDispatcher.cs
│   ├── ProfileExchange/                     # Phase 3: Direct, Relayed & Dynamic Profile Synchronization
│   │   ├── ProfileIntegrationScenarioTests.cs
│   │   ├── InMemoryRelayProfileRepository.cs
│   │   ├── LoopbackProfileDeliveryClient.cs
│   │   └── ProtobufProfileSerializerTests.cs
│   ├── GroupMessaging/                      # Phase 4: Relayed Group Chat & Sender Keys
│   │   ├── GroupChatScenarioTests.cs
│   │   ├── RelayGroupLedgerAdapter.cs
│   │   └── ZkPresentationTestDouble.cs
│   ├── MediaAttachments/                    # Phase 5: Encrypted Media Attachments
│   │   ├── MediaAttachmentScenarioTests.cs
│   │   └── RelayBlobStoreAdapter.cs
│   ├── PeerDiscovery/                       # Phase 6: Sovereign Directory & Rendezvous
│   │   ├── PeerDiscoveryScenarioTests.cs
│   │   └── RelayPeerDirectoryAdapter.cs
│   └── BulkFileTransfer/                    # Phase 7: Multi-Stream Swarm File Transfer
│       ├── FileTransferScenarioTests.cs
│       └── LoopbackStreamPoolAdapter.cs
├── Common/
│   ├── Builders/
│   │   ├── TestClusterBuilder.cs            # Fluent builder with AddNode & AddRelayNode
│   │   ├── TestIdentityNode.cs              # Client participant node
│   │   └── RelayNode.cs                     # Autonomous infrastructure relay node
│   ├── Infrastructure/
│   │   ├── EndpointPort.cs
│   │   ├── InMemoryLoopbackNetworkSwitch.cs
│   │   ├── ScenarioCryptoEngine.cs
│   │   ├── SealedWireFraming.cs
│   │   ├── TestDateTimeProvider.cs
│   │   └── TestPayloadSerializer.cs
│   ├── Adapters/
│   │   ├── InMemoryRepositories.cs
│   │   ├── LoopbackStreamRegistry.cs
│   │   ├── LoopbackTransportDispatcher.cs
│   │   ├── SealedEnvelopeUnwrapper.cs
│   │   ├── DefaultSessionWirePacker.cs
│   │   └── Queries/
│   │       └── InMemoryQueryAdapters.cs
│   └── Routing/
│       ├── TestPeerReachabilityService.cs
│       └── DeterministicChannelId.cs        # Deterministic 1:1 ChannelId generator
```

---

## 3. Prerequisite Application & Domain Fixes (Critical Review Findings)

Prior to implementing Phase 2 and subsequent phases, the following core defects must be addressed:

### 3.1 Deterministic Direct Channel Creation in Handshake Flow (`Percolator.Application`)
- **Identified Failure Mode**:
  - In Phase 1, `EstablishDirectSessionWithAsync` artificially generated a `ChannelId.New()` and inserted `DirectChannel` records directly into both nodes' `IChannelRepository`.
  - In Phase 2, Alice calls `HandshakeService.InitiateHandshakeAsync`. `HandshakeService` creates a `DirectRatchetSession`, but **never** provisions a `DirectChannel` in `IChannelRepository`.
  - When Charlie receives and completes the handshake (`HandshakeService.ProcessActiveInvitationAsync`), it passes `ChannelId.New()` in `InboundPayloadContext`.
  - When `ChatPayloadHandler.HandleInboundAsync` executes, it compares `dto.ChannelId != context.ChannelId` and fails with `CHANNEL_MISMATCH`, and `_channelRepo.GetDirectByIdAsync(context.ChannelId)` returns null.
- **Architectural Solution**:
  1. Inject `IChannelRepository` into `HandshakeService`.
  2. Use deterministic channel derivation for 1:1 direct channels: `DeterministicChannelId.ForDirect(aliceId, peerId)` (sorting the pair of `PublicIdentityId`s lexicographically so both nodes independently derive the identical `ChannelId`).
  3. In `InitiateHandshakeAsync`, create and save the `DirectChannel` in `IChannelRepository` if not already present.
  4. In `ProcessActiveInvitationAsync`, create and save the `DirectChannel` for the recipient before dispatching the initial greeting payload to `IAppRouter`.

### 3.2 Relayed Delivery Routing & Transport Dispatcher Support (`Percolator.ApplicationIntegrationTests`)
- **Identified Failure Mode**:
  - `LoopbackTransportDispatcher` only handles `DeliveryRouteType.DirectP2P`.
  - When Alice's outbox processes a job for Charlie (`DeliveryRouteType.RelayedOneToOne`), `LoopbackTransportDispatcher` returns `UNSUPPORTED_ROUTE`.
  - `TestIdentityNode` currently injects `routingCoordinator: null` into `HandshakeService`, defaulting all handshakes to `DirectP2P`.
- **Architectural Solution**:
  1. Implement `TestPeerReachabilityService` that knows which nodes have direct streams vs which use a home relay (`localhost:15003`).
  2. Wire `RoutingCoordinator` with `TestPeerReachabilityService` into `TestIdentityNode`.
  3. Extend `LoopbackTransportDispatcher` to handle `DeliveryRouteType.RelayedOneToOne`:
     - Resolves the recipient's `BlindedRoutingToken` and `DeliveryToken`.
     - Packages the sealed frame into a `MailboxEnvelope(EnvelopeId.New(), recipientToken, sealedBytes, expiresAtUtc)`.\
     - Enqueues directly to the target relay's `RelayMailboxQueue.Enqueue(envelope, deliveryToken, timeProvider)`.

### 3.3 Autonomous Relay Node Representation (`TestClusterBuilder`)
- **Identified Failure Mode**:
  - `TestIdentityNode` creates client applications with user identities, chat services, and contact managers.
  - A Relay node is an autonomous infrastructure server, not an end-user client.
- **Architectural Solution**:
  - Add `RelayNode` to `TestClusterBuilder` via `.AddRelayNode(\"Relay\", port, policy)`.
  - `RelayNode` encapsulates:
    - `RelayMailboxQueue`
    - `IRelayPreKeyRepository` (holding `RelayHostedPreKeyBundle`s)
    - `IRelayProfileRepository` (holding `RelayHostedProfileSlot` entries indexed by `BlindedProfileLocator`)
    - `IRelayLedgerRepository` (holding `RelayGroupLedger`s)
    - `InMemoryRelayPeerDirectory` (for Phase 6 discovery)
    - In-memory relay blob store (for Phase 5 attachments)

### 3.4 Wire Framing Cleanliness & Single Discriminator (`SealedWireFraming` & `DefaultSessionWirePacker`)
- **Identified Failure Mode**:
  - `DefaultSessionWirePacker` prepends `WireFrameKind` byte, and `SealedWireFraming` also prepends `WireFrameKind` byte.
  - This creates duplicate headers that risk off-by-one errors when processing relayed envelopes.
- **Architectural Solution**:
  - Standardize `SealedWireFraming` as the outer framing envelope for transport dispatch and loopback switch.
  - The outer frame contains `[WireFrameKind (1B)][ChannelId (16B)][SenderIdentityId (16B)][SenderDeviceId (16B)][PayloadLength (4B)][PackedSessionWireBytes]`.
  - For store-and-forward privacy over relay, the relay sees only `MailboxEnvelope.Ciphertext = sealedWireBytes` without inspecting inner fields.

### 3.5 Media Attachment Blob Reference Preservation (`Percolator.Apps.Chat` + `Percolator.Application`)
- Add `BlobReference? OutOfBandReference = null` to `MediaAttachmentDto` and preserve it in `ChatMediaService.UploadOutOfBandAttachmentAsync`.
- Add `DownloadOutOfBandAttachmentAsync(MediaAttachmentDto attachment, CancellationToken ct = default)` to `ChatMediaService`.
- When `StorageMode == BlobStorageMode.DirectP2P`, resolve peer and invoke `_peerStreamBlobClient.RequestBlobFromPeerAsync`.

### 3.6 Allow `TransferSessionCoordinator` Seeder Pump in `Uploading` State (`Percolator.Apps.FileTransfer`)
- Fix line 345 in `TransferSessionCoordinator.PumpSingleStreamAsync` so `isDownloadDone` is `session.IsDownloadComplete && session.State is not (TransferSessionState.Uploading or TransferSessionState.Seeding)`.

---\

## 4. Decomposed Multi-Step Orchestration by Phase

### Phase 1: 2 Identities (`Alice` & `Bob`) — Direct Connection & 1:1 Double Ratchet Chat
- **Step 1.1**: Build cluster with `Alice` (`15001`) and `Bob` (`15002`) over loopback switch.
- **Step 1.2**: Exchange invitation links out-of-band and open direct loopback stream.
- **Step 1.3**: Establish pairwise Double Ratchet session and direct channel.
- **Step 1.4**: Alice sends 1:1 chat message over `DirectP2P`; assert immediate delivery via fast-path stream write (`IStreamRegistry.TryWriteAsync`).
- **Step 1.5**: Bob asserts inbound message via `ChatQueries` and conversation unread count via `ConversationQueries`.
- **Step 1.6**: Bob replies; assert ratchet turnaround (`StepDhRatchet`) and causal ordering on Alice.

### Phase 2: 3rd (`Relay`) & 4th Identity (`Charlie`) — Relayed Handshake & Chat
- **Step 2.1: Relay Provisioning & Charlie Registration**:
  - Build cluster with `Relay` (`localhost:15003`, `RelayNode`) and `Charlie` (`localhost:15004`, client node).
  - Charlie registers `(MailboxRoutingToken, MailboxDeliveryToken, MailboxDrainToken)` in `Relay.MailboxQueue`.
  - Charlie publishes `RelayHostedPreKeyBundle` with 5 one-time prekeys to `Relay`.
- **Step 2.2: Alice PreKey Consumption & Handshake Initiation**:
  - Alice queries `Relay` and consumes Charlie's `PreKeyBundle` (asserting OTPK count decrements from 5 to 4).
  - Alice creates deterministic `DirectChannel(aliceId, charlieId)` and initiates handshake with initial greeting (`ChatPayloadEnvelopeDto: \"Hi Charlie, reaching out via Relay!\"`).
  - Handshake package is saved to Alice's outbox with route `RelayedOneToOne(TargetRelayId = relay.Id, TargetToken = charlie.MailboxRoutingToken)`.
- **Step 2.3: Relayed Outbox Dispatch**:
  - Alice runs `OutboxWorker.ProcessBatchAsync()`.
  - `LoopbackTransportDispatcher` packages the sealed handshake into `MailboxEnvelope` and enqueues on `Relay.MailboxQueue.Enqueue(envelope, charlie.MailboxDeliveryToken, timeProvider)`.
  - Assert `Alice.OutboxQueries.GetPendingJobsAsync()` is empty and job status is `Delivered`.
  - Assert `Relay.MailboxQueue.TotalCount` is 1 and envelope has zero sender metadata.
- **Step 2.4: Charlie Mailbox Drain & Pending Request Reception**:
  - Charlie drains `Relay.MailboxQueue.TryDrainForToken(charlie.MailboxRoutingToken, charlie.MailboxDrainToken)`.
  - Charlie unwraps the envelope via `ISealedEnvelopeUnwrapper` and feeds to `IngressPipeline.ProcessInboundAsync`.
  - Assert `Charlie.ContactQueries.GetContactDetailAsync(charlie.Id, alice.Id)` has `State == ContactState.PendingApproval`.
  - Assert greeting message is NOT yet visible in `Charlie.ChatQueries.GetMessagesAsync` (held in pending handshake store).
- **Step 2.5: Charlie Consent Approval & Handshake Completion**:
  - Charlie calls `ContactRequestCoordinator.ApproveRequestAsync(charlie.Id, alice.Id)`.
  - Charlie calls `HandshakeService.CompletePendingHandshakeAsync(charlie.Id, alice.Id)`.
  - Charlie derives master secret, provisions local `DirectChannel`, decrypts initial greeting, and routes to `ChatPayloadHandler`.
  - Assert `Charlie.ContactQueries` shows `ContactState.Approved`.
  - Assert `Charlie.ChatQueries.GetMessagesAsync` contains `\"Hi Charlie, reaching out via Relay!\"`.
- **Step 2.6: Bidirectional Relayed Chat**:
  - Charlie sends reply `\"Hello Alice via Relay!\"` using route `DeliveryRoute.RelayedOneToOne`.
  - Charlie's outbox dispatches envelope to `Relay.MailboxQueue`.
  - Alice drains `Relay.MailboxQueue`, processes inbound envelope, and asserts Charlie's reply in `Alice.ChatQueries`.

### Phase 3: Profile Capability Synchronization (Direct P2P, Relayed Hosting, and Revision Updates)
- **Step 3.1: Direct P2P Profile Exchange & In-Place Revision Update**:
  - Alice and Bob establish direct connection over loopback switch (`localhost:15001` and `localhost:15002`).
  - Alice publishes initial profile (`rev: 1`, DisplayName: `"Alice Wonderland"`, Bio: `"Curiouser and curiouser!"`) locally.
  - Alice dispatches profile capability to Bob over direct P2P stream (`DeliveryRoute.DirectP2P`).
  - Bob fetches unauthenticated from Alice's profile endpoint, decrypts AEAD package with `ProfileKey`, and records profile in `PeerProfileRepo`.
  - Bob records snapshot in multi-source storage with `ProfileSourceType.DirectP2P` and endpoint `localhost:15001`.
  - Strict CQRS: `Bob.PeerProfileQueries.GetProfileAsync(alice.Id)` projects updated display name, bio, and `rev: 1`.
  - Local Nickname Immutability: Assert Bob's user-assigned local contact nickname for Alice remains intact in `Bob.ContactQueries.GetContactDetailAsync`.
  - Conditional Fetch: Alice re-sends identical capability (`rev: 1`); Bob skips redundant network fetch.
  - Revision Update: Alice updates profile to `rev: 2` (DisplayName: `"Alice Liddell"`, Bio: `"Through the Looking-Glass"`) and re-shares; Bob detects higher revision and performs in-place atomic update.
- **Step 3.2: Zero-Knowledge Relayed Profile Hosting & Mailbox Capability Dispatch**:
  - Build cluster with Alice, Charlie, and zero-knowledge `RelayNode` (`localhost:15003`).
  - Alice publishes encrypted profile package to `RelayNode` (`RelayHostedProfileSlot`) using `ProfileWriteToken`.
  - Relay asserts rigid 1040-byte constant package size (1024-byte padded payload + 16-byte AEAD tag) and hashes write token via SHA-256 for zero-knowledge authorization.
  - Alice dispatches profile capability to Charlie via `DeliveryRoute.RelayedOneToOne` through Charlie's mailbox.
  - Alice outbox worker dispatches envelope to `Relay.MailboxQueue`.
  - Charlie drains mailbox via `TryDrainForToken` with secret `DrainToken` and unseals profile capability.
  - Charlie fetches 1040-byte package from `RelayNode` unauthenticated using `BlindedProfileLocator`, decrypts with `ProfileKey`, and persists to `PeerProfileRepo`.
  - Multi-source profile snapshot recorded with `ProfileSourceType.Relay` and endpoint `"relay"`.
  - Strict CQRS: `Charlie.PeerProfileQueries.GetProfileAsync(alice.Id)` returns relayed profile metadata; Charlie's local contact nickname remains preserved.
- **Step 3.3: Profile Key Rotation, Cryptographic Revocation, and Slot Deletion**:
  - Alice shares initial profile (`rev: 1`) with both Bob and Charlie.
  - Alice revokes Bob by rotating her `ProfileKey`.
  - Alice deletes old profile slot on `RelayNode` presenting stale `ProfileWriteToken`.
  - Alice derives new `BlindedProfileLocator` and `ProfileWriteToken` from fresh `ProfileKey` and publishes updated profile (`rev: 2`) to `RelayNode`.
  - Alice shares fresh capability exclusively with approved contact Charlie via pairwise Double Ratchet.
  - Charlie successfully decrypts and updates profile to `rev: 2`.
  - Assert cryptographic revocation guarantees against Bob:
    1. Bob attempting to fetch the old locator from `RelayNode` receives `PROFILE_NOT_FOUND`.
    2. Bob attempting to overwrite or delete using stale write token is rejected (`UNAUTHORIZED_PROFILE_WRITE`).
    3. Even if Bob intercepted the new locator, Bob's stale `ProfileKey` fails AEAD decryption.
    4. Relays reject envelopes presented with invalid/revoked `DeliveryToken`.

### Phase 4: Group Chat (`Alice`, `Bob`, `Charlie`) Hosted on `Relay` [COMPLETED in GroupChatScenarioTests.cs]
- **Step 4.1: Group Creation & Ledger Registration**:
  - Alice creates `GroupChannel(\"Percolator Builders\")` with `GroupMasterKey`.
  - Alice registers `RelayGroupLedger` on `Relay` with `ActiveRoutingTokens` for Alice, Bob, and Charlie.
- **Step 4.2: Pairwise Invitation & Acceptance**:
  - Alice sends invitations: Bob over `DirectP2P`, Charlie over `RelayedOneToOne`.
  - Bob and Charlie query `GroupInvitationQueries.GetPendingInvitationsAsync()` and accept.
- **Step 4.3: Pairwise Sender Key Distribution**:
  - Alice, Bob, and Charlie each call `GroupKeyDistributionService.DistributeSenderKeyAsync(...)`.
  - Keys are distributed pairwise via 1:1 Double Ratchet channels (`AppId.SystemControl = 0x00`).
  - Recipients process inbound system messages and import sender keys into `IGroupReceiverSessionRepository`.
- **Step 4.4: Zero-Knowledge Relayed Broadcast**:
  - Alice broadcasts `\"Welcome to the group, Bob and Charlie!\"` via `ChatService.SendMessageAsync(Route: DeliveryRoute.RelayedGroup)`.
  - Dispatcher computes ZK proof / dispatch challenge and submits `(channelId, proof, sealedPayload)` to `Relay`.
  - `Relay.RelayGroupLedger.VerifyDispatch` verifies proof and fans out opaque `MailboxEnvelope`s to Bob and Charlie's routing tokens.
  - Bob and Charlie drain/receive the broadcast, decrypt with Alice's sender key, and verify Alice's author signature.

### Phase 5: Chat Media & Out-of-Band Encrypted Attachment Storage
- **Step 5.1**: Alice uploads 128 KB binary payload via `ChatMediaService.UploadOutOfBandAttachmentAsync`.
- **Step 5.2**: Relay blob store stores encrypted chunks and returns `BlobReference`.
- **Step 5.3**: Alice sends chat message with attachment metadata and `BlobReference`.
- **Step 5.4**: Bob and Charlie download attachment via `ChatMediaService.DownloadOutOfBandAttachmentAsync`.
- **Step 5.5**: Verify byte-for-byte fidelity and AEAD tag verification.

### Phase 6: Peer Discovery (`Apps.Discovery` — Sovereign Directory & Rendezvous)
- **Step 6.1: Directory Visibility Registration**:
  - Bob registers discoverable profile with `Relay` via `DiscoveryOpcode.RegisterVisibility`.
- **Step 6.2: Nearby Discovery Workflow**:
  - Dave queries `Relay` via `NearbyDiscoveryWorkflow`, discovers Bob, retrieves `PreKeyBundle`, and initiates handshake.
- **Step 6.3: Blinded Rendezvous Matching**:
  - Alice and Dave derive rotating epoch locators via `PrivateRendezvousService.DeriveLocator`.
  - Alice publishes rendezvous route; Dave queries window, matches, and establishes direct session.

### Phase 7: Multi-Stream Swarm Bulk File Transfer (`Apps.FileTransfer`)
- **Step 7.1**: Alice stages 3.5 MB file (4 x 1 MB chunks) in `IDesktopFileStorage`.
- **Step 7.2**: Alice initiates transfer via `FileTransferActions.InitiateTransferAsync`.
- **Step 7.3**: Bob receives in-band offer and calls `AcceptTransferAsync`.
- **Step 7.4**: Concurrently pump multi-stream chunks across connected stream pool.
- **Step 7.5**: Bob verifies completed manifest and byte-for-byte SHA256 integrity.
