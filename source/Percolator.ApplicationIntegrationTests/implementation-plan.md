# Implementation Plan: `Percolator.ApplicationIntegrationTests` (Phased Multi-Identity Suite)

## 1. Overview & Architectural Principles
Build a progressive, 6-phase integration test suite in `Percolator.ApplicationIntegrationTests` (`RootNamespace: Percolator.Application2IntegrationTests`) where a fluent **Identity & Cluster Builder** spins up isolated nodes bound to distinct `localhost:<port>` endpoints (`localhost:15001`..`localhost:15005`) over an in-memory loopback network switch.

### Strict DDD, Hexagonal (Ports & Adapters), and CQRS Rules
1. **No Repository Reads in Tests (Strict CQRS)**:
   - Tests must **never** query write-side domain repositories (`IChatMessageRepository`, `IPeerContactRepository`, `IGroupInvitationRepository`, `IOutboxRepository`, `ITransferSessionRepository`) to assert state.
   - All state verification in Phases 1–6 must go through **CQRS Query Ports** returning immutable `*ReadModel` projections:
     - `IChatMessageQueryService` (`ChatMessageReadModel`)
     - `IConversationListQueryService` (`ConversationSummaryReadModel`)
     - `IGroupInvitationQueryService` (`GroupInvitationSummaryReadModel`)
     - `IPeerContactQueryService` (`PeerContactSummaryReadModel`, `PeerContactDetailReadModel`)
     - `IOutboxQueryService` (`OutboxJobSummaryReadModel`)
     - `IFileTransferQueryService` (`TransferSessionReadModel`, `ManifestFileReadModel`)
2. **Drive Actions Exclusively Through Driving Ports**:
   - Tests must invoke application and plugin behavior through driving interfaces (`IChatService`, `IHandshakeService`, `IContactRequestCoordinator`, `IGroupKeyDistributionService`, `IFileTransferActionHandler`) rather than mutating domain aggregates or repositories directly during test execution.
3. **No Primitive Obsession or Ad-Hoc Delegate Injection**:
   - Use domain value objects (`PublicIdentityId`, `DeviceId`, `ChannelId`, `EndpointHost`, `EndpointPort`, `DirectEndpointCandidate`, `RelayPipeEndpointUri`, `BlindedRoutingToken`, `DeliveryToken`, `BlobId`, `BlobReference`, `TransferSessionId`, `RootDirectoryPath`, `SanitizedRelativePath`) instead of raw primitives.
   - Never inject `Func<...>` callbacks into application services to bypass hexagonal ports; always depend on domain/application port interfaces (`IRoutingCoordinator`, `ISessionWirePacker`, `ISealedEnvelopeUnwrapper`).

### Signal Protocol, Privacy & Hot-Path Invariants
1. **Asynchronous X3DH (`X3dhAgreement` + `PreKeyBundle`)**:
   - Initiator verifies the responder's Ed25519 signature over `SignedPreKey` (`VerifyEd25519Signature`), generates an ephemeral X25519 keypair (`EK_A`), and computes 3 or 4 DH shared secrets (`DH1 = DH(IK_A, SPK_B)`, `DH2 = DH(EK_A, IK_B)`, `DH3 = DH(EK_A, SPK_B)`, optional `DH4 = DH(EK_A, OPK_B)`) to derive the 32-byte `MasterSecret`.
   - Initial ciphertexts in `HandshakeService.InitiateHandshakeAsync` step the sending chain (`counter = 0`) and encrypt `[AppId (1B)][Payload]` under `MessageKey.Expand(0)`.
2. **Double Ratchet (`DirectRatchetSession`)**:
   - Each 1:1 message steps the KDF sending chain (`StepSendingChain`) to yield a single-use `MessageKey` and `RatchetHeader(EphemeralPublicKey, Counter, PreviousChainLength)`.
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

---

## 2. Prerequisite Application & Plugin Layer Fixes

