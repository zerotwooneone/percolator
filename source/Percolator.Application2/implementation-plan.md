# Percolator.Application2 Implementation Plan
## Event-Driven Microkernel / Plugin Pipeline Architecture

---

## 1. Architectural Vision & Scope

`Percolator.Application2` acts as the **application orchestration core** sitting directly on top of `Percolator.Domain`. While the domain is strictly isolated and agnostic to wire protocols, storage engines, and specific applications, `Percolator.Application2` is responsible for:
- Orchestrating use cases across bounded contexts (Identities, Conversations, Security, Delivery).
- Managing store-and-forward outbox workers, delivery retry policies, and transport routing.
- Hosting an **Event-Driven Microkernel & Plugin Pipeline** that decouples application logic (Chat, Discovery, File Transfer) from core end-to-end encryption transport.
- Enforcing the strict duality between **Control Plane** (in-band E2EE signaling) and **Data Plane** (out-of-band high-throughput P2P streaming).
- Packaging cryptographic framing (`RatchetHeader`, `MailboxEnvelope`) and orchestrating cryptographic handshakes (X3DH initial sessions, pairwise Sender Key distribution, and ZK group relay dispatch).

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
                 │ (Payload, AppId=0x01)     │ (Payload, AppId=0x02)      │ (Control Manifest, 0x03)
                 ▼                           ▼                            ▼
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
 │   │   - SQLite Job Queue & Transactional Consistency                           │   │
 │   │   - Exponential Backoff & Jitter Retry Policies                            │   │
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

## 2. Core Architectural Pillars

### 2.1 The Control Plane vs. Data Plane Duality

Streaming multi-megabyte or gigabyte file chunks through the Double Ratchet or Group Sender Key ratchet destroys ratcheting counters, saturates relay bandwidth, and fragments local databases. To solve this, the architecture splits operations into two planes:

1. **The Control Plane (In-Band E2EE via `Percolator.Domain`):**
   - Transports all lightweight, latency-sensitive, and security-critical signaling.
   - Chat messages, reactions, delivery/read receipts, typing notifications.
   - DHT presence tickets, rendezvous discovery requests.
   - File transfer manifests (infohashes, Merkle roots, file sizes, chunk sizes, and one-time symmetric encryption keys).
   - Strict forward secrecy and post-compromise security via Double Ratchet / Sender Keys.
2. **The Data Plane (Out-of-Band High-Throughput P2P):**
   - Transports raw file chunks and heavy binary streams directly between peers or swarms (BitTorrent-style P2P sidecar, WebRTC data channels, or direct TCP/QUIC).
   - Chunks are encrypted with ephemeral symmetric keys negotiated over the Control Plane and verified against the Merkle tree.
   - The domain and relay never see or buffer raw file chunks; untrusted transport nodes only see opaque ciphertext blocks.

### 2.2 Event-Driven Microkernel & Multiplexing Protocol

Every message sent across the network encapsulates an application header:
- **Header:** 1-byte `AppId` (e.g., `0x01` Chat, `0x02` Discovery, `0x03` FileTransferControl, `0x00` SystemControl).
- **Body:** Opaque application payload (`ReadOnlyMemory<byte>`), serialized and deserialized by the respective plugin.

The microkernel pipeline provides:
- **`IAppPlugin` / `IAppPayloadHandler`:** Plugin contract for handling specific `AppId` payloads.
- **Middleware Chain:** Extensible pipeline handlers for inbound and outbound messages:
  - Idempotency & Deduplication
  - Anti-Flood & Rate Limiting
  - Diagnostics & Privacy-Preserving Logging

---

## 3. Cryptographic Orchestration & Framing Flows

### 3.1 Flow A: Direct 1:1 Session Handshake & Ratchet
1. **Outbound Initiation:**
   - Client fetches Bob's published `PreKeyBundle`.
   - Calls `DirectRatchetSession.InitiateOutbound(AliceId, AliceDeviceId, bobBundle, engine)`.
   - Steps sending chain: receives `(counter = 0, messageKey, ephemeralPublicKey)`.
   - Encrypts payload with `messageKey` via AES-GCM; packages plaintext `RatchetHeader(ephemeralPublicKey, counter, previousLength)`.
   - Dispatches initial packet to Outbox.
