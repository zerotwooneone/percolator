# Percolator.Infrastructure2

## 1. Overview & Architectural Role

`Percolator.Infrastructure2` provides concrete technical adapters, storage repositories, network transport clients/servers, cryptographic primitives, and binary serialization engines for the Percolator microkernel architecture.

Following Clean Architecture principles:
- **Dependencies Flow Inward**: `Infrastructure2` references `Percolator.Domain`, `Percolator.Application2`, and `Percolator.PluginSdk`. It does NOT expose infrastructure-specific types (e.g., SQLite connections, gRPC stubs, Protobuf classes, raw sockets) to the domain or application layers.
- **Port Realization**: Every component in this project implements an interface (port) defined by `Percolator.Domain`, `Percolator.Application2`, or `Percolator.PluginSdk`.
- **Wire Contract & Serialization Ownership**: Concrete Protobuf `.proto` schemas, code-generated message classes, and binary serializers live strictly within `Percolator.Infrastructure2.Serialization`. The inner layers interact solely via pure C# DTOs and the `IPayloadSerializer` port.

---

## 2. Legacy Migration & Cutover Strategy

The legacy codebase contains overlapping, dated modules:
- `Percolator.Identity`
- `Percolator.Cryptography`
- `Percolator.Network`
- `Percolator.Infrastructure`
- `Percolator.Application`

### Cutover Workflow
1. **Domain & Application Completion**: Implement and verify `Percolator.Domain`, `Percolator.Application2`, application plugins (`Percolator.Apps.Chat`, `Discovery`, `FileTransfer`), and their test suites.
2. **Project Deletion**: Remove the legacy projects from the solution and delete their directories before implementing `Percolator.Infrastructure2` and updating the desktop application.
3. **Compiler-Error-Driven Triage**:
   - The resulting compiler breaks in the desktop app and infrastructure serve as an intentional audit trail.
   - For every breaking symbol/class, decide deliberately:
     - **Keep & Convert**: If an existing adapter (e.g., SQLCipher schema setup or gRPC proto definitions) can be refactored to implement the clean domain/application ports.
     - **Delete & Rewrite**: If the legacy class contains domain leakage, state coupling, or obsolete patterns.

---

## 3. High-Level Requirements

1. **Zero Domain Logic in Infrastructure**: Infrastructure adapters must solely translate between external APIs/data formats and domain value objects / entities.
2. **Zero Plaintext Sensitive State at Rest**: All private keys, ratchet chain keys, and session secrets stored on disk must be encrypted using SQLCipher or OS DPAPI/keychain.
3. **Deterministic Memory Zeroization**: Any unmanaged buffers or cryptographic key spans must be cleared (`CryptographicOperations.ZeroMemory`) when disposed.
4. **Resilient Network Handling**: All gRPC network calls must obey cancellation tokens, transport timeouts, and surface transient vs. permanent network failures cleanly to `OutboxRetryPolicy`.

---

## 4. Required Port Implementations & Specific Adapters

### 4.1 Cryptography (`Percolator.Domain.Security.Ports.ICryptoEngine` & `IZkProofEngine`)
- **Adapter**: `SignalCryptoEngine` / `SodiumCryptoEngine` (`ICryptoEngine`)
  - **Ed25519 Signatures**: Signs and verifies device link proofs and pre-key signatures (`VerifyEd25519Signature(IdentityKey, ...)`).
  - **Curve25519 (X25519) Diffie-Hellman**: Computes scalar multiplication between private keys and public DH keys (`ComputeDiffieHellman`, `DeriveX3dhMasterSecret`).
  - **HKDF / HMAC Ratchet Steps**: Performs SHA-256 HMAC-based root key updates (`KdfRk`) and symmetric chain key advances (`StepRatchet`).
  - **Authenticated Encryption**: AES-256-GCM authenticated symmetric encryption (`EncryptAesGcm`, `DecryptAesGcm`).
- **Adapter**: `ZkgroupCryptographyService` (`IZkProofEngine`)
  - **Native zkgroup FFI**: Wraps `Signal.Interop` native C/Rust binaries with safe handle management.
  - **ZK Group Presentations**: Generates and verifies zero-knowledge membership proofs for anonymous group relay interactions (`VerifyGroupPresentation`, `GenerateGroupPresentation`).