*(Note: Deterministic AEAD nonce derivation via `MessageKey.Expand(uint counterOrIteration)` is already implemented in `Percolator.Domain` and wired into `OutboundEgressPipeline` and `InboundIngressPipeline`.)*

Before writing the Phase 1–6 tests, apply these **4 minimal fixes** adhering strictly to our hexagonal ports and value objects:

### 2.1 Preserve `BlobReference` in `MediaAttachmentDto` & Add `DownloadOutOfBandAttachmentAsync` (`Percolator.Apps.Chat` + `Percolator.Application`)
- **Current Gap**:
  - `ChatMediaService.UploadOutOfBandAttachmentAsync` (`Percolator.Apps.Chat/Features/Media/ChatMediaService.cs`) calls `IBlobStorageService.UploadBlobAsync` and gets `BlobUploadResult(BlobReference Reference, BlobStorageMode StorageMode)`, but discards `Reference` (`EncryptionKey`, `BaseNonce`, `CiphertextSha256`, `CiphertextSizeBytes`, `StorageMode`, `RelayEndpointUri`) and only stores `BlobId` on `MediaAttachmentDto`.
  - `ChatMediaService` has no download method.
  - `BlobStorageService.DownloadBlobAsync` (`Percolator.Application/Services/BlobStorageService.cs:240`) injects `IPeerStreamBlobClient` (`_peerStreamBlobClient`) + `IChannelRepository` (`_channelRepo`) but returns `"P2P_DOWNLOAD_NOT_FOUND"` instead of calling `_peerStreamBlobClient.RequestBlobFromPeerAsync` when `StorageMode == BlobStorageMode.DirectP2P`.
- **Fix**:
  1. Add `BlobReference? OutOfBandReference = null` to `MediaAttachmentDto` (`Percolator.Apps.Chat/Features/Media/MediaAttachmentDto.cs`) and populate `OutOfBandReference: uploadResult.Value.Reference` in `ChatMediaService.UploadOutOfBandAttachmentAsync`.
  2. Add `DownloadOutOfBandAttachmentAsync(MediaAttachmentDto attachment, CancellationToken ct = default)` to `ChatMediaService` that validates `attachment.OutOfBandReference`, calls `_blobStorage.DownloadBlobAsync(new BlobDownloadRequest(attachment.OutOfBandReference, ct), ct: ct)`, and verifies the decrypted stream length matches `attachment.ByteSize`.
  3. In `BlobStorageService.DownloadBlobAsync`, when `request.Reference.StorageMode == BlobStorageMode.DirectP2P` and the blob is not in `_localChunkStore`, resolve the remote peer via `IChannelRepository` / `request.Reference` and invoke `_peerStreamBlobClient.RequestBlobFromPeerAsync`.

### 2.2 Resolve Pairwise `DirectChannel` & `DeliveryRoute` via `IChannelRepository` and `IRoutingCoordinator` in `GroupKeyDistributionService` (`Percolator.Application`)
- **Current Gap**:
  - `GroupKeyDistributionService.DistributeSenderKeyAsync` (`Percolator.Application/Services/GroupKeyDistributionService.cs:86-93`) hardcodes `DeliveryRoute.DirectP2P` and passes the group's `channelId` in `OutboundPayloadContext`.