2. **Inbound Initiation:**
   - Bob receives packet with `RatchetHeader`.
   - Looks up corresponding local pre-key private key.
   - Calls `DirectRatchetSession.InitiateInbound(BobId, BobDeviceId, AliceId, AliceDeviceId, localPreKeyPriv, header.EphemeralPublicKey, engine)`.
   - Steps receiving chain: derives matching `messageKey` and decrypts payload.
   - Session transitions to active Double Ratchet state.

### 3.2 Flow B: 1:1 Over Store-and-Forward Relay
1. **Envelope Packaging:**
   - Application serializes payload and encrypts via `DirectRatchetSession`.
   - Bundles `RatchetHeader` and ciphertext into `ReadOnlyMemory<byte>`.
   - Wraps into `MailboxEnvelope(EnvelopeId.New(), RecipientToken, envelopeData, enqueuedAtUtc, expiresAtUtc)`.
2. **Relay Enqueue & Drain:**
   - Sender submits envelope with authorized `DeliveryToken` to `RelayMailboxQueue.Enqueue`.
   - Relay verifies token, enforces quota policy, and buffers envelope.
   - Recipient authenticates to relay, calls `RelayMailboxQueue.DrainForToken(RecipientToken)`, parses header, and steps ratchet.

### 3.3 Flow C: Group Sender Key Fan-Out & ZK Relay Dispatch
1. **Pairwise Key Distribution:**
   - Group creator/author generates `GroupSenderKeyRatchet`.
   - Packages distribution control payload: `SenderKeyDistributionPayload(ConversationId, InitialChainKey, Iteration)`.
   - Transmits distribution payload to each group member pairwise via their established 1:1 `DirectRatchetSession` channels.
   - Each member receives the control payload and instantiates a `GroupReceiverSession(ConversationId, AuthorId, AuthorDeviceId, chainKey)`.
2. **Group Message Dispatch & Verification:**
   - Author calls `GroupSenderKeyRatchet.Advance(engine)` ➔ derives `(iteration, messageKey)`.
   - Encrypts message payload.
   - Generates ZK membership presentation proof over the envelope ciphertext.
   - Relay verifies proof against current group epoch via `RelayGroupLedger.VerifyDispatch()`.
   - Relay broadcasts envelope to all `ActiveRoutingTokens`.
   - Recipients receive envelope, call `GroupReceiverSession.AdvanceTo(iteration, engine)`, and decrypt payload (with out-of-order skipped key cache support).

---

## 4. Sub-System Specifications

### 4.1 Sub-System 1: Outbox & Delivery Orchestrator (`Percolator.Application2.Delivery`)
- **Lifecycle & Storage:**
  - Persists `OutboxJob` entities via `IOutboxRepository`.
  - Manages statuses: `Pending` ➔ `InFlight` ➔ `Delivered` (or `Failed` with exponential backoff).
- **Zero-Leakage Inactivity ("Black Hole" Rule):**
  - Listens to `IdentityDisabledEvent` from `Percolator.Domain`.
  - Immediately transitions all pending jobs for the disabled persona to `PausedDormant`.
  - Suppresses all outbound socket/relay connections for that identity without emitting network errors.
- **Transport Routing:**
  - Dispatches to direct P2P endpoints or relay gRPC mailboxes based on `DeliveryRoute` (`DirectP2P`, `RelayedOneToOne`, `RelayedGroup`).

### 4.2 Sub-System 2: Application Pipeline & Plugin Host (`Percolator.Application2.Pipeline`)
- **Contracts:**
  - `IAppPlugin`: Base contract declaring `AppId`, plugin metadata, and lifecycle hooks (`StartAsync`, `StopAsync`).
  - `IAppPayloadHandler`: Handles inbound decrypted payloads for a specific `AppId` (`ReadOnlyMemory<byte>`).
  - `IPayloadDispatcher`: Routes inbound decrypted messages from the domain ratchet session to the appropriate registered handler.
  - `IOutboundPipeline`: Orchestrates application payload serialization, header attachment, ratchet session encryption, and outbox job creation.