### 4.2 Serialization & Wire Contracts (`Percolator.PluginSdk.IPayloadSerializer`)
- **Protobuf Schemas (`Protos/`)**:
  - `chat.proto`: Wire contracts for `TextMessageDto`, `ReactionDto`, `ReceiptDto`, `SenderKeyDistribution`.
  - `discovery.proto`: Wire contracts for `DhtPingPayload`, `DhtPongPayload`, `DhtFindNodePayload`.
  - `filetransfer.proto`: Wire contracts for `FileManifestDto`, `ManifestQueryDto`, `TransferNegotiationDto`.
- **Adapter**: `ProtobufPayloadSerializer` (`IPayloadSerializer`):
  - Serializes C# DTOs to binary using `Google.Protobuf.CodedOutputStream` and `IBufferWriter<byte>`.
  - Deserializes binary spans into strongly-typed C# DTOs via `Google.Protobuf.MessageParser<T>`.

### 4.3 Persistence & Database Repositories
- **Database Engine**: Encrypted SQLite using SQLCipher (`SQLitePCLRaw.bundle_e_sqlcipher` with EF Core or Dapper).
- **Adapters**:
  - **`IOutboxRepository`** (`Percolator.Application2.Delivery.Ports`):
    - Persists outbound messages (`OutboxJob`) in transactional storage.
    - Supports FIFO retrieval of pending jobs, status progression (`Pending` $\rightarrow$ `InFlight` $\rightarrow$ `Delivered` / `Failed`), and persona black-holing (`PauseJobsForIdentityAsync`).
  - **`IPeerContactRepository`** (`Percolator.Domain.Identities.Ports`):
    - Stores `PeerContact` records, primary identity keys, authorized secondary devices, and trust levels.
  - **`IRatchetSessionRepository`** (`Percolator.Application2.Ports`):
    - Persists active `DirectRatchetSession` states (root key, current chain keys, skipped message key cache) under encryption.
  - **`IChannelRepository`** (`Percolator.Domain.Channels.Ports`):
    - Persists direct channels (`DirectChannel`) and group channels (`GroupChannel`) with member roles, epochs, and encrypted payload logs (`ChannelPayload`). Serves as the single authoritative persistence store for all channel state across applications.
  - **`IRelayPreKeyDirectoryRepository`** (`Percolator.Domain.Relays.Ports`):
    - Backs the relay pre-key hosting directory with paging, expiration cleanup, and quota enforcement.
  - **`IUnknownGroupMessageCacheRepository`** (`Percolator.Apps.Chat` port):
    - Persists bounded out-of-order group messages awaiting author sender key distribution.
  - **`IManifestCatalogRepository`** (`Percolator.Apps.FileTransfer` port):
    - Indexes hosted and remote file manifests (`FileManifest`), chunk hashes, and Merkle root trees.

### 4.4 Ingress Edge Filtering & Stream Registry (`Percolator.Application2.Ports`)
- **Adapters**:
  - **`IIngressFilterService`** (`Percolator.Application2.Ports`):
    - Enforces blacklist filtering against blocked identities (`PeerTrustLevel.Blocked`), sender rate limits, and identity dormancy status.
  - **`IStreamRegistry`** (`Percolator.Application2.Ports`):
    - Tracks active, open gRPC duplex/server-streaming connections for direct peers and home relays.
    - Detects HTTP/2 connection drops and backpressure flow-control saturation (`StreamWriteResult`).
  - **`IPeerReachabilityService`** (`Percolator.Application2.Routing`):
    - Evaluates direct reachability (via active connection or discovery rendezvous ticket) and resolves home relay mailboxes for offline peers.