- **Fix (Hexagonal Port Injection, No Delegates)**:
  - Inject optional `IChannelRepository? channelRepo = null` and `IRoutingCoordinator? routingCoordinator = null` into `GroupKeyDistributionService`.
  - For each `recipientId`, resolve the pairwise 1:1 `DirectChannel` via `_channelRepo.GetDirectByPeerAsync(senderId, recipientId, ct)` (using `directChannel.Id` as `OutboundPayloadContext.ChannelId` so 1:1 channel locks don't contend with the group channel lock) and resolve the route via `_routingCoordinator.ResolveRouteAsync(recipientId, ct)` (`DeliveryRoutingMode.RelayMailbox` -> `DeliveryRoute.RelayedOneToOne`, `DeliveryRoutingMode.DirectPeer` -> `DeliveryRoute.DirectP2P`).

### 2.3 Add Strongly-Typed `HandshakeHeader` Packing to `ISessionWirePacker` (`Percolator.Domain` + `Percolator.Application`)
- **Current Gap**:
  - `HandshakeService.InitiateHandshakeAsync` (`Percolator.Application/Handshake/HandshakeService.cs:140-145`) manually copies `[EphemeralPublicKey (32B)][EncryptedInitialPayload]` using `Buffer.BlockCopy`, violating `ISessionWirePacker` encapsulation and omitting `SenderIdentityKey`, `SignedPreKeyId`, and `OneTimePreKeyId` needed for `InboundHandshakeEnvelope`.
- **Fix**:
  - Define a `HandshakeHeader(IdentityKey SenderIdentityKey, DhPublicKey SenderEphemeralKey, uint SignedPreKeyId, uint? OneTimePreKeyId)` value object in `Percolator.Domain.Security.ValueObjects`.
  - Add `PackHandshakeMessage(HandshakeHeader header, ReadOnlyMemory<byte> encryptedPayload)` and `UnpackHandshakeMessage(PublicIdentityId recipientId, PublicIdentityId senderId, DeviceId senderDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc)` to `ISessionWirePacker` (`Percolator.Application/Ports/ISessionWirePacker.cs`).
  - Update `HandshakeService.InitiateHandshakeAsync` to call `_wirePacker.PackHandshakeMessage(...)` and resolve the outbound route via `IRoutingCoordinator` (falling back to `DirectP2P` if not injected).

### 2.4 Allow `TransferSessionCoordinator` Seeder Pump in `Uploading` State (`Percolator.Apps.FileTransfer`)
- **Current Gap**:
  - In `TransferSessionCoordinator.PumpSingleStreamAsync` (`Percolator.Apps.FileTransfer/Features/MultiStreamEngine/TransferSessionCoordinator.cs:345-351`), a seeder has `session.IsDownloadComplete == true`. When `HandleAcceptAsync` transitions the sender's session from `WaitingForPeer` to `TransferSessionState.Uploading`, `isDownloadDone` evaluates `session.IsDownloadComplete && session.State != TransferSessionState.Seeding` (`true`), causing the sender's stream pump to exit immediately before reading `RequestChunkFrame`s from the receiver.
- **Fix**:
  - Update line 345 in `TransferSessionCoordinator.PumpSingleStreamAsync` so `isDownloadDone` is `session.IsDownloadComplete && session.State is not (TransferSessionState.Uploading or TransferSessionState.Seeding)`.

---

## 3. Fluent Builder Architecture (`TestIdentityNode`, `TestClusterBuilder`, `InMemoryLoopbackNetworkSwitch`)

### 3.1 `TestIdentityNode` (`Percolator.ApplicationIntegrationTests/Builders/TestIdentityNode.cs`)
Each node built by `TestIdentityBuilder` encapsulates an isolated Hexagonal application instance bound to an `EndpointPort` (`15001`..`15005`):
- **Identity & Value-Object Addressing**:
  - `string Name`, `EndpointPort Port`, `DirectEndpointCandidate LocalEndpointCandidate` (`localhost:{Port}`)
  - `PublicIdentityId IdentityId`, `DeviceId DeviceId = DeviceId.Primary`
  - `IdentityKey IdentityPublicKey`, `EphemeralPrivateKey IdentityPrivateKey`
  - `DhPublicKey SignedPreKeyPublic`, `EphemeralPrivateKey SignedPreKeyPrivate`, `DeviceLinkProof SignedPreKeySignature`
  - `BlindedRoutingToken MailboxRoutingToken`, `DeliveryToken MailboxDeliveryToken`, `DeliveryToken MailboxDrainToken`
  - `InvitationLink CreateInvitationLink()` -> `new InvitationLink("localhost", Port.Value, Convert.ToHexString(IdentityPublicKey.Span))`
  - `PreKeyBundle CreatePreKeyBundle(uint? oneTimeKeyId = null)`
- **Exposed Driving Ports (Commands / Actions)**:
  - `IChatService ChatService`
  - `ChatMediaService ChatMediaService`
  - `GroupInvitationService GroupInvitationService`
  - `IHandshakeService HandshakeService`
  - `IContactRequestCoordinator ContactRequestCoordinator`
  - `IGroupKeyDistributionService GroupKeyDistributionService`
  - `NearbyPeerDiscoveryWorkflow NearbyDiscoveryWorkflow`
  - `IFileTransferActionHandler FileTransferActions`
  - `TransferSessionCoordinator FileTransferCoordinator`
  - `OutboxWorker OutboxWorker`
  - `InboundIngressPipeline IngressPipeline`
- **Exposed CQRS Query Ports (Read-Only Assertions — Used by Tests)**:
  - `IChatMessageQueryService ChatQueries` (`GetMessagesAsync` -> `IReadOnlyList<ChatMessageReadModel>`)
  - `IConversationListQueryService ConversationQueries` (`GetConversationsAsync` -> `IReadOnlyList<ConversationSummaryReadModel>`)
  - `IGroupInvitationQueryService GroupInvitationQueries` (`GetPendingInvitationsAsync` -> `IReadOnlyList<GroupInvitationSummaryReadModel>`)
  - `IPeerContactQueryService ContactQueries` (`GetContactSummariesAsync`, `GetContactDetailAsync` -> `PeerContactDetailReadModel`)
  - `IOutboxQueryService OutboxQueries` (`GetPendingJobsAsync`, `GetFailedJobsAsync`, `GetJobSummaryByIdAsync` -> `OutboxJobSummaryReadModel`)
  - `IFileTransferQueryService FileTransferQueries` (`GetAllTransfersAsync`, `GetTransferByIdAsync`, `GetManifestFilesAsync` -> `TransferSessionReadModel`, `ManifestFileReadModel`)
- **Internal Driven Adapters (Encapsulated Inside Node)**:
  - Write repositories (`IRatchetSessionRepository`, `IGroupSenderKeyRepository`, `IGroupReceiverSessionRepository`, `IChannelRepository`, `IOutboxRepository`, `IPeerContactRepository`, `IPendingHandshakeRepository`, `IChatMessageRepository`, `IGroupInvitationRepository`, `ITransferSessionRepository`) and in-memory query adapters projecting those stores into the `*ReadModel`s above.
- **Relay Hosting State (when `.AsRelayNode()` is configured on `localhost:15003`)**:
  - `RelayMailboxQueue`, `IRelayLedgerRepository`, `IRelayPreKeyRepository`, `RelayHostingPolicy`, `InMemoryRelayPeerDirectory`, `PrivateRendezvousService`, and an in-memory relay blob store backing `IRelayBlobClient`.

### 3.2 Stateless Privacy-Preserving Wire Framing (`ISessionWirePacker`, `ISealedEnvelopeUnwrapper`, `InMemoryLoopbackNetworkSwitch`)
To ensure `Relay` (`localhost:15003`) never sees cleartext sender or channel metadata on `MailboxEnvelope` and `InMemoryLoopbackNetworkSwitch` never relies on length heuristics or out-of-band test dictionaries:
1. **Explicit `WireFrameKind` Discriminator**:
   - Every inner transport envelope wrapped for `IStreamRegistry` or `MailboxEnvelope.Ciphertext` starts with a 53-byte inner sealed header decoded exclusively by the recipient's `ISealedEnvelopeUnwrapper`:
     - `[WireFrameKind (1B: 0x01=DirectRatchet, 0x02=GroupBroadcast, 0x03=X3dhHandshake)]`
     - `[ChannelId (16B)]`
     - `[SenderIdentityId (16B)]`
     - `[SenderDeviceId (16B)]`
     - `[PayloadLength (4B LE)]`
     - `[PackedSessionWireBytes (from ISessionWirePacker)]`
2. **Direct P2P Delivery (`DeliveryRouteType.DirectP2P`)**:
   - When Node A's `OutboundEgressPipeline` (via `IStreamRegistry.TryWriteAsync`) or `OutboxWorker` (via `ITransportDispatcher.SendAsync`) sends to Node B over an active `localhost:{port}` connection, `ISealedEnvelopeUnwrapper` on Node B decodes the `WireFrameKind`, delegates to `ISessionWirePacker.UnpackDirectRatchetMessage` or `UnpackHandshakeMessage`, and passes the resulting `InboundEnvelope` into `NodeB.IngressPipeline.ProcessInboundAsync(...)`.
3. **Relayed 1:1 Store-and-Forward (`DeliveryRouteType.RelayedOneToOne`)**:
   - `ITransportDispatcher` wraps the sealed frame into `MailboxEnvelope(EnvelopeId.New(), recipientBlindedRoutingToken, sealedBytes, expiresAtUtc)` and calls `Relay.MailboxQueue.Enqueue(envelope, recipientDeliveryToken, timeProvider)`.
   - `Relay` only sees `BlindedRoutingToken` and opaque `sealedBytes`.
   - When the recipient drains `Relay.MailboxQueue.TryDrainForToken(recipientBlindedRoutingToken, recipientDrainToken)`, the recipient passes each `MailboxEnvelope` to `ISealedEnvelopeUnwrapper.Unwrap(envelope)` and feeds the resulting `InboundEnvelope` into `IngressPipeline.ProcessInboundAsync(...)`.
4. **Relayed Group Broadcast (`DeliveryRouteType.RelayedGroup`)**:
   - `ISessionWirePacker.PackGroupMessage` produces `[Iteration (4B LE)][Ed25519 Signature (64B)][Ciphertext]`.
   - The sender's `ITransportDispatcher` computes `RelayGroupLedger.ComputeDispatchChallenge(sealedBytes, challengeSpan)`, generates a `ZkPresentationBytes` proof via `IZkProofEngine`, and submits `(channelId, proof, sealedBytes)` to `Relay` (`localhost:15003`).
   - `Relay` loads `RelayGroupLedger` for `channelId`, verifies `ledger.VerifyDispatch(proof, sealedBytes, zkEngine)` (enforcing zero-knowledge membership and anti-replay), and enqueues a `MailboxEnvelope` for each `BlindedRoutingToken` in `ledger.ActiveRoutingTokens` (excluding the sender's routing token).

---

## 4. Phased Implementation & Test Progression

### Phase 1: 2 Identities (`Alice` & `Bob`) — Direct Connection & 1:1 Double Ratchet Chat
- **Arrange**:
  - Build `Alice` (`localhost:15001`) and `Bob` (`localhost:15002`) via `TestClusterBuilder`.
  - Exchange `InvitationLink` (`percolator://localhost:15001/<hex>` and `percolator://localhost:15002/<hex>`) and `PreKeyBundle` out of band.
  - Establish mutual X3DH / `DirectRatchetSession` and shared `DirectChannel` between `Alice` and `Bob`, and open a direct stream between `localhost:15001` and `localhost:15002`.
- **Act & Assert (via CQRS Query Ports)**:
  - `Alice` sends `"Hello Bob over direct localhost!"` via `Alice.ChatService.SendMessageAsync(new SendMessageCommand(channelId, alice.IdentityId, bob.IdentityId, DeviceId.Primary, "Hello Bob over direct localhost!", DeliveryRoute.DirectP2P))`.
  - Query `Alice.OutboxQueries.GetJobSummaryByIdAsync(...)` (or `GetPendingJobsAsync`) and verify the job transitioned to `OutboxStatus.Delivered` via fast-path `IStreamRegistry.TryWriteAsync`.
  - Query `Bob.ChatQueries.GetMessagesAsync(channelId, null, 50, nowMs)` and `Bob.ConversationQueries.GetConversationsAsync(bob.IdentityId, nowMs)`; verify `ChatMessageReadModel.Content == "Hello Bob over direct localhost!"` and `ConversationSummaryReadModel.UnreadCount == 1`.
  - `Bob` replies `"Hi Alice, loud and clear!"` via `Bob.ChatService.SendMessageAsync`.
  - Query `Alice.ChatQueries.GetMessagesAsync(channelId, null, 50, nowMs)` and verify both messages appear in chronological order after the Double Ratchet DH turnaround (`StepDhRatchet`).

### Phase 2: 3rd (`Relay`) & 4th Identity (`Charlie`) — X3DH Handshake & Chat Over Relay
- **Arrange**:
  - Add `Relay` (`localhost:15003`, `.AsRelayNode()`) and `Charlie` (`localhost:15004`, reachable via `Relay`).
  - `Charlie` publishes a `RelayHostedPreKeyBundle` to `Relay` (`RelayHostedPreKeyBundle.Create(...)` with 5 one-time prekeys) and registers his `(MailboxRoutingToken, MailboxDeliveryToken, MailboxDrainToken)` in `Relay.MailboxQueue`.
- **Act & Assert (via CQRS Query Ports)**:
  - `Alice` consumes a `PreKeyBundle` for `Charlie` from `Relay` (`RelayHostedPreKeyBundle.ConsumeBundle()`, verifying `AvailableOneTimePreKeyCount` decrements from 5 to 4).
  - `Alice` serializes a greeting `ChatPayloadEnvelopeDto` (`"Hi Charlie, reaching out via Relay!"`) and calls `Alice.HandshakeService.InitiateHandshakeAsync(alice.IdentityId, DeviceId.Primary, charlieBundle, AppId.Chat, greetingBytes)`.
  - `Alice.OutboxWorker.ProcessBatchAsync()` dispatches the handshake job into `Relay.MailboxQueue.Enqueue(...)`.
  - `Charlie` drains his mailbox on `Relay` (`TryDrainForToken`), unwraps via `ISealedEnvelopeUnwrapper`, and processes the `InboundHandshakeEnvelope` via `Charlie.IngressPipeline.ProcessInboundAsync`.
  - Query `Charlie.ContactQueries.GetContactDetailAsync(charlie.IdentityId, alice.IdentityId)` and verify `State == ContactState.PendingApproval`; query `Charlie.ChatQueries.GetMessagesAsync` and verify the greeting is not yet visible before consent.
  - `Charlie` calls `Charlie.ContactRequestCoordinator.ApproveRequestAsync(charlie.IdentityId, alice.IdentityId)` and `Charlie.HandshakeService.CompletePendingHandshakeAsync(charlie.IdentityId, alice.IdentityId)`.
  - Query `Charlie.ContactQueries.GetContactDetailAsync(charlie.IdentityId, alice.IdentityId)` (`State == ContactState.Approved`) and `Charlie.ChatQueries.GetMessagesAsync(...)` (verifying `"Hi Charlie, reaching out via Relay!"` is now decrypted and projected in `ChatMessageReadModel`).
  - `Charlie` and `Alice` exchange follow-up 1:1 messages using `DeliveryRoute.RelayedOneToOne` routed through `RelayMailboxQueue`.

### Phase 3: Group Chat (`Alice`, `Bob`, `Charlie`) Hosted on `Relay`
- **Arrange**:
  - `Alice` creates a `GroupChannel` (`"Percolator Builders"`), `GroupCredentials` (`GroupMasterKey`), and registers a `RelayGroupLedger` on `Relay` (`localhost:15003`) with `BlindedRoutingToken`s for `Alice`, `Bob`, and `Charlie`.
- **Act & Assert (via CQRS Query Ports)**:
  - `Alice` invites `Bob` (over `DeliveryRoute.DirectP2P`) and `Charlie` (over `DeliveryRoute.RelayedOneToOne`) using `Alice.GroupInvitationService.SendInvitationAsync`.
  - Query `Bob.GroupInvitationQueries.GetPendingInvitationsAsync()` and `Charlie.GroupInvitationQueries.GetPendingInvitationsAsync()`; verify each returns a `GroupInvitationSummaryReadModel` with `GroupName == "Percolator Builders"` and `Status == GroupInvitationStatus.Pending`.
  - `Bob` and `Charlie` call `GroupInvitationService.AcceptInvitationAsync(invitation.InvitationId, ...)`.
  - `Alice`, `Bob`, and `Charlie` each call `GroupKeyDistributionService.DistributeSenderKeyAsync(groupChannelId, senderId, DeviceId.Primary, rosterIds)` (which uses `IRoutingCoordinator` + `IChannelRepository` to route `AppId.SystemControl` over `DirectP2P` to `Bob` and `RelayedOneToOne` to `Charlie`).
  - `Alice` broadcasts `"Welcome to the group, Bob and Charlie!"` via `Alice.ChatService.SendMessageAsync` with `RecipientIdentityId: null` and `Route: DeliveryRoute.RelayedGroup`.
  - `Bob` broadcasts `"Hey everyone from Bob!"` and `Charlie` broadcasts `"Hey everyone from Charlie!"`.
  - Query `ChatQueries.GetMessagesAsync(groupChannelId, null, 50, nowMs)` on `Alice`, `Bob`, and `Charlie` and verify all 3 `ChatMessageReadModel`s are present with verified author identities, while `Relay` only ever processed `ZkPresentationBytes` and opaque ciphertexts.

### Phase 4: Chat Media & Out-of-Band Encrypted Attachment Storage
- **Act & Assert (via CQRS Query Ports)**:
  - `Alice` generates a 128 KB binary payload (`"architecture-spec.pdf"`, `application/pdf`) and calls `Alice.ChatMediaService.UploadOutOfBandAttachmentAsync(stream, 131072, groupChannelId, "architecture-spec.pdf", "application/pdf")`.
  - `Alice` sends a group chat message with `Attachments: [mediaAttachmentDto]` via `Alice.ChatService.SendMessageAsync`.
  - `Bob` and `Charlie` query `ChatQueries.GetMessagesAsync(groupChannelId, null, 50, nowMs)`, extract `readModel.Attachments[0]`, and call `ChatMediaService.DownloadOutOfBandAttachmentAsync(readModel.Attachments[0])`.
  - Verify `Relay`'s `IRelayBlobClient.DownloadBlobAsync` serves the encrypted chunks, `IBlobCryptoService.DecryptStreamAsync` verifies the AEAD tags and decrypts the stream, and the downloaded bytes on `Bob` and `Charlie` match `Alice`'s 128 KB payload byte-for-byte.

### Phase 5: Peer Discovery (`Apps.Discovery` — Opcode Wire Protocol, Directory & Blinded Rendezvous)
- **Arrange**:
  - Add a 5th identity `Dave` (`localhost:15005`) who has an authenticated session with `Relay` (`localhost:15003`) for `AppId.Discovery` (`0x02`), and a LAN/direct discovery channel to `Bob` (`localhost:15002`).
- **Act & Assert (via Driving & Query Ports)**:
  1. **Relay Visibility Registration & Nearby Discovery (`DiscoveryOpcode.RegisterVisibility` & `NearbyQuery`)**:
     - `Bob` sends a `DiscoveryOpcode.RegisterVisibility` (`0x01`) frame with `PeerDiscoveryVisibility.Discoverable` to `Relay`'s `DiscoveryPayloadHandler`.
     - `Dave` runs `Dave.NearbyDiscoveryWorkflow.DiscoverAndConnectAsync(dave.IdentityId, DeviceId.Primary, relay.IdentityId, DiscoveryResultLimit.From(10))`, which queries `Relay`, discovers `Bob`, fetches `Bob`'s `PreKeyBundle` via `IPreKeyBundleProvider`, and initiates an X3DH handshake with `Bob`.
     - Verify `Bob.ContactQueries.GetContactDetailAsync(bob.IdentityId, dave.IdentityId)` shows `Dave`'s inbound handshake request.
  2. **Private Blinded Rendezvous (`DiscoveryOpcode.RendezvousPublish` & `RendezvousQuery`)**:
     - `Alice` and `Dave` share an out-of-band passphrase secret (`sharedSecret`) and derive a `BlindedLocator` for the current `DiscoveryEpoch` via `PrivateRendezvousService.DeriveLocator(alice.IdentityId, sharedSecret, epoch)`.
     - `Alice` publishes her `EncryptedRendezvousRoute` to `Relay` via `DiscoveryOpcode.RendezvousPublish` (`0x04`).
     - `Dave` queries `Relay` via `DiscoveryOpcode.RendezvousQuery` (`0x05`) with `PrivateRendezvousService.DeriveLocatorWindow(alice.IdentityId, sharedSecret, epoch)`, receives `BlindedRendezvousMatch`, handshakes with `Alice`, and exchanges a verified 1:1 chat message asserted via `Alice.ChatQueries.GetMessagesAsync`.

### Phase 6: Multi-Stream Bulk File Transfer (`Apps.FileTransfer`)
- **Arrange**:
  - `Alice` (`localhost:15001`) stages a 3.5 MB file (`"release-bundle.bin"`, spanning four 1 MB chunks under `ChunkSizeBytes`) in her `IDesktopFileStorage` under `RootDirectoryPath.Parse("/alice-share")`.
- **Act & Assert (via Driving & CQRS Query Ports)**:
  - `Alice` calls `Alice.FileTransferActions.InitiateTransferAsync(aliceBobChannelId, alice.IdentityId, [bob.IdentityId], RootDirectoryPath.Parse("/alice-share"), [SanitizedRelativePath.Parse("release-bundle.bin")], DirectWanPolicy.AllowDirectWan)`.
  - Query `Alice.FileTransferQueries.GetTransferByIdAsync(sessionId)` and verify `TransferSessionReadModel.Status == TransferSessionState.WaitingForPeer` and `TotalChunks.Count == 4`.
  - Query `Bob.FileTransferQueries.GetTransferByIdAsync(sessionId)` and `GetManifestFilesAsync(sessionId)` after `Bob`'s `FileTransferPayloadHandler` processes the in-band `FileTransferControlOpcode.Offer`; verify `Status == TransferSessionState.PendingConsent` and `ManifestFileReadModel` lists `"release-bundle.bin"` (`3,670,016` bytes).
  - `Bob` calls `Bob.FileTransferActions.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse("/bob-downloads"))`.
  - Query `Alice.FileTransferQueries.GetTransferByIdAsync(sessionId)` and verify `Status == TransferSessionState.Uploading`.
  - Run `TransferSessionCoordinator.PumpPeerStreamsAsync` concurrently across `Alice` and `Bob`'s connected `ITransferStreamPool` (`localhost:15001` <-> `localhost:15002`).
  - Query `Bob.FileTransferQueries.GetTransferByIdAsync(sessionId)` and `GetManifestFilesAsync(sessionId)`; verify `Status == TransferSessionState.Completed`, `CompletedChunks == TotalChunks`, `DownloadedBytes == TotalBytes`, `ManifestFileReadModel.IsCompleted == true`, and the assembled file in `Bob`'s `IDesktopFileStorage` matches `Alice`'s 3.5 MB payload byte-for-byte.