- **Middleware Infrastructure:**
  - Pipeline context carrying `ConversationId`, `SenderIdentityId`, `RecipientIdentityId`, timestamp, and raw payload span.

### 4.3 Sub-System 3: Chat Application Plugin (`Percolator.Apps.Chat`)
- **Payload Schema (`AppId = 0x01`):**
  - Text messages (UTF-8, limited markdown).
  - Reactions: `(TargetMessageId, EmojiCode, Action: Add|Remove)`.
  - Receipts: `(TargetMessageId, Status: Delivered|Read, TimestampUtc)`.
- **Local Link Preview Engine (Signal-Style Privacy):**
  - The *sender's* client extracts OpenGraph / microdata from links locally.
  - Generates small compressed thumbnails (< 32KB).
  - Encrypts preview metadata and thumbnail into the chat payload.
  - The *recipient* renders the preview locally without ever fetching the external URL, preventing IP address leakage and tracking.

### 4.4 Sub-System 4: Peer Discovery & DHT Plugin (`Percolator.Apps.Discovery`)
- **Payload Schema (`AppId = 0x02`):**
  - `DhtPing`, `DhtFindNode`, `DhtNodeAdvertisement`.
- **Blinded DHT Identity Locators:**
  - Opt-in discovery: Users can publish blinded locator tokens `SHA256(PublicIdentityId || Salt)` into a Kademlia DHT.
  - Peers who know the shared secret or public identity can compute the locator and discover the peer's active relay endpoint or direct rendezvous IP.
  - Zero linkage between DHT keys and actual identity keys for non-contacts.

### 4.5 Sub-System 5: Out-of-Band File Transfer Plugin (`Percolator.Apps.FileTransfer`)
- **Control Plane (`AppId = 0x03`):**
  - `FileManifestMessage`:
    - `InfoHash` (SHA-256 of file descriptor)
    - `MerkleRoot` (tree root of chunk hashes)
    - `TotalSizeBytes`, `ChunkSizeBytes` (typically 32KB–128KB)
    - `EphemeralKey` (256-bit AES-GCM or ChaCha20 key)
- **Data Plane (Out-of-Band P2P Sidecar):**
  - Independent transport channel: WebRTC DataChannel, direct TCP/QUIC, or BitTorrent swarm.
  - Chunks requested via `Bitfield` and `PieceRequest` messages.
  - Each chunk is verified against the Merkle tree before writing to disk.
  - BitTorrent-style tit-for-tat or concurrent multi-source chunk downloading from swarm peers.

---

## 5. Step-by-Step TDD Implementation Plan

### Milestone 1: Microkernel Pipeline & Plugin Contracts (`Percolator.Application2`)
- **Tasks:**
  - Define `IAppPlugin`, `IAppPayloadHandler`, `InboundPayloadContext`, `OutboundPayloadContext`.
  - Implement `PayloadDispatcher` with `AppId` multiplexing (tagging first byte).
  - Implement inbound/outbound middleware pipeline (`PipelineDelegate`, `IPipelineBehavior`).
- **Tests (`Percolator.Application2.Tests`):**
  - `PayloadDispatcherTests`: Verifies payload routing to correct `AppId` handler, unknown `AppId` handling, and malformed header rejection.
  - `PipelineBehaviorTests`: Verifies middleware order execution, cancellation, and error handling.

### Milestone 2: Outbox Worker & Transport Dispatcher (`Percolator.Application2`)
- **Tasks:**
  - Implement `OutboxWorker` (reactive/background channel processing pending `OutboxJob`s).
  - Implement exponential backoff retry scheduler with jitter.
  - Implement dormancy event listener: binds `IdentityDisabledEvent` to suspend jobs via `OutboxJob.PauseForDormancy`.
- **Tests (`Percolator.Application2.Tests`):**
  - `OutboxWorkerTests`: Successful delivery transitions job to `Delivered`.
  - `OutboxRetryPolicyTests`: Transient failure increments retry count and sets backoff; terminal failure marks `Failed`.
  - `DormancySuspensionTests`: Emitting `IdentityDisabledEvent` pauses pending outbox jobs and halts transmissions.

