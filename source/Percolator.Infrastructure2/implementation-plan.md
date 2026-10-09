# Percolator.Infrastructure2 Implementation Plan

## 1. Overview & Architectural Role

`Percolator.Infrastructure2` provides concrete technical adapters, storage repositories, network transport clients/servers, cryptographic primitives, and binary serialization engines for the Percolator microkernel architecture.

Following Clean Architecture principles:
- **Dependencies Flow Inward**: `Infrastructure2` references `Percolator.Domain`, `Percolator.Application`, and `Percolator.PluginSdk`. It does NOT expose infrastructure-specific types (e.g., SQLite connections, gRPC stubs, Protobuf classes, raw sockets) to the domain or application layers.
- **Port Realization**: Every component in this project implements an interface (port) defined by `Percolator.Domain`, `Percolator.Application`, `Percolator.PluginSdk`, or application plugins (`Percolator.Apps.*`).
- **Wire Contract & Serialization Ownership**: Concrete Protobuf `.proto` schemas, code-generated message classes, gRPC service stubs, and binary serializers live strictly within `Percolator.Infrastructure2`. The inner layers interact solely via pure C# DTOs and domain models via the `IPayloadSerializer` and `ISessionWirePacker` ports.

---

## 2. Legacy Migration & Cutover Strategy

The legacy codebase contains dated, overlapping modules marked for total deletion during the cutover:
- `Percolator.Contracts` (Legacy Protobuf definitions and gRPC service contracts)
- `Percolator.Identity`
- `Percolator.Cryptography`
- `Percolator.Network`
- `Percolator.Infrastructure`
- `Percolator.Wpf` (Legacy Windows-only desktop client)

### Cutover Workflow
1. **Domain & Application Completion**: Implement and verify `Percolator.Domain`, `Percolator.Application` (formerly `Application2`), application plugins (`Percolator.Apps.Chat`, `Discovery`, `FileTransfer`), and their test suites.
2. **Legacy Project Deletion**: Remove `Percolator.Contracts` and all legacy projects from the solution (`Percolator.sln`) and delete their filesystem directories.
   - None of the modern projects (`Percolator.Domain`, `Percolator.PluginSdk`, `Percolator.Application`, `Percolator.Apps.*`) reference `Percolator.Contracts`.
   - All replacement Protobuf schemas are authored fresh within `Percolator.Infrastructure2/Protos/`.
3. **Compiler-Error-Driven Triage**:
   - The resulting compiler breaks in the desktop app and infrastructure serve as an intentional audit trail.
   - For every breaking symbol/class, decide deliberately:
     - **Keep & Convert**: If an existing adapter (e.g., SQLCipher schema setup or gRPC proto definitions) can be refactored to implement the clean domain/application ports.
     - **Delete & Rewrite**: If the legacy class contains domain leakage, state coupling, or obsolete patterns.

---

## 3. High-Level Requirements

