# Percolator.Infrastructure2

## 1. Overview & Architectural Role

`Percolator.Infrastructure2` provides concrete technical adapters, storage repositories, network transport clients/servers, and cryptographic primitives for the Percolator microkernel architecture.

Following Clean Architecture principles:
- **Dependencies Flow Inward**: `Infrastructure2` references `Percolator.Domain`, `Percolator.Application2`, and `Percolator.PluginSdk`. It does NOT expose infrastructure-specific types (e.g., SQLite connections, gRPC stubs, raw sockets) to the domain or application layers.
- **Port Realization**: Every component in this project implements an interface (port) defined by `Percolator.Domain` or `Percolator.Application2`.

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

### 4.1 Cryptography (`Percolator.Domain.Security.Ports.ICryptoEngine`)
- **Adapter**: `SignalCryptoEngine` / `SodiumCryptoEngine`
- **Responsibilities**:
  - **Ed25519 Signatures**: Signs and verifies device link proofs and pre-key signatures (`VerifyEd25519Signature(IdentityKey, ...)`).
  - **Curve25519 (X25519) Diffie-Hellman**: Computes scalar multiplication between private keys and public DH keys (`ComputeDiffieHellman`, `DeriveX3dhMasterSecret`).
  - **HKDF / HMAC Ratchet Steps**: Performs SHA-256 HMAC-based root key updates (`KdfRk`) and symmetric chain key advances (`StepRatchet`).
  - **Authenticated Encryption**: AES-256-GCM / ChaCha20-Poly1305 authenticated symmetric encryption (`EncryptAesGcm`, `DecryptAesGcm`).

### 4.2 Persistence & Database Repositories
- **Database Engine**: Encrypted SQLite using SQLCipher (`SQLitePCLRaw.bundle_e_sqlcipher` with EF Core or Dapper).
- **Adapters**:
  - **`IOutboxRepository`** (`Percolator.Application2.Delivery.Ports`):
    - Persists outbound messages (`OutboxJob`) in transactional storage.
    - Supports FIFO retrieval of pending jobs, status progression (`Pending` $\rightarrow$ `InFlight` $\rightarrow$ `Delivered` / `Failed`), and persona black-holing (`PauseJobsForIdentityAsync`).
  - **`IPeerContactRepository`**:
    - Stores `PeerContact` records, primary identity keys, authorized secondary devices, and trust levels.
  - **`IRatchetSessionRepository`**:
    - Persists active `DirectRatchetSession` states (root key, current chain keys, skipped message key cache) under encryption.
  - **`IRelayPreKeyDirectoryRepository`**:
    - Backs the relay pre-key hosting directory with paging, expiration cleanup, and quota enforcement.

### 4.3 Network Transport & Dispatcher
- **Adapters**:
  - **`ITransportDispatcher`** (`Percolator.Application2.Delivery.Ports`):
    - Implements outbound message dispatching.
    - Routes `OutboxJob` packets via direct gRPC P2P channel or gRPC Relay Mailbox service based on `IRoutingCoordinator` directives.
  - **`IInboundIngressService` gRPC Server Endpoint**:
    - ASP.NET Core gRPC service exposing `DeliverOpaqueMessage(OpaqueEnvelopeRequest)` to receive incoming frames from peers or relays.
    - Validates packet size, invokes ingress edge filters, and hands payloads to `IInboundIngressService`.
  - **Relay Client Adapter**:
    - Publishes `PreKeyBundle` uploads to relay identities.
    - Fetches remote peer pre-key bundles from relays out-of-band.
    - Queues and fetches store-and-forward mailbox envelopes via `DeliveryToken` / `BlindedRoutingToken`.

### 4.4 Platform & Storage Adapters
- **`IDateTimeProvider`** (`Percolator.Domain.Common`):
  - `SystemDateTimeProvider` delegating to `DateTimeOffset.UtcNow`.
- **`IFileChunkStorage`** (`Percolator.Apps.FileTransfer`):
  - Streams chunk payloads to/from local disk with SHA-256 / Merkle root integrity verification.