### 4.5 Network Transport, Streaming & Dispatching
- **Adapters**:
  - **`ITransportDispatcher`** (`Percolator.Application2.Delivery.Ports`):
    - Routes `OutboxJob` packets via direct gRPC P2P channel or gRPC Relay Mailbox service based on `DeliveryChannelType`.
  - **`IInboundIngressService` gRPC Server Endpoint**:
    - ASP.NET Core gRPC service exposing `DeliverOpaqueMessage(OpaqueEnvelopeRequest)` to receive incoming frames from peers or relays.
    - Validates packet size, invokes ingress edge filters (`IIngressFilterService`), and hands payloads to `InboundIngressPipeline`.
  - **`RelayGroupStreamWorker` (`IHostedService`)**:
    - Manages live, resilient server-streaming gRPC subscriptions (`RelayGroupService.SubscribeGroupStream`) to host relays.
    - Implements automatic reconnection with exponential backoff and jitter upon network drops.\
    - Feeds streamed envelopes directly into `InboundIngressPipeline`.
  - **Direct P2P TLS Certificate Pinning**:
    - Configures custom `RemoteCertificateValidationCallback` on gRPC `SocketsHttpHandler`.
    - Validates self-signed peer certificates by verifying certificate thumbprints match the pinned `IdentityKey` of the remote contact.
  - **Relay Client Adapter**:
    - Publishes `PreKeyBundle` uploads to relay identities.
    - Fetches remote peer pre-key bundles from relays out-of-band.
    - Queues and fetches store-and-forward mailbox envelopes via `DeliveryToken` / `BlindedRoutingToken`.

### 4.6 Platform, Discovery & Out-of-Band Transfer Adapters
- **`IDateTimeProvider`** (`Percolator.Domain.Common`):
  - `SystemDateTimeProvider` delegating to `DateTimeOffset.UtcNow`.
- **`ICredentialStorage`** (Security Provider):
  - `DpapiCredentialService` utilizing Windows DPAPI (`ProtectedData.Protect`/`Unprotect`) and local filesystem ACLs to safeguard SQLCipher encryption passphrases and root identity seed keys.
- **`UdpLanDiscoveryAdapter`** (`Percolator.Apps.Discovery`):
  - Manages UDP broadcast socket (`ReuseAddress`, `EnableBroadcast = true`) for local subnet peer auto-discovery without external server dependencies.
- **`KademliaRoutingTable`** (`Percolator.Apps.Discovery`):
  - Manages 160-bit XOR distance metrics, $k=20$ K-bucket storage, and node contact tables for decentralized rendezvous discovery.
- **`IOutBandTransferAdapter` & `IFileChunkStorage`** (`Percolator.Apps.FileTransfer`):
  - Dedicated out-of-band binary transfer adapter (raw TCP/QUIC data streams) bypassing domain Double Ratchet channels for multi-megabyte/gigabyte payload streaming.
  - Streams chunk payloads to/from local disk with SHA-256 / Merkle root integrity verification.

### 4.7 Transient State Cleanup & Pruning Services (Research & Design)
- **Problem Statement**: Multiple transient and ephemeral tables accumulate stale records that must be pruned periodically without locking active database transactions or degrading ingress throughput.
- **Research Scope & Target Adapters**:
  - **`OutboxRetentionPruner`**: Evaluates retention window policies for `OutboxJob` rows (e.g., pruning `Delivered` jobs older than 7 days, purging dead-letter jobs that have exceeded `MaxRetryCount` and manual inspection windows).
  - **`RelayPreKeyDirectoryPruner`**: Evicts expired signed pre-key bundles and consumed or timed-out one-time pre-keys from `IRelayPreKeyDirectoryRepository`.
  - **`UnknownGroupMessageCachePruner`**: Purges buffered group chat frames from `IUnknownGroupMessageCacheRepository` that exceed maximum TTL (e.g. 48 hours) where the author's sender key distribution was never received.
  - **`AbandonedInvitePruner`**: Scans inbound contact requests (`PeerContactState.PendingApproval`) and group invitations (`PendingGroupInvitation`) exceeding local expiration policies (e.g. 14 days without user response) and marks or purges them.
  - **`RendezvousTicketPruner`**: Scans DHT presence announcements in `Apps.Discovery` past `ExpiresAtUtc` and evicts expired routing entries.
- **Design Invariant**: Background pruners execute as scheduled `IHostedService` cron workers utilizing batch deletion with SQLite `LIMIT` clauses to avoid long-lived database write locks.