### Milestone 3: Chat Application Plugin (`Percolator.Apps.Chat`)
- **Tasks:**
  - Implement `ChatPayload` binary/Protobuf serialization (text, reactions, receipts).
  - Implement `ChatPlugin` (`IAppPlugin`, `AppId = 0x01`).
  - Implement sender-side `LinkPreviewExtractor` and thumbnail packager.
- **Tests (`Percolator.Apps.Chat.Tests`):**
  - `ChatPayloadTests`: Serialization roundtrip for text, emoji reactions, and receipts.
  - `ChatPluginTests`: Inbound payload triggers appropriate domain chat events.
  - `LinkPreviewExtractorTests`: OpenGraph parser produces compact preview payloads without recipient network requests.

### Milestone 4: Peer Discovery Plugin (`Percolator.Apps.Discovery`)
- **Tasks:**
  - Implement `DiscoveryPayload` (`AppId = 0x02`).
  - Implement blinded locator derivation (`SHA256(PublicIdentityId || Salt)`).
  - Implement rendezvous ping/pong state machine.
- **Tests (`Percolator.Apps.Discovery.Tests`):**
  - `BlindedLocatorTests`: Verifies deterministic blinded token generation and contact verification.
  - `DiscoveryPluginTests`: Handles inbound peer lookup and returns relay routing descriptor.

### Milestone 5: Control/Data Plane File Transfer Plugin (`Percolator.Apps.FileTransfer`)
- **Tasks:**
  - Implement Control Plane manifest schema (`FileManifestTicket`, `AppId = 0x03`).
  - Implement Merkle tree chunk builder and proof verifier.
  - Implement Data Plane chunk transport adapter (symmetric encryption per chunk, verified against Merkle leaf).
- **Tests (`Percolator.Apps.FileTransfer.Tests`):**
  - `FileManifestTests`: Manifest creation, serialization, and symmetric key exchange.
  - `MerkleChunkVerifierTests`: Detects and rejects corrupted or tampered file chunks.
  - `DataPlaneTransferTests`: Simulated multi-chunk transfer with out-of-band P2P mock, verifying zero Double Ratchet involvement.

### Milestone 6: End-to-End Cryptographic Integration Test Suite (`Percolator.Application2IntegrationTests`)
- **Tasks:**
  - Implement end-to-end integration test harnesses validating cryptographic orchestration between Application2, plugins, and the domain.
- **Tests (`Percolator.Application2IntegrationTests`):**
  - **`DirectOneToOneEncryptionIntegrationTests`:**
    - Handshake initiation via `PreKeyBundle` and `DirectRatchetSession.InitiateOutbound` / `InitiateInbound`.
    - Bidirectional messaging with alternating DH ratchets and symmetric stepping.
    - Out-of-order message arrival with skipped-key recovery and replay protection.
    - Application pipeline multiplexing (`AppId = 0x01` Chat text, reactions, receipts).
    - Secret hygiene verification: session disposal zeroizes memory.
  - **`RelayedOneToOneEncryptionIntegrationTests`:**
    - Alice packs encrypted payload and `RatchetHeader` into `MailboxEnvelope`.
    - Outbox dispatches envelope to `RelayMailboxQueue` using `DeliveryToken`.
    - Relay verifies token, quota policy, and buffers envelope.
    - Bob authenticates with `BlindedRoutingToken`, drains envelope, unpacks header, and decrypts.
    - Expired envelope cleanup via `PurgeExpired`.
  - **`GroupCommunicationEncryptionIntegrationTests`:**
    - Group creation and `RelayGroupLedger.CreateGenesis`.
    - Pairwise sender key distribution: author distributes initial `ChainKey` to members via 1:1 `DirectRatchetSession`.
    - Members instantiate `GroupReceiverSession`.
    - Author advances `GroupSenderKeyRatchet`, encrypts group broadcast message, generates ZK presentation proof.
    - `RelayGroupLedger.VerifyDispatch` verifies proof against current epoch and dispatches to active routing tokens.
    - Multiple group members drain envelope and decrypt via `GroupReceiverSession.AdvanceTo()`.
    - Out-of-order group message delivery verified via `GroupReceiverSession` skipped-key caching.
    - Group member removal triggering epoch rotation and rekeying.