1. **Zero Domain Logic in Infrastructure**: Infrastructure adapters must solely translate between external APIs/data formats and domain value objects / entities.
2. **Zero Plaintext Sensitive State at Rest**: All private keys, ratchet chain keys, and session secrets stored on disk must be encrypted using SQLCipher and/or OS keychain abstractions. Sensitive cryptographic fields must never be stored as unencrypted BLOBs in SQLite.
3. **Deterministic Memory Zeroization**: Any unmanaged buffers or cryptographic key spans must be cleared (`CryptographicOperations.ZeroMemory`) when disposed.
4. **Resilient Network Handling**: All gRPC network calls must obey cancellation tokens, transport timeouts, and surface transient vs. permanent network failures cleanly to `OutboxRetryPolicy`.
5. **Cryptographic Logging Guardrails**: Honor `CryptographyOptions.EnableCryptographicMaterialLogging = false` by default across all infrastructure loggers and diagnostics. Sensitive cryptographic keys, KDF digests, and plaintexts must never be emitted to logs.
6. **Strict Authentication & Metadata Isolation**:
   - **Public Edge / Ingress & Deposit**: Must be unauthenticated (zero sender identity leakage on the wire). Relies on recipient-issued `DeliveryToken`s and Sealed Sender cryptography.
   - **Mailbox Retrieval, Directory & Management**: Must be strictly authenticated via challenge-response or signature cryptographic proofs to prevent unauthorized message draining, OPK exhaustion attacks, and mass directory scraping (mirroring Signal's security model).
   - **Group Operations**: Must be protected via Zero-Knowledge presentation proofs against `RelayGroupLedger` epochs without revealing individual member identities to relays.
7. **Zero Platform Lock-in (Cross-Platform Architecture)**: Core infrastructure contracts, cryptographic flows, and persistence abstractions must run seamlessly across Windows, Linux, and macOS. Platform-specific mechanisms (e.g. Windows SChannel PFX loading, NTFS `FSCTL_SET_SPARSE`, DPAPI) must live behind clean OS-agnostic ports with functional non-Windows fallbacks.

---

## 4. Required Port Implementations & Specific Adapters

### 4.1 Cryptography (`Percolator.Domain.Security.Ports.ICryptoEngine` & `IZkProofEngine`)
- **Adapter**: `SignalCryptoEngine` / `SodiumCryptoEngine` (`ICryptoEngine`)
  - **Ed25519 Signatures**: Signs and verifies device link proofs, pre-key signatures, and group sender key messages (`VerifyEd25519Signature(IdentityKey, ...)`, `SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)`).
  - **Curve25519 (X25519) Diffie-Hellman**: Computes scalar multiplication between private keys and public DH keys (`ComputeDiffieHellman`, `DeriveX3dhMasterSecret`).
  - **HKDF / HMAC Ratchet Steps**: Performs SHA-256 HMAC-based root key updates (`KdfRk`) and symmetric chain key advances (`StepRatchet`).
  - **Authenticated Encryption**: AES-256-GCM authenticated symmetric encryption (`EncryptAesGcm`, `DecryptAesGcm`).
  - **Resilient Public Key Parsing**: Supports importing public keys from both raw 32/64/65-byte point spans and ASN.1 DER SubjectPublicKeyInfo structures without throwing parsing exceptions.
  - **Logging Guardrails**: Redacts all key spans and secrets in logs, controlled via `CryptographyOptions`.
  - **RFC 4122 UUID Endianness Normalization**:
    - When bridging between .NET `Guid` structures and native cryptographic libraries (`libsignal`, `zkgroup`) or cross-platform network payloads, explicitly normalize byte orders via `Guid.ToByteArray(bigEndian: true)`.
    - Defends against .NET Windows little-endian byte ordering of Data1, Data2, and Data3 which otherwise corrupts UUIDs passed into native C/Rust functions.
  - **Unmanaged Native Callback Database Threading (`IDbContextFactory<T>`)**:
    - Native C/Rust libraries invoke C-ABI callbacks synchronously (e.g. `LoadSenderKey`, `StoreSenderKey`) on foreign unmanaged threads outside ASP.NET Core request scopes.
    - Interop bridge adapters must consume `IDbContextFactory<T>` to spin up short-lived isolated database contexts, preventing concurrency clashes and cross-thread context contamination.
- **Adapter**: `ZkgroupCryptographyService` (`IZkProofEngine`)
  - **Native zkgroup FFI**: Wraps `Signal.Interop` native C/Rust binaries with safe handle management (`using var ...` wrapping native `SafeHandle`).
  - **ZK Group Presentations**: Generates and verifies zero-knowledge membership proofs for anonymous group relay interactions (`VerifyGroupPresentation`, `GenerateGroupPresentation`).

### 4.2 Serialization, Wire Contracts & Protobuf Schemas (`Protos/`)

Rather than blindly cloning legacy contracts, `Infrastructure2` defines purpose-built Protobuf services organized strictly by **trust boundary** and **authentication model**:

```
                                  ┌────────────────────────────────────────┐
                                  │      Client / Sender / Peer Node       │
                                  └───────────────────┬────────────────────┘
                                      │                   │              │
                   (Unauthenticated / │       (Auth via   │   (Auth via  │
                    Delivery Token)   │        Signature) │    ZK-Proof) │
                                      ▼                   ▼              ▼
┌──────────────────────────────────────┐ ┌──────────────────┐ ┌─────────────────────┐
│    UnauthenticatedDeliveryService    │ │ Authenticated    │ │ AnonymousGroupRelay │
├──────────────────────────────────────┤ │ RelayService     │ │ Service             │
│ • DeliverDirect(SealedEnvelope)      │ ├──────────────────┤ ├─────────────────────┤
│ • EnqueueMailbox(SealedEnvelope)     │ │ • ConnectMailbox │ │ • SubscribeGroup    │
│                                      │ │   Stream()       │ │   Stream()          │
│                                      │ │ • DrainMailbox() │ │ • DispatchGroupMsg()│
│                                      │ │ • RegisterMbox() │ │ • CommitMutation()  │
│                                      │ │ • PublishPreKey()│ │ • FetchGroupState() │
│                                      │ │ • FetchPreKey()  │ └─────────────────────┘
└──────────────────────────────────────┘ └──────────────────┘
```

#### 1. `unauthenticated_delivery.proto` (Public Edge / Sealed Sender Ingress & Deposit)
- **Trust Boundary**: Open to peers and anonymous senders. Zero sender identity metadata is exposed to transport headers or relay logs. Protected by rate limiting and recipient-issued `DeliveryToken`s.
- **Messages**:
  - `SealedEnvelopeProto`:
    - `bytes recipient_routing_token = 1;` (16-byte `BlindedRoutingToken` for mailbox routing)
    - `bytes delivery_token = 2;` (32-byte `DeliveryToken` verifying sender authorization to drop message)
    - `bytes encrypted_package = 3;` (Opaque encrypted package containing sender ID, ratchet header, and inner payload)
    - `google.protobuf.Timestamp expires_at_utc = 4;` (Enforces queue purge TTL and limits replay windows)
  - `DeliveryAckProto`: `bool success = 1;`, `string error_message = 2;`
  - `EnqueueAckProto`: `bool accepted = 1;`, `google.protobuf.Timestamp expires_at_utc = 2;`
- **Service**:
  - `service UnauthenticatedDeliveryService`:
    - `rpc DeliverDirect(SealedEnvelopeProto) returns (DeliveryAckProto);` (Direct 1:1 P2P ingress over ephemeral TLS)
    - `rpc EnqueueMailbox(SealedEnvelopeProto) returns (EnqueueAckProto);` (Deposit into recipient's store-and-forward relay queue)

#### 2. `authenticated_relay.proto` (Mailbox Ownership, Pre-Key Directory & Stream Management)
- **Trust Boundary**: Challenge-response / signature authenticated. Restricted exclusively to the private key holder of the `PublicIdentityId` / caller identity.
- **Security Rationale (Mirroring Signal)**:
  - `FetchPreKeyBundle` requires authenticated requester identity verification to prevent anonymous scrapers from exhausting one-time prekeys (OPKs) and crawling directory listings, while enabling per-account rate limiting.
- **Messages**:
  - `MailboxStreamClientMessage`:
    - `oneof payload`:
      - `AuthResponseProto auth_response = 1;` (Signature over server-issued challenge nonce)
      - `MailboxAckProto ack = 2;` (Confirms client receipt and triggers atomic deletion of queued message)
  - `MailboxStreamServerMessage`:
    - `oneof payload`:
      - `AuthChallengeProto auth_challenge = 1;` (Server-generated random 32-byte challenge nonce)
      - `AuthResultProto auth_result = 2;` (Authentication success or error code)
      - `QueuedSealedEnvelopeProto envelope = 3;` (Queued `SealedEnvelopeProto` accompanied by unique `bytes ack_id`)
  - `RegisterMailboxRequest`:
    - `bytes owner_identity_key = 1;`
    - `bytes routing_token = 2;`
    - `bytes authorized_delivery_token = 3;`
    - `bytes signature = 4;` (Signs `routing_token + authorized_delivery_token` to prove ownership)
  - `RegisterMailboxResponse`: `bool success = 1;`
  - `DrainMailboxRequest`: `bytes routing_token = 1;`, `bytes auth_token = 2;`
  - `DrainMailboxResponse`: `repeated QueuedSealedEnvelopeProto envelopes = 1;`
  - `PublishPreKeyBundleRequest`:
    - `bytes identity_key = 1;`
    - `bytes signed_prekey = 2;`
    - `bytes prekey_signature = 3;`
    - `bytes signed_prekey_id = 4;`
    - `repeated OneTimePreKeyProto one_time_prekeys = 5;`
    - `bytes signature = 6;`
  - `PublishPreKeyBundleResponse`: `bool success = 1;`
  - `FetchPreKeyBundleRequest`:
    - `bytes recipient_routing_token = 1;`
    - `bytes requester_identity_key = 2;`
    - `bytes requester_signature = 3;` (Signs `recipient_routing_token + timestamp_utc` proving requester identity)
    - `google.protobuf.Timestamp timestamp_utc = 4;`
  - `FetchPreKeyBundleResponse`:
    - `bytes identity_key = 1;`
    - `bytes signed_prekey = 2;`
    - `bytes prekey_signature = 3;`
    - `bytes signed_prekey_id = 4;`
    - `optional bytes one_time_prekey = 5;`
    - `optional bytes one_time_prekey_id = 6;`
  - `OneTimePreKeyProto`: `bytes key_id = 1;`, `bytes public_key = 2;`
- **Service**:
  - `service AuthenticatedRelayService`:
    - `rpc ConnectMailboxStream(stream MailboxStreamClientMessage) returns (stream MailboxStreamServerMessage);` (Duplex streaming connection for real-time pushed messages with ACK flow control)
    - `rpc RegisterMailbox(RegisterMailboxRequest) returns (RegisterMailboxResponse);` (Registers authorized delivery tokens)
    - `rpc DrainMailbox(DrainMailboxRequest) returns (DrainMailboxResponse);` (Unary fallback for intermittent polling)
    - `rpc PublishPreKeyBundle(PublishPreKeyBundleRequest) returns (PublishPreKeyBundleResponse);` (Authenticated prekey publishing)
    - `rpc FetchPreKeyBundle(FetchPreKeyBundleRequest) returns (FetchPreKeyBundleResponse);` (Authenticated prekey lookup with OPK rate-limiting & anti-scraping enforcement)

#### 3. `anonymous_group.proto` (Zero-Knowledge Group Relay Service)
- **Trust Boundary**: Zero-Knowledge membership verification. Group members prove authorization to read and write without revealing their identities to the relay.
- **Messages**:
  - `GroupStreamClientMessage`:
    - `bytes conversation_id = 1;`
    - `uint32 current_epoch = 2;`
    - `bytes zk_presentation_proof = 3;` (Proves membership in the current epoch)
  - `GroupStreamServerMessage`:
    - `oneof payload`:
      - `GroupBroadcastEnvelope broadcast = 1;` (Real-time group payload dispatched to subscribers)
      - `EpochMutationNotification mutation = 2;` (Alerts members that a roster change occurred)
  - `GroupBroadcastEnvelope`:
    - `bytes conversation_id = 1;`
    - `uint32 epoch = 2;`
    - `uint32 sender_key_iteration = 3;`
    - `bytes ciphertext = 4;`
    - `bytes author_signature = 5;`
  - `EpochMutationNotification`:
    - `bytes conversation_id = 1;`
    - `uint32 new_epoch = 2;`
  - `DispatchGroupMessageRequest`:
    - `bytes conversation_id = 1;`
    - `uint32 epoch = 2;`
    - `bytes zk_presentation_proof = 3;` (Verifies proof over `SHA256(ciphertext)`)
    - `uint32 sender_key_iteration = 4;`
    - `bytes ciphertext = 5;`
    - `bytes author_signature = 6;`
  - `DispatchGroupMessageResponse`: `bool accepted = 1;`
  - `CommitRosterMutationRequest`:
    - `bytes conversation_id = 1;`
    - `uint32 base_epoch = 2;`
    - `bytes new_roster_blob = 3;`
    - `repeated bytes new_routing_tokens = 4;`
    - `bytes zk_presentation_proof = 5;` (Verifies proof over mutation transcript hash)
  - `CommitRosterMutationResponse`: `bool committed = 1;`, `uint32 new_epoch = 2;`
  - `FetchGroupStateRequest`: `bytes conversation_id = 1;`, `bytes zk_presentation_proof = 2;`
  - `FetchGroupStateResponse`: `uint32 current_epoch = 1;`, `bytes encrypted_roster_blob = 2;`
- **Service**:
  - `service AnonymousGroupRelayService`:
    - `rpc SubscribeGroupStream(GroupStreamClientMessage) returns (stream GroupStreamServerMessage);` (Server-streaming subscription to live channel updates)
    - `rpc DispatchGroupMessage(DispatchGroupMessageRequest) returns (DispatchGroupMessageResponse);` (Anonymous broadcast dispatch)
    - `rpc CommitRosterMutation(CommitRosterMutationRequest) returns (CommitRosterMutationResponse);` (Roster epoch transition)
    - `rpc FetchGroupState(FetchGroupStateRequest) returns (FetchGroupStateResponse);` (State catch-up)

#### 4. `session.proto` (Inner Cryptographic Packaging)
Carried inside `SealedEnvelopeProto.encrypted_package` (opaque to relays and passive observers):
- **Messages**:
  - `RatchetHeaderProto`: `bytes ratchet_public_key = 1;`, `uint32 counter = 2;`, `uint32 previous_chain_length = 3;`
  - `DirectSessionPackageProto`:
    - `RatchetHeaderProto header = 1;`
    - `bytes nonce = 2;`
    - `bytes ciphertext = 3;`
  - `GroupSessionPackageProto`:
    - `bytes conversation_id = 1;`
    - `uint32 iteration = 2;`
    - `bytes ciphertext = 3;`
    - `bytes author_signature = 4;`
  - `HandshakeInvitationPackageProto`:
    - `bytes initiator_identity_key = 1;`
    - `bytes initiator_ephemeral_key = 2;`
    - `bytes signed_prekey_id = 3;`
    - `bytes onetime_prekey_id = 4;`
    - `bytes encrypted_payload = 5;`

#### 5. Application Plugins (`chat.proto`, `discovery.proto`)
- `chat.proto`: `TextMessageDto`, `ReadReceiptDto`, `DeliveredReceiptDto`, `EmojiAnnotationDto`, `SenderKeyDistributionDto`, `GroupUpdateDto`, `ProfileUpdateDto`.
- `discovery.proto`: `DhtPingPayload`, `DhtPongPayload`, `DhtFindNodeRequest`, `DhtFindNodeResponse`, `NodeInfoProto`.

#### Serialization Adapters:
- **`ProtobufSessionWirePacker`** (`Percolator.Application.Ports.ISessionWirePacker`):
  - Packs domain cryptographic elements (`RatchetHeader`, `nonce`, `ciphertext`) into Protobuf `DirectSessionPackageProto` or `GroupSessionPackageProto`.
  - Wraps packed payloads into `SealedEnvelopeProto` with target `BlindedRoutingToken` and `DeliveryToken`.
  - Unpacks incoming `SealedEnvelopeProto` records into typed `InboundDirectEnvelope` or `InboundGroupEnvelope` instances for `Application`.
- **`ProtobufPayloadSerializer`** (`Percolator.PluginSdk.IPayloadSerializer`):
  - Serializes C# DTOs to binary using `Google.Protobuf.CodedOutputStream` and `IBufferWriter<byte>`.
  - Deserializes binary spans into strongly-typed C# DTOs via `Google.Protobuf.MessageParser<T>`.

---

### 4.3 Persistence, Database Repositories & Fast-Path Query Services

Database Engine: Encrypted SQLite using SQLCipher (`SQLitePCLRaw.bundle_e_sqlcipher` with EF Core or Dapper).

#### Architectural Guardrails for Persistence:
1. **Modular Schema Partitioning vs. Monolithic DbContext**:
   - Rather than repeating the legacy pitfall of a single monolithic 38-`DbSet` `PercolatorDbContext`, database access is cleanly partitioned:
     - `OperationalDbContext`: High-frequency transactional outbox jobs, inbound ingress queues, and live transport state.
     - `VaultDbContext`: Encrypted identity keys, signed pre-keys, and ratchet session states.
     - `ChatHistoryDbContext`: Channel messages, reaction blobs, and delivery receipts.
   - Prevents table lock contention on SQLite, accelerates application cold starts, and minimizes migration friction.
2. **Deterministic Cryptographic BLOB Mapping**:
   - EF Core LINQ equality over `byte[]` arrays (`x.RatchetPublicKey == key.ToArray()`) translates to pointer/reference comparisons or generates runtime evaluation warnings.
   - Repositories must configure explicit EF Core value converters with deterministic `ValueComparer<byte[]>` structural byte comparisons or utilize raw parameterized SQL / Dapper for indexed BLOB lookups.
3. **Defense-in-Depth Cryptographic Column Encryption**:
   - Replaces the legacy vulnerability where private keys (`ExportECPrivateKey()`) and chain keys (`ChainKey`, `SignatureKey`) were stored in raw plaintext BLOBs.
   - Key material stored on disk must always have column-level encryption using master keys derived from `ICredentialStorage`.

#### 1. Domain Aggregate Repositories (Write / Consistency Enforcing)
- **`IOutboxRepository`** (`Percolator.Application.Delivery.Ports`):
  - Persists outbound messages (`OutboxJob`) in transactional storage.
  - Supports FIFO retrieval of pending jobs, status progression (`Pending` $\rightarrow$ `InFlight` $\rightarrow$ `Delivered` / `Failed`), and persona black-holing (`PauseJobsForIdentityAsync`).
- **`IPeerContactRepository`** (`Percolator.Domain.Identities.Ports`):
  - Stores `PeerContact` records, primary identity keys, authorized secondary devices, and trust levels.
- **`IRatchetSessionRepository`** (`Percolator.Application.Ports`):
  - Persists active `DirectRatchetSession` states (root key, current chain keys, skipped message key cache) under encryption.
- **`IGroupReceiverSessionRepository`** (`Percolator.Application.Ports`):
  - Persists active `GroupReceiverSession` states (channel, author ID, author device ID, current iteration, chain key, skipped message keys) under encryption.
- **`IGroupSenderKeyRepository`** (`Percolator.Application.Ports`):
  - Persists active `GroupSenderKeyRatchet` states (channel, author ID, author device ID, current iteration, chain key, Ed25519 signing key) under encryption.
- **`IPendingHandshakeRepository`** (`Percolator.Application.Ports`):
  - Persists pending inbound handshake envelopes (`InboundHandshakeEnvelope`) and encrypted greeting payloads while an inbound contact request is awaiting user approval (`PendingApproval`).
  - Supports retrieval by `(RecipientIdentityId, SenderIdentityId)` and atomic deletion upon handshake completion or contact rejection.
- **`IPrivatePreKeyStore`** (`Percolator.Domain.Identities.Ports`):
  - Persists local private signed pre-keys and pools of private one-time pre-keys, supporting atomic retrieval and consumption by key ID for inbound X3DH responder handshakes.
- **`IGroupCredentialsRepository`** (`Percolator.Domain.Security.Ports`):
  - Persists client-side `GroupCredentials` (master keys, auth credential MACs, blob keys) under encryption.
- **`IChannelRepository`** (`Percolator.Domain.Channels.Ports`):
  - Persists direct channels (`DirectChannel`) and group channels (`GroupChannel`) with member roles, epochs, and encrypted payload logs (`ChannelPayload`). Serves as the single authoritative persistence store for all channel state across applications.
- **`IRelayPreKeyDirectoryRepository`** (`Percolator.Domain.Relays.Ports`):
  - Backs the relay pre-key hosting directory with paging, expiration cleanup, and quota enforcement.
- **`IUnknownGroupMessageCacheRepository`** (`Percolator.Apps.Chat` port):
  - Persists bounded out-of-order group messages awaiting author sender key distribution.
- **`ITransferSessionRepository`** (`Percolator.Apps.FileTransfer.Ports`):
  - Persists active and historical file transfer sessions, manifests, bitfields, and swarm peer caches.

#### 2. Fast-Path Read Query Adapters (Bypassing Domain Aggregates via Direct SQL / Dapper Projections)
- **`SqlChatMessageQueryService`** (`Percolator.Apps.Chat.Ports.IChatMessageQueryService`):
  - Queries indexed `ChannelPayloads` and joined reaction/receipt tables via direct paged SQL (`WHERE ChannelId = @channelId AND Sequence < @beforeSeq ORDER BY Sequence DESC LIMIT @limit`). Returns lightweight `ChatMessageReadModel` instances without hydrating domain aggregates.
- **`SqlConversationListQueryService`** (`Percolator.Apps.Chat.Ports.IConversationListQueryService`):
  - Direct SQL query aggregating active channels, last message snippet, timestamp, and unread counts for the UI channel list.
- **`SqlGroupInvitationQueryService`** (`Percolator.Apps.Chat.Ports.IGroupInvitationQueryService`):
  - Fast query for pending group invitations awaiting user consent.
- **`SqlOutboxQueryService`** (`Percolator.Application.Delivery.Ports.IOutboxQueryService`):
  - Reads pending/failed outbox job summaries for UI diagnostic dashboards without hydrating byte payload buffers.
- **`SqlPeerContactQueryService`** (`Percolator.Application.Ports.IPeerContactQueryService`):
  - Fetches contact book rows, pending contact requests (`ContactState.PendingApproval`), and reachability summaries without loading private keys or device link proofs. Projects `ContactState State` into `PeerContactSummaryReadModel` and `PeerContactDetailReadModel` for UI contact lists and incoming request approval badges.
- **`SqlDiscoveryQueryService`** (`Percolator.Apps.Discovery.Ports.IDiscoveryQueryService`):
  - Reads active peer presence entries and locator cache records directly.
- **`SqlFileTransferQueryService`** (`Percolator.Apps.FileTransfer.Ports.IFileTransferQueryService`):
  - Reads active file transfer task progress, byte rates, and manifest catalogs without locking active chunk transfer state machines.

---

### 4.4 Ingress Edge Filtering, Stream Registry & Envelope Unwrapping (`Percolator.Application.Ports`)
- **Adapters**:
  - **`IIngressFilterService`** (`Percolator.Application.Ports`):
    - Enforces blacklist filtering against blocked identities (`PeerTrustLevel.Blocked`), sender rate limits, and identity dormancy status.
  - **`IStreamRegistry`** (`Percolator.Application.Ports`):
    - Tracks active, open gRPC duplex/server-streaming connections for direct peers and home relays.
    - Detects HTTP/2 connection drops and backpressure flow-control saturation (`StreamWriteResult`).
  - **`IPeerReachabilityService`** (`Percolator.Application.Routing`):
    - Evaluates direct reachability (via active connection or discovery rendezvous ticket) and resolves home relay mailboxes for offline peers.
  - **`SealedEnvelopeUnwrapper`** (`Percolator.Application.Ports.ISealedEnvelopeUnwrapper`):
    - Bridges relay `MailboxEnvelope` instances to strongly typed `InboundEnvelope` objects using `ProtobufSessionWirePacker`.

---

### 4.5 Network Transport, Streaming & Dispatching
- **Adapters**:
  - **`ITransportDispatcher`** (`Percolator.Application.Delivery.Ports`):
    - Direct 1:1: Invokes `UnauthenticatedDeliveryService.DeliverDirect` against peer's ephemeral TLS endpoint.
    - Relayed 1:1: Invokes `UnauthenticatedDeliveryService.EnqueueMailbox` against recipient's home relay using `DeliveryToken`.
    - Relayed Group: Invokes `AnonymousGroupRelayService.DispatchGroupMessage` against shared channel relay with ZK membership proof.
  - **`UnauthenticatedDeliveryEndpoint` (ASP.NET Core gRPC Service)**:
    - Implements `UnauthenticatedDeliveryService`.
    - Validates packet size and `DeliveryToken` validity, invokes ingress edge filters (`IIngressFilterService`), and hands unpacked envelopes to `InboundIngressPipeline`.
  - **Primary Container Service Activator (`PrimaryContainerServiceActivator<T>`)**:
    - *Pattern*: Implements `IGrpcServiceActivator<T>` to bridge Kestrel's secondary gRPC listener container to the host application's primary DI container (`IServiceProvider`).
    - *Lifecycle*: Dynamically creates an `IServiceScope` from the primary container per incoming gRPC request, resolves service dependencies, and guarantees deterministic scope cleanup in `ReleaseAsync()`.
  - **gRPC Interceptor Pipeline**:
    - **`IdentityReadinessInterceptor`**: Global interceptor that verifies `IActiveIdentityAccessor.IsActive`. If the local node or identity vault is locked (e.g., awaiting master passphrase unlock), rejects incoming RPCs with `StatusCode.Unavailable` ("Node not ready (no active identity)."), protecting uninitialized stores and crypto engines.
    - **`DeliveryCertificateAuthInterceptor`**: Intercepts authenticated relay and pre-key gRPC routes (Unary and Duplex streaming) to validate caller metadata (`x-percolator-sender-public-identity-id`, anti-replay `x-percolator-timestamp` window, and `x-percolator-signature` challenge proof) before handler execution.
  - **Thread-Safe gRPC Channel Factory with `Lazy<T>` (`PeerGrpcChannelFactory`)**:
    - Employs `ConcurrentDictionary<string, Lazy<GrpcChannel>>` keyed by `https://{host}:{port}`.
    - The `Lazy<GrpcChannel>` wrapper guarantees that only a single physical HTTP/2 connection pool is created even under concurrent dial races.
    - **Teardown Policy**: On application shutdown, concurrently calls `channel.ShutdownAsync()` with a 2-second timeout, followed by `.Dispose()` on all channels to terminate hung OS socket handles cleanly.
  - **Bounded Live Relay Stream Dispatcher**:
    - Replaces legacy unbounded channels with `Channel.CreateBounded<ServerRelayStream>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait })` to prevent out-of-memory memory leaks when streaming to slow or degraded clients.
    - Uses concurrent tasks to separate incoming client ACKs (`requestStream.ReadAllAsync`) from outgoing live frame pumping (`responseStream.WriteAsync`).
    - Flushes offline/pending outbox egress jobs atomically upon client connection before streaming live frames.
    - Unregisters channels cleanly in a `finally` block on client disconnect.
  - **`RelayMailboxStreamWorker` (`IHostedService`)**:
    - Maintains resilient duplex stream to user's home relay via `AuthenticatedRelayService.ConnectMailboxStream`.
    - Handles challenge-response authentication with local `IdentityKey`.
    - Receives queued `SealedEnvelopeProto` messages, feeds them into `InboundIngressPipeline`, and returns `MailboxAckProto` confirmations upon successful persistence.
    - Automatically reconnects with exponential backoff and jitter on socket/HTTP-2 disconnects.
  - **`RelayGroupStreamWorker` (`IHostedService`)**:
    - Manages live server-streaming subscriptions (`AnonymousGroupRelayService.SubscribeGroupStream`) to host relays.
    - Presents ZK membership proofs for each active group channel.
    - Feeds broadcast group envelopes directly into `InboundIngressPipeline`.
  - **Ephemeral Direct P2P TLS 1.3 Transport (Anonymity & Blind-Trust Architecture)**:
    - **Security Architecture**: TLS does *not* authenticate peers; authentic end-to-end identity and secrecy is provided entirely by Signal Double Ratchet & X3DH application payloads. The transport layer's sole responsibility is wire encryption and network-level metadata obfuscation (masking HTTP/2 framing, gRPC route names, and packet lengths from passive network observers).
    - **Blind Trust Client Verification**: `SocketsHttpHandler` configures `RemoteCertificateValidationCallback = (_, _, _, _) => true`. This decouples TLS certificates from permanent identity keys, preventing network-level linkability/correlation of IP endpoints to identities.
    - **SSRF & LAN Endpoint Boundary Validation (`CallbackEndpointValidator`)**:
      - Remote peer endpoints and callback hosts provided via invitation links (`percolator://{host}[:{port}]/{publicKey}`) or discovery records are strictly validated before network connections are initiated.
      - Automatically rejects loopback (`127.0.0.1`), private RFC1918 subnets (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), link-local (`169.254.0.0/16`), and IPv6 private/ULA/link-local (`fc00::/7`, `fe80::/10`) to defend against Server-Side Request Forgery and malicious intranet port scanning, unless explicitly permitted by developer configuration flags (`AllowLoopback = true`, `AllowLan = true`).
    - **Dynamic Port Range Probing & Multi-Identity Contention**:
      - The protocol decouples clients and identities from strict single-port bindings. Each running node/identity operates across a sane, configurable port range (e.g. 5200–5299, roughly one port per active identity/relay).
      - **Optional Invitation Port**: When an invitation link omits the port (`percolator://{host}/{publicKey}`), the P2P transport dialer attempts connections across the configured port range to bring the direct channel online. Only when a user explicitly specifies a strict port in the invitation link does the dialer bypass port range probing.
      - **Port Contention Resolution**: On startup, nodes query active listening ports and dynamically claim an open port within the range (`SqliteReservedPortQuery`), enabling seamless multi-identity operation on the same machine without port collision.
    - **Ephemeral Server Certificate Generation (`TransportCertificateProvider`)**:
      - On application/identity startup, generates a fresh, disposable ECDSA P-256 (`ECCurve.NamedCurves.nistP256`) keypair and self-signed certificate with `CN=Percolator-Ephemeral`, `KeyUsageFlags.DigitalSignature`, `EnhancedKeyUsage` (ServerAuthentication `1.3.6.1.5.5.7.3.1`), and loopback SANs (`localhost`, `127.0.0.1`, `::1`).
      - **Windows SChannel / CNG Invariant**: Windows SChannel fails TLS 1.3 server handshakes with ephemeral in-memory certificates. On Windows, the certificate must be exported as PKCS#12 (`.pfx`) bytes and loaded from disk using `X509CertificateLoader.LoadPkcs12FromFile(..., X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet)`. On Linux/macOS, ephemeral in-memory certificates are used directly without filesystem round-trips.
      - **Antivirus Write Retry**: Writing the `.pfx` file on Windows triggers asynchronous Windows Defender scans, holding shared file locks. The certificate writer must implement an exponential retry policy (`WriteCertificateWithRetryAsync`) to avoid `IOException` sharing violations.
      - **CNG Key Deletion on Rotation**: Upon certificate rotation or shutdown on Windows, explicitly delete the CNG key container via `((ECDsaCng)cert.GetECDsaPrivateKey()).Key.Delete()` to avoid leaking orphaned cryptographic keys in the Windows CNG store.
  - **Relay Client Adapter**:
    - Publishes `PreKeyBundle` uploads to relay identities via `AuthenticatedRelayService.PublishPreKeyBundle`.
    - Fetches remote peer pre-key bundles from relays out-of-band via `AuthenticatedRelayService.FetchPreKeyBundle`, authenticating with local `IdentityKey` signature to defeat OPK exhaustion attacks.
    - Queues store-and-forward mailbox envelopes via `UnauthenticatedDeliveryService.EnqueueMailbox` using recipient `DeliveryToken` / `BlindedRoutingToken`.

---

### 4.6 Platform & Discovery Adapters
- **`IDateTimeProvider`** (`Percolator.Domain.Common`):
  - `SystemDateTimeProvider` delegating to `DateTimeOffset.UtcNow`.
- **Cross-Platform `ICredentialStorage`** (Security Provider):
  - Replaces the legacy Windows-only DPAPI (`ProtectedData.Protect`) coupling with an OS-agnostic credential provider:
    - **Windows**: Windows DPAPI / Windows Credential Manager with ACLs granting exclusive access to the current user.
    - **Linux**: FreeDesktop Secret Service API (`libsecret` via DBus) with desktop keyring integration.
    - **macOS**: Apple Keychain Services (`Security.framework`).
    - **Headless / Linux Fallback**: Master passphrase key derivation using Argon2id with an AES-256-GCM encrypted local keyfile.
- **`KademliaRoutingTable`** (`Percolator.Apps.Discovery`):
  - Manages 160-bit XOR distance metrics, $k=20$ K-bucket storage, and node contact tables for decentralized rendezvous discovery. (Note: UDP LAN discovery is explicitly excluded).

---

### 4.7 Transient State Cleanup & Pruning Services (Research & Design)
- **Problem Statement**: Multiple transient and ephemeral tables accumulate stale records that must be pruned periodically without locking active database transactions or degrading ingress throughput.
- **Research Scope & Target Adapters**:
  - **`OutboxRetentionPruner`**: Evaluates retention window policies for `OutboxJob` rows (e.g., pruning `Delivered` jobs older than 7 days, purging dead-letter jobs that have exceeded `MaxRetryCount` and manual inspection windows).
  - **`RelayPreKeyDirectoryPruner`**: Evicts expired signed pre-key bundles and consumed or timed-out one-time pre-keys from `IRelayPreKeyDirectoryRepository` environmental records.
  - **`UnknownGroupMessageCachePruner`**: Purges buffered group chat frames from `IUnknownGroupMessageCacheRepository` that exceed maximum TTL (e.g. 48 hours) where the author's sender key distribution was never received.
  - **`AbandonedInvitePruner`**: Scans inbound contact requests (`PeerContactState.PendingApproval`), pending handshake envelopes in `IPendingHandshakeRepository`, and group invitations (`PendingGroupInvitation`) exceeding local expiration policies (e.g. 14 days without user response) and marks or purges them.
  - **`RendezvousTicketPruner`**: Scans DHT presence announcements in `Apps.Discovery` past `ExpiresAtUtc` and evicts expired routing entries.
- **Background Worker Resiliency Best Practices** (Synthesized from Legacy Lessons):
  - **Async Scopes**: Workers must use `IServiceScopeFactory.CreateAsyncScope()` for deterministic asynchronous resource cleanup.
  - **Time Virtualization**: Depend strictly on .NET `TimeProvider` (`timeProvider.GetUtcNow()`, `Task.Delay(..., timeProvider, ct)`) to enable deterministic time manipulation and fast-forwarding in integration test suites.
  - **Proactive Soft Expiry**: Workers refreshing certificates, pre-keys, or tokens must inspect soft thresholds (e.g. `ExpiresAtUtc < now + TimeSpan.FromHours(4)`) rather than waiting for hard expiration, preventing downtime during transient network outages.
  - **Granular Error Discrimination**:
    - `OperationCanceledException`: Clean shutdown loop termination.
    - `CryptographicException`: Fatal configuration/corruption error; worker logs critical alert and terminates without busy-looping.
    - Transient network exceptions (`RpcException(Unavailable)`, `HttpRequestException`, `TimeoutException`): Triggers exponential backoff with jitter.
  - **Per-Item Fault Isolation**: Batch processing loops must wrap each entity/peer evaluation in an isolated `try/catch` block so a failure with a single peer cannot abort the entire background sweep.
  - **Bounded Batches**: Background pruners execute as scheduled `IHostedService` cron workers utilizing batch deletion with SQLite `LIMIT` clauses to avoid long-lived database write locks.

---

### 4.8 Encrypted Blob Storage, Chunk Streaming & Relay Pruning Adapters (`Percolator.Application.Ports`)
- **`StreamingAesGcmCryptoService`** (`Percolator.Application.Ports.IBlobCryptoService`):
  - **EXIF & Geolocation Metadata Scrubbing**: Strips invasive EXIF/XMP tags (GPS coordinates, camera serial numbers, device model) from raw image/video streams client-side before padding and encryption, guaranteeing complete location privacy.
  - **Stepped Bucket Padding**: Applies PKCS#7 or ISO/IEC 7816-4 padding to discrete boundaries (< 1 MB to 32 KB; 1–10 MB to 256 KB; > 10 MB to 1 MB) to prevent traffic analysis and file fingerprinting.
  - **Chunked STREAM AEAD**: Encrypts and decrypts in fixed $64\text{ KB}$ chunks using .NET `AesGcm` with sequential counter nonces ($\text{Nonce}_i = N_{\text{base}} \oplus i$) and 16-byte authentication tags.
  - **Bounded Memory & Zero LOH Allocations**: Rents chunk buffers from `ArrayPool<byte>.Shared`, ensuring memory consumption never exceeds $\le 64\text{ KB}$ regardless of multi-megabyte file size.
- **`RelayBlobClient`** (`Percolator.Application.Ports.IRelayBlobClient`):
  - High-performance HTTP/2 or gRPC streaming client communicating with the channel's designated relay.
  - **Streaming Upload (`POST /blobs`)**: Streams chunked ciphertext out-of-band with real-time `TransferProgress` telemetry (bytes transferred, rate, ETA) and cancellation support. Attaches authentication headers (signed ticket for 1:1 relay; Group Epoch Token for group channels).
  - **Resumable Download (`GET /blobs/{sha256}`)**: Supports HTTP `Range: bytes={start}-{end}` to resume dropped downloads at chunk boundaries without restarting from byte 0.
- **`PeerStreamBlobClient`** (`Percolator.Application.Ports.IPeerStreamBlobClient`):
  - Multiplexed data stream client operating over active direct peer connections registered in `IStreamRegistry`.
  - Serves chunks on demand for 1:1 Direct P2P channels where both peers are online.
- **`LocalFileChunkStore`** (`Percolator.Application.Ports.ILocalChunkStore`):
  - Persistent file-system backed chunk store located in the application data directory (`%AppData%/Percolator/Blobs/`).
  - **Atomic File Writes**: Writes incoming chunks to a `.tmp` file and atomically commits via `File.Move(..., overwrite: true)` upon completion to prevent corrupted partial files.
  - **Content-Addressed Storage**: Files stored as `{sha256Hex}.blob`.
- **`RelayBlobRetentionPruner`** (`IHostedService`):
  - Background cron worker on relays executing periodic sweeps to delete expired blobs based on TTL (default 14 days).
  - Enforces disk storage high/low watermarks (evicting oldest expired blobs if disk usage exceeds 90%).

---

### 4.9 High-Performance File Transfer Adapters, Multi-Stream Engine & Relay Pipes (`Percolator.Apps.FileTransfer.Ports`)

Concrete realization of out-of-band high-speed croc-inspired and BitTorrent swarm mechanics:

#### 1. Multi-Stream Sockets & Binary Framing Engine
- **`TcpMultiStreamPool` & `TransferStream`** (`ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`):
  - Opens and manages a pool of 4 to 8 parallel multiplexed TCP/QUIC connections with `TCP_NODELAY = true`.
  - Implements the binary length-prefixed framing wire protocol:
    - `0x01 STREAM_HANDSHAKE`: Handshake with `SessionId`, `StreamIndex`, and `StreamAuthToken`.
    - `0x02 BITFIELD`: Bitfield declaration of possessed chunks.
    - `0x03 HAVE`: Chunk verification announcement.
    - `0x04 REQUEST_CHUNK` & `0x05 CHUNK_DATA`: 1 MB chunk streaming with AES-256-GCM authentication tags.
    - `0x06 CANCEL_CHUNK`: Endgame chunk cancellation.
    - `0x07 CHOKE` / `0x08 UNCHOKE`: Transport backpressure signaling.
    - `0x09 KEEP_ALIVE`: Liveness ping frames.
    - `0x0A PEX_PEERS`: Out-of-band peer exchange gossip frames.
  - Work-stealing scheduler: Detects stalled streams and redistributes in-flight chunk requests to faster streams in the pool.

#### 2. Channel Relay Transfer Pipe (Client & Relay Service Endpoint)
- **`RelayTransferPipeClient`**:
  - Connects 4 to 8 parallel streams to the channel's designated relay endpoint: `POST /transfer-pipes/{sessionId}` (or WebSocket `GET /transfer-pipes/{sessionId}/ws`).
  - Presents `PipeAuthToken = HMAC-SHA256(K_transfer, "relay-pipe-rendezvous")` in HTTP headers (`X-Pipe-Auth`).
  - Operates as a transparent binary stream pipe to the paired peer.
- **Relay Server Pipe Service (Relay-Side Execution)**:
  - Pairs incoming sender and receiver streams by `(SessionId, StreamIndex)`.
  - Executes zero-copy bidirectional socket splicing (`System.IO.Pipelines` or `PipeReader.CopyToAsync`).
  - **Zero Disk Usage**: Pure memory buffer pipe (bounded to 2 MB buffer per stream).
  - **Operator Opt-In Safeguards**: Configured via `RelayFileTransferCapabilities`:
    - `SupportsTransferPipes`: If false, relay rejects connection with HTTP 403 Forbidden.
    - `MaxPipeBandwidthBytesPerSec`: Bandwidth throttling per active pipe session.
    - `MaxConcurrentPipes`: Rejects new pipe negotiations if concurrent limit is reached.
    - Idle timeout: Closes pipe after 60 seconds of silence.

#### 3. Relay Blind Swarm Tracker (Client & Relay Service Endpoint)
- **`RelaySwarmTrackerClient`** (`IRelaySwarmTrackerClient`):
  - Sends out-of-band HTTP `POST /swarms/{blindSwarmId}/announce` to channel relay.
  - Blind identifier: $\text{BlindSwarmId} = \text{HMAC-SHA256}(K_{\text{transfer}}, \text{"swarm-rendezvous"})$.
  - Receives list of active candidate endpoints for the swarm without touching in-band ratchet channels.
- **Relay Server Swarm Tracker Endpoint (Relay-Side Execution)**:
  - In-memory thread-safe dictionary: `ConcurrentDictionary<byte[], SwarmPeerRegistry>`.
  - Ephemeral 90-second sliding expiration. Entries purged if heartbeat stops.
  - Zero disk storage; zero awareness of channel identity or file contents.
  - Opt-in enforcement: If `SupportsSwarmTracker = false`, returns HTTP 501 Not Implemented, causing clients to fall back strictly to out-of-band PEX.

#### 4. Desktop Sparse File Storage & Atomic File Finalization
- **`DesktopSparseFileStore`** (`IDesktopFileStorage`):
  - **Sparse File Pre-Allocation (Cross-Platform)**:
    - Opens target files with `FileShare.ReadWrite | FileShare.Delete`.
    - **Windows**: Invokes `DeviceIoControl` with `FSCTL_SET_SPARSE` (IOCTL `0x000900C4`) to enable NTFS sparse file allocation.
    - **Linux/macOS**: Invokes `fallocate` / `posix_fallocate` or advances end-of-file pointer via `FileStream.SetLength`.
    - Allocates multi-gigabyte files instantaneously without zero-filling or physical disk block pre-allocation.
  - **File Staging with `.percolator-part`**:
    - Writes incoming chunks to `{relativePath}.percolator-part` using `RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory<byte>, fileOffset, ct)`.
    - Protects incomplete files against antivirus scans, search indexers, and accidental user execution.
  - **Atomic Finalization**:
    - Upon complete chunk verification of all blocks belonging to a file:
    - Flushes file buffers to disk.
    - Atomically renames `{relativePath}.percolator-part` to `{relativePath}` via `File.Move(..., overwrite: true)`.
    - Sets `File.SetLastWriteTimeUtc` from manifest timestamp.
  - **Resumption Bitfield Scanner**:
    - Reads existing `.percolator-part` and completed files on disk in 1 MB blocks.
    - Computes SHA-256 over each block and compares against `ChunkHashes` to construct the resumption `Bitfield`.

#### 5. Bandwidth Rate Limiter
- **`TokenBucketRateLimiter`** (`ITransferRateLimiter`):
  - Implements high-precision token-bucket algorithm using `PeriodicTimer` or fractional nanosecond timestamp tracking.
  - Enforces global speed caps (`MaxDownloadBytesPerSec`, `MaxUploadBytesPerSec`) and per-session overrides.
  - Dynamically throttles `Stream.ReadAsync` and `Stream.WriteAsync` loops in `TcpMultiStreamPool`.

#### 6. SQLite Repositories for File Transfer
- Implements `ITransferSessionRepository`:
  - `ft_transfer_sessions`: Stores session state, channel ID, peer identity, root name, metrics, manifest blob ID, session key, base nonce, destination path, and status.
  - `ft_manifest_files`: Stores individual file entries and bundle byte offsets.
  - `ft_session_bitfields`: Stores binary chunk bitfield state for active downloads.
  - `ft_swarm_peers`: Caches known active swarm peer endpoints.
