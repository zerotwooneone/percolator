# Percolator.Infrastructure Implementation Plan

## Refinement Status Gate
- [ ] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [ ] Implementation Gate: LOCKED (Refinement in progress)

---

## 1. Plan Overview & Architectural Context

`Percolator.Infrastructure` provides concrete technical adapters, persistent storage repositories, network transport clients/servers, cryptographic primitives, and binary serialization engines for the Percolator microkernel.

- **Deep Technical Reference**: For complete database schemas, wire framing protocols, Protobuf definitions, and cryptographic invariants, see [technical-notes.md](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/technical-notes.md).
- **Core Guardrails**:
  - Target standard `net10.0` with zero Windows/macOS locks.
  - Two-Stage Container: Stage 1 (Pre-Unlock Bootstrap) $\rightarrow$ Stage 2 (Authenticated Host via `IStage2HostFactory`).
  - Zero DPAPI master key persistence (keys live exclusively in pinned unmanaged memory via `IMasterKeyHandle`).
  - Native C-API SQLCipher keying via `SQLitePCL.raw.sqlite3_key` (trapping native error code 26 `SQLITE_NOTADB`).
  - Vertical feature slices co-locating data models, read query services, write repositories, and background pruners.
  - Co-located unit test suites with low-brittle assertions (no mock verification counts, virtual `FakeTimeProvider`).

---

## 2. Implementation Phases & Vertical Feature Slices

```
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 1: Bootstrapping & Cryptographic Primitives                      │
│ ├─ Chunk 1.1: Project Scaffolding & Core Platform Primitives           │
│ ├─ Chunk 1.2: Core Cryptographic & ZK-Proof Engines                    │
│ ├─ Chunk 1.3: Vault Protobuf Schema, Header Storage & Crypto-Shredder  │
│ └─ Chunk 1.4: Composable SQLCipher Unlock Orchestration                │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 2: Core Relational Persistence & Identity Vault                  │
│ ├─ Chunk 2.1: SQLite DbContext, Value Comparers & Baseline Migrations  │
│ ├─ Chunk 2.2: Vertical Slice: Identity Key Stores & Ratchet Sessions   │
│ ├─ Chunk 2.3: Vertical Slice: Outbox Jobs & Fast-Path Query Engine     │
│ └─ Chunk 2.4: Vertical Slice: Contacts & Channel Repositories          │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 3: Network Transport, Wire Protocols & Delivery Dispatching      │
│ ├─ Chunk 3.1: Core Delivery & Session Wire Schemas (Protos/)           │
│ ├─ Chunk 3.2: Session Wire Packaging & Stepped Bucket Serializer       │
│ ├─ Chunk 3.3: Transport Primitives, Interceptors & Endpoint Validation │
│ ├─ Chunk 3.4: Ingress Filtering & Transport Route Dispatching          │
│ └─ Chunk 3.5: gRPC Ingress Endpoints & Hosted Relay Workers            │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 4: Vertical Plugin Slices (End-to-End Capabilities)              │
│ ├─ Chunk 4.1: Vertical Slice: Chat Messaging, History & Query Engine   │
│ ├─ Chunk 4.2: Vertical Slice: Peer Discovery & Rendezvous Engine       │
│ └─ Chunk 4.3: Vertical Slice: Media Streaming & Swarm File Transfers   │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 5: Operational Workers, Two-Stage Composition Root & System Verif│
│ ├─ Chunk 5.1: Scheduled Operational Pruning Workers                    │
│ └─ Chunk 5.2: Stage 2 DI Composition Root & Infrastructure Verification│
└────────────────────────────────────────────────────────────────────────┘
```

---

### Phase 1: Bootstrapping & Cryptographic Primitives

#### Chunk 1.1: Project Scaffolding & Core Platform Primitives
- **Scope & Deliverables**:
  - [ ] Scaffold `Percolator.Infrastructure/Percolator.Infrastructure.csproj` targeting `net10.0`, nullable enabled, referencing `Percolator.Domain`, `Percolator.Application`, and `Percolator.PluginSdk`.
    - Package dependencies:
      - `Microsoft.EntityFrameworkCore.Sqlite.Core` (v10.x)
      - `SQLitePCLRaw.bundle_e_sqlcipher` (v2.1.x+)
      - `Konscious.Security.Cryptography.Argon2` (v1.3.x)
      - `Google.Protobuf` (v3.25.2)
      - `Grpc.Tools` (v2.57.0)
  - [ ] Implement `SystemDateTimeProvider` (`Percolator.Domain.Common.IDateTimeProvider`) returning `DateTimeOffset.UtcNow`.
  - [ ] Implement RFC 4122 Big-Endian UUID extension (`Guid.ToByteArray(bigEndian: true)`).
  - [ ] Scaffold `Percolator.Infrastructure.Tests` test project targeting `net10.0` with xUnit, FluentAssertions, and FakeItEasy.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Common/`)**:
  - `GuidExtensionsTests.ToBigEndianByteArray_Formats_ValidRfc4122ByteOrder`: GUID bytes match big-endian RFC 4122 byte order. Low-brittle guarantee: asserts raw byte ordering against RFC vector.
  - `SystemDateTimeProviderTests.UtcNow_ReturnsCurrentTime`: returns `DateTimeOffset.UtcNow` within reasonable system clock tolerance.
- **Verification Gate**:
  - `dotnet build Percolator.Infrastructure`
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Common`

#### Chunk 1.2: Core Cryptographic & ZK-Proof Engines
- **Scope & Deliverables**:
  - [ ] Implement `SignalCryptoEngine` / `SodiumCryptoEngine` (`Percolator.Domain.Security.Ports.ICryptoEngine`):
    - Ed25519 signatures and verification (`VerifyEd25519Signature`, `SignEd25519`).
    - Curve25519 (X25519) Diffie-Hellman scalar multiplication (`ComputeDiffieHellman`, `DeriveX3dhMasterSecret`).
    - HKDF SHA-256 HMAC root key update (`KdfRk`) and symmetric chain key advances (`StepRatchet`).
    - AES-256-GCM authenticated encryption/decryption (`EncryptAesGcm`, `DecryptAesGcm`).
    - Memory zeroization with `CryptographicOperations.ZeroMemory` on sensitive spans.
    - Logging guardrail: redact all keys when `CryptographyOptions.EnableCryptographicMaterialLogging = false`.
  - [ ] Implement `ZkgroupCryptographyService` (`Percolator.Domain.Security.Ports.IZkProofEngine`):
    - Wraps native `Signal.Interop` / `libzkgroup` C-ABI with safe handle management.
    - Generates and verifies zero-knowledge group membership proofs (`VerifyGroupPresentation`, `GenerateGroupPresentation`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Security/`)**:
  - `SignalCryptoEngineTests.Ed25519_SignAndVerify_MatchesRfc8032Vectors`: known test vectors match RFC 8032. Low-brittle guarantee: asserts mathematical outputs; zero mock coupling.
  - `SignalCryptoEngineTests.Curve25519_DiffieHellman_MatchesRfc7748Vectors`: known test vectors match RFC 7748. Low-brittle guarantee: asserts mathematical outputs.
  - `SignalCryptoEngineTests.AesGcm_EncryptDecrypt_SucceedsAndRejectsBitflip`: encrypts payload, decrypts successfully; single bitflip throws authenticated tag mismatch.
  - `ZkgroupCryptographyServiceTests.VerifyGroupPresentation_ValidProof_ReturnsSuccess`: presentation proof generates and validates against group public credentials.
  - `SignalCryptoEngineTests.LoggingGuardrail_RedactsSensitiveKeys_WhenLoggingDisabled`: asserts logger redacts all key bytes when material logging is disabled.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Security.Crypto`

#### Chunk 1.3: Vault Protobuf Schema, Header Storage & Crypto-Shredder
- **Technical Specifications**:
  - Protobuf layout in `DatabaseHeader.proto`: `version` (int32), `salt` (16 bytes), `master_slot_nonce` (12 bytes), `master_slot_payload` (48 bytes: 32-byte master key + 16-byte Poly1305 tag), `duress_slot_nonce` (12 bytes), `duress_slot_payload` (48 bytes: 32-byte sentinel + tag, or uniform dummy random noise).
  - Plausible deniability invariant: dummy duress slot is filled with CSPRNG random bytes of identical length.
  - Shredding pipeline: overwrite header with random bytes and `Flush(flushToDisk: true)` before truncation and deletion in $< 2\text{ ms}$; catch `IOException` on locked DB files without failing.
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/DatabaseHeader.proto`.
  - [ ] Implement `ProtobufHeaderStorage` (`Percolator.Infrastructure.Vault.IHeaderStorage`): reads/writes header with temp file staging and atomic replace.
  - [ ] Implement `DuressWipeHandler` (`Percolator.Application.Ports.IDuressWipeHandler`): instant crypto-shredding pipeline.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/`)**:
  - `ProtobufHeaderStorageTests.PlausibleDeniability_WireSize_IdenticalWithOrWithoutDuress`: header with dummy random noise vs active duress has identical 48-byte payload wire size; decryption of dummy duress slot throws `AuthenticationTagMismatchException`.
  - `ProtobufHeaderStorageTests.CorruptedHeader_TrapsGracefully`: 0-byte or truncated `percolator.header` traps `InvalidProtocolBufferException` returning structured failure without uncaught crashes.
  - `DuressWipeHandlerTests.ExecuteWipeAsync_OverwritesHeaderAndFlushes_InLessThan2Ms`: overwrites header with random bytes and flushes to disk in $< 2\text{ ms}$.
  - `DuressWipeHandlerTests.ExecuteWipeAsync_LockedDbFile_ContinuesWithoutThrowing`: simulates external file lock on `percolator.db`; catches `IOException` without throwing; destroys header permanently.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Vault.Storage`

#### Chunk 1.4: Composable SQLCipher Unlock Orchestration
- **Technical Specifications**:
  - Decompose unlock orchestration into composable, single-responsibility collaborators:
    1. `IVaultEnvelopePacker`: Derives Argon2id keys and evaluates ChaCha20-Poly1305 master/duress slots.
    2. `IDatabaseCanaryProbe`: Executes `SELECT count(*) FROM sqlite_master;` on isolated probe connection, explicitly trapping native error code 26 (`SQLITE_NOTADB`).
    3. `IDatabaseMigrator`: Responsible for executing EF Core migrations (`Database.MigrateAsync()`).
    4. `IStage2HostFactory`: Assembles the Stage 2 authenticated host.
  - `MasterKeyHandle`: Allocates and pins 32-byte unmanaged memory; passes raw span to `SQLitePCL.raw.sqlite3_key`.
  - `SqlCipherPragmaInterceptor`: Configures `PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;`.
  - `SqlCipherDatabaseUnlockCoordinator`: Coordinates status determination, initialization, unlock, envelope re-wrapping, and session relock.
- **Scope & Deliverables**:
  - [ ] Implement `MasterKeyHandle` (`Percolator.Domain.Security.Ports.IMasterKeyHandle`).
  - [ ] Implement `SqlCipherPragmaInterceptor` (`Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor`).
  - [ ] Implement `VaultEnvelopePacker` (`Percolator.Infrastructure.Vault.IVaultEnvelopePacker`).
  - [ ] Implement `DatabaseCanaryProbe` (`Percolator.Infrastructure.Vault.IDatabaseCanaryProbe`).
  - [ ] Implement `SqlCipherDatabaseUnlockCoordinator` (`Percolator.Application.Ports.IDatabaseUnlockCoordinator`).
  - [ ] Implement `AuthenticatedSession` (`Percolator.Application.Ports.IAuthenticatedSession`).
  - [ ] Implement `InfrastructureServiceCollectionExtensions.AddBootstrapInfrastructure(IServiceCollection, IConfiguration)`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/Unlock/`)**:
  - `MasterKeyHandleTests.Dispose_ZeroizesPinnedMemory`: unmanaged buffer memory is zeroized on dispose; subsequent access throws `ObjectDisposedException`.
  - `DatabaseCanaryProbeTests.InvalidKey_TrapsSqliteErrorCode26`: invalid key traps `SqliteException` with `SqliteErrorCode == 26` (`SQLITE_NOTADB`) and returns controlled failure.
  - `VaultEnvelopePackerTests.MasterPasswordChange_RewrapsSlot_PreservesDuress`: re-wraps master slot with new key; preserves salt and duress slot; decrypts successfully.
  - `SqlCipherUnlockTests.HappyPathMasterUnlock_CoordinatesCollaborators`: correct master password derives key, evaluates master slot, executes canary probe, invokes migrator, and returns `UnlockResult.Success(session)`.
  - `SqlCipherUnlockTests.InvalidPassword_ReturnsInvalidCredentials`: incorrect password fails master and duress slots; returns `UnlockResult.InvalidCredentials`.
  - `SqlCipherUnlockTests.LockAsync_ScrubsMemoryAndClearsPools`: calling `LockAsync()` clears connection pools (`SqliteConnection.ClearAllPools()`), zeroizes `MasterKeyHandle`, disposes host, transitions status to `Locked`.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Vault.Unlock`

---

### Phase 2: Core Relational Persistence & Identity Vault

#### Chunk 2.1: SQLite DbContext, Value Comparers & Baseline Migrations
- **Scope & Deliverables**:
  - [ ] Implement `PercolatorDbContext` with configurations for operational outbox (`ops_*`), cryptographic vault (`vault_*`), and channels/contacts.
  - [ ] Implement EF Core deterministic BLOB mapping with `ValueComparer<byte[]>`.
  - [ ] Implement column-level encryption value converters for private keys and chain keys.
  - [ ] Implement `SqliteDatabaseMigrator` (`Percolator.Infrastructure.Vault.IDatabaseMigrator`): executes EF Core migrations against `PercolatorDbContext`.
  - [ ] Create initial EF Core database migration for `percolator.db`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Core/`)**:
  - `PercolatorDbContextTests.ColumnLevelEncryption_StoresKeysAsCiphertextAtRest`: direct byte inspection of raw SQLite database confirms plaintext private keys are absent.
  - `PercolatorDbContextTests.BlobValueComparer_DetectsByteArrayEquality`: queries by byte array values and asserts match success.
  - `SqliteDatabaseMigratorTests.MigrateAsync_AppliesInitialSchema`: executes migration and verifies table existence.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Core`

#### Chunk 2.2: Vertical Slice: Identity Key Stores & Ratchet Sessions
- **Scope & Deliverables**:
  - [ ] Implement Cryptographic Vault Repositories:
    - `SqliteLocalIdentityKeyStore` (`Percolator.Application.Ports.ILocalIdentityKeyStore`)
    - `SqlitePrivatePreKeyStore` (`Percolator.Domain.Identities.Ports.IPrivatePreKeyStore`)
    - `SqliteRatchetSessionRepository` (`Percolator.Application.Ports.IRatchetSessionRepository`)
    - `SqliteGroupReceiverSessionRepository` (`Percolator.Application.Ports.IGroupReceiverSessionRepository`)
    - `SqliteGroupSenderKeyRepository` (`Percolator.Application.Ports.IGroupSenderKeyRepository`)
    - `SqlitePendingHandshakeRepository` (`Percolator.Application.Ports.IPendingHandshakeRepository`)
    - `SqliteGroupCredentialsRepository` (`Percolator.Domain.Security.Ports.IGroupCredentialsRepository`)
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Vault/`)**:
  - `SqliteRatchetSessionRepositoryTests.SaveAndLoad_PreservesCiphertextAndState`: saves and reloads ratchet session state atomically.
  - `SqliteGroupSenderKeyRepositoryTests.AtomicCommit_RollsBackOnFailure`: asserts all child records update together or throw without orphan rows.
  - `SqliteLocalIdentityKeyStoreTests.StoreAndRetrieveIdentity_MatchesKey`: stores local identity key and asserts decrypted roundtrip.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Vault`

#### Chunk 2.3: Vertical Slice: Outbox Jobs & Fast-Path Query Engine
- **Scope & Deliverables**:
  - [ ] Implement Outbox Persistence & CQRS Read Model:
    - `SqliteOutboxRepository` (`Percolator.Application.Delivery.Ports.IOutboxRepository`): write-side enqueue, dequeue, retry tracking.
    - `SqlOutboxQueryService` (`Percolator.Application.Delivery.Ports.IOutboxQueryService`): fast-path diagnostic summary without hydrating byte payload buffers.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Outbox/`)**:
  - `SqliteOutboxRepositoryTests.EnqueueAndDequeue_TransitionsStateCleanly`: jobs advance cleanly from `Queued` to `InFlight` and `Delivered`.
  - `SqlOutboxQueryServiceTests.GetOutboxSummary_ProjectsLightweightModel`: verifies projection contains counts and statuses without payload loading.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Outbox`

#### Chunk 2.4: Vertical Slice: Contacts & Channel Repositories
- **Scope & Deliverables**:
  - [ ] Implement Contacts, Channels & Relay Ledger Repositories:
    - `SqlitePeerContactRepository` (`Percolator.Domain.Identities.Ports.IPeerContactRepository`)
    - `SqlPeerContactQueryService` (`Percolator.Application.Ports.IPeerContactQueryService`): fast-path summary and detail read models.
    - `SqliteChannelRepository` (`Percolator.Domain.Channels.Ports.IChannelRepository`)
    - `SqliteRelayPreKeyRepository` (`Percolator.Domain.Delivery.Ports.IRelayPreKeyRepository`)
    - `SqliteRelayProfileRepository` (`Percolator.Domain.Delivery.Ports.IRelayProfileRepository`)
    - `SqliteRelayLedgerRepository` (`Percolator.Domain.Delivery.Ports.IRelayLedgerRepository`)
    - `SqlitePeerProfileRepository` (`Percolator.PluginSdk.Profiles.IPeerProfileRepository`)
    - `SqlitePeerMailboxRoutingRepository` (`Percolator.PluginSdk.Profiles.IPeerMailboxRoutingRepository`)
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Contacts/`)**:
  - `SqlitePeerContactRepositoryTests.SaveContact_UpdatesTrustLevelAndTimestamps`: updates contact state and persists atomically.
  - `SqlPeerContactQueryServiceTests.GetContacts_ProjectsSummaryList`: projects contacts and pending requests into summary view.
  - `SqliteChannelRepositoryTests.SaveChannel_PersistsParticipants`: persists channel and associated participant metadata.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Contacts`

---

### Phase 3: Network Transport, Wire Protocols & Delivery Dispatching

#### Chunk 3.1: Core Delivery & Session Wire Schemas (`Protos/`)
- **Scope & Deliverables**:
  - [ ] Configure `Google.Protobuf` and `Grpc.Tools` in `Percolator.Infrastructure.csproj`.
  - [ ] Author Protobuf schemas in `Percolator.Infrastructure/Protos/`:
    - `unauthenticated_delivery.proto`
    - `authenticated_relay.proto`
    - `anonymous_group.proto`
    - `session.proto`
  - [ ] Verify clean compilation of generated C# classes.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Protos/`)**:
  - `CoreProtoCompilationTests.AllProtos_GenerateValidClrTypes`: tests that message types instantiate and default-serialize without schema errors.
- **Verification Gate**:
  - `dotnet build Percolator.Infrastructure`

#### Chunk 3.2: Session Wire Packaging & Stepped Bucket Serializer
- **Scope & Deliverables**:
  - [ ] Implement `ProtobufSessionWirePacker` (`Percolator.Application.Ports.ISessionWirePacker`):
    - Packs `DirectSessionPackageProto` and `GroupSessionPackageProto`.
    - Wraps into `SealedEnvelopeProto` with recipient `BlindedRoutingToken` and `DeliveryToken`.
    - Unpacks incoming `SealedEnvelopeProto` records into typed `InboundDirectEnvelope` or `InboundGroupEnvelope`.
  - [ ] Implement `ProtobufPayloadSerializer` (`Percolator.PluginSdk.IPayloadSerializer`):
    - Binary serialization via `Google.Protobuf.CodedOutputStream` and `IBufferWriter<byte>`.
    - Span-based deserialization via `Google.Protobuf.MessageParser<T>`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Packaging/`)**:
  - `ProtobufSessionWirePackerTests.RoundtripDirectSession_PacksAndUnpacksCorrectly`: wraps and unwraps typed `InboundDirectEnvelope`.
  - `ProtobufSessionWirePackerTests.ReplyContext_ExcludesQuoteSnippetFromWire`: quote snippet text is strictly absent from wire payload (only parent ID and author ID).
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Packaging`

#### Chunk 3.3: Transport Primitives, Interceptors & Endpoint Validation
- **Scope & Deliverables**:
  - [ ] Implement `InMemoryTransportCertificateProvider`:
    - Generates disposable ECDSA P-256 self-signed certificate with loopback SANs in memory with zero disk I/O.
  - [ ] Implement `CallbackEndpointValidator`:
    - Validates endpoints before connection; blocks loopback, RFC1918 private subnets, link-local unless explicit dev flags (`AllowLoopback = true`, `AllowLan = true`).
  - [ ] Implement dynamic port range probing (5200–5299) and port contention resolution via `ops_reserved_ports`.
  - [ ] Implement `PeerGrpcChannelFactory`:
    - Thread-safe channel cache using `ConcurrentDictionary<string, Lazy<GrpcChannel>>`.
    - Teardown policy with 2-second timeout and clean socket disposal.
  - [ ] Implement gRPC Interceptor Pipeline:
    - `IdentityReadinessInterceptor`: Rejects incoming RPCs with `StatusCode.Unavailable` if no active identity or vault locked.
    - `DeliveryCertificateAuthInterceptor`: Validates caller identity metadata, anti-replay timestamp, and signature proof.
  - [ ] Implement `PrimaryContainerServiceActivator<T>`:
    - Bridges Kestrel gRPC listener container to host DI container (`IServiceProvider`) with scoped lifetime cleanup.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Primitives/`)**:
  - `CallbackEndpointValidatorTests.RejectsPrivateAndLoopbackIps_UnlessDevFlag`: rejects loopback and RFC1918 IPs by default.
  - `IdentityReadinessInterceptorTests.VaultLocked_ReturnsUnavailable`: incoming RPC returns `StatusCode.Unavailable` when vault locked or stealth active.
  - `DeliveryCertificateAuthInterceptorTests.TimestampSkewExceeded_RejectsRequest`: rejects signatures with timestamp delta > 60s.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Transport.Primitives`

#### Chunk 3.4: Ingress Filtering & Transport Route Dispatching
- **Scope & Deliverables**:
  - [ ] Implement `IngressFilterService` (`Percolator.Application.Ports.IIngressFilterService`):
    - Enforces blacklist filtering (`PeerTrustLevel.Blocked`), sender rate limits, and identity dormancy status.
  - [ ] Implement `StreamRegistry` (`Percolator.Application.Ports.IStreamRegistry`):
    - Tracks active gRPC duplex and server-streaming connections for direct peers and home relays.
    - Detects connection drops and handles flow-control saturation (`StreamWriteResult.Dropped`).
  - [ ] Implement `TransportDispatcher` (`Percolator.Application.Delivery.Ports.ITransportDispatcher`):
    - Direct 1:1: dispatches `DeliverDirect` over ephemeral TLS.
    - Relayed 1:1: dispatches `EnqueueMailbox` to recipient's home relay using `DeliveryToken`.
    - Relayed Group: dispatches `DispatchGroupMessage` to shared group relay with ZK membership proof.
  - [ ] Implement `SealedEnvelopeUnwrapper` (`Percolator.Application.Ports.ISealedEnvelopeUnwrapper`):
    - Bridges relay `MailboxEnvelope` instances to strongly typed `InboundEnvelope` objects.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Dispatching/`)**:
  - `IngressFilterServiceTests.BlockedSender_RejectsEnvelopeBeforeCrypto`: envelopes from blocked identities rejected before any cryptographic operations.
  - `StreamRegistryTests.BackpressureSaturation_ReturnsDropped`: queue overflow returns `StreamWriteResult.Dropped`.
  - `TransportDispatcherTests.DirectVsRelayRoute_ResolvesCorrectAdapter`: resolves direct TLS vs relay delivery token cleanly.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Transport.Dispatching`

#### Chunk 3.5: gRPC Ingress Endpoints & Hosted Relay Workers
- **Scope & Deliverables**:
  - [ ] Implement `UnauthenticatedDeliveryEndpoint` (ASP.NET Core gRPC Service on relays and direct peer listening ports):
    - Handles `DeliverDirect`, `EnqueueMailbox`, `GetProfile`, `UploadProfile`, `DeleteProfile`, and `DownloadBlindBlob`.
  - [ ] Implement Duplex & Streaming Workers (`IHostedService`):
    - `RelayMailboxStreamWorker`: Duplex stream to home relay with automatic reconnection and jittered exponential backoff.
    - `RelayGroupStreamWorker`: Live subscription stream with ZK proofs for active group channels.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Endpoints/`)**:
  - `UnauthenticatedDeliveryEndpointTests.DeliverDirect_ValidEnvelope_ReturnsSuccessAck`: validates RPC execution and ack generation.
  - `RelayMailboxStreamWorkerTests.ConnectionDrop_RetriesWithExponentialBackoff`: tests automatic reconnection retry sequence.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Transport.Endpoints`

---

### Phase 4: Vertical Plugin Slices (End-to-End Capabilities)

#### Chunk 4.1: Vertical Slice: Chat Messaging, History & Query Engine
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/chat.proto` with stepped bucket padding.
  - [ ] Implement Chat Persistence & Fast-Path CQRS Query Services:
    - `SqlChatMessageRepository` (`Percolator.Apps.Chat.Ports.IChatMessageRepository`)
    - `SqlChatMessageQueryService` (`Percolator.Apps.Chat.Ports.IChatMessageQueryService`): paged SQL timeline read model.
    - `SqlConversationListQueryService` (`Percolator.Apps.Chat.Ports.IConversationListQueryService`): unread badges and last message snippet rollups.
    - `SqlGroupInvitationRepository` (`Percolator.Apps.Chat.Ports.IGroupInvitationRepository`)
    - `SqlGroupInvitationQueryService` (`Percolator.Apps.Chat.Ports.IGroupInvitationQueryService`)
    - `SqlUnknownGroupMessageCacheRepository` (`Percolator.Apps.Chat.Ports.IUnknownGroupMessageCacheRepository`)
  - [ ] Implement Chat Lifecycle Background Pruners:
    - `DisappearingMessageEvictionWorker`: 30s timer, purges expired messages and local blob caches.
    - `UnknownGroupMessageCachePruner`: 48h out-of-order buffer pruner.
    - `AbandonedInvitePruner`: stale invitation pruner.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/Chat/`)**:
  - `ProtobufPayloadSerializerTests.SteppedBucketPadding_ExpandsToExpectedBucketSize`: serialized chat frames expand to discrete 256/512/1024/2048/4096 B boundaries.
  - `SqlChatMessageRepositoryTests.DeleteMessage_CascadesAttachmentDeletion`: deleting a message cascades and deletes attachment records in SQLite.
  - `SqlChatMessageQueryServiceTests.PagedTimeline_ClampsToLimitAndOffset`: limit/offset paging clamps accurately to boundary without aggregate hydration.
  - `SqlConversationListQueryServiceTests.UnreadBadgeCount_AggregatesPastReadReceipt`: count aggregates messages strictly past read receipt timestamp.
  - `DisappearingMessageEvictionWorkerTests.ExpiredMessages_PurgedAfterVirtualClockAdvance`: advances virtual clock by 31s with `FakeTimeProvider`; asserts expired messages are deleted.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.Chat`

#### Chunk 4.2: Vertical Slice: Peer Discovery & Rendezvous Engine
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/discovery.proto`.
  - [ ] Implement Directory Persistence:
    - `SqliteRelayPeerDirectory` (`Percolator.Apps.Discovery.Features.RelayDirectoryHosting.IRelayPeerDirectory`).
  - [ ] Implement Discovery Adapters & Private Rendezvous:
    - `DiscoveryQueryClient` (`Percolator.Apps.Discovery.Ports.INearbyDiscoveryQueryClientPort`)
    - `DiscoveryHandshakeAdapter` (`Percolator.Apps.Discovery.Ports.IDiscoveryHandshakePort`)
    - `PreKeyAvailabilityAdapter` (`Percolator.Apps.Discovery.Ports.IPreKeyAvailabilityPort`)
    - `PreKeyBundleProviderAdapter` (`Percolator.Application.Ports.IPreKeyBundleProvider`)
    - `GrpcProfileDeliveryClient` (`Percolator.PluginSdk.Profiles.IProfileDeliveryClient`)
    - `PrivateRendezvousService`: 24-hour UTC epoch blinded locators.
  - [ ] Implement Directory Maintenance Pruners:
    - `RelayDiscoveryRegistrationPruner`: daily pruner for stale relay peer registrations.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/Discovery/`)**:
  - `PrivateRendezvousServiceTests.BlindedLocator_DeterministicWithin24hEpoch`: computes matching blinded locator within epoch; rolls over deterministically across 24h boundary.
  - `DiscoveryQueryClientTests.NearbyQuery_ClampsCountTo100`: verifies query parameters are strictly clamped.
  - `SqliteRelayPeerDirectoryTests.RegisterAndQuery_MatchesLocator`: registers peer descriptor and queries by blinded locator.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.Discovery`

#### Chunk 4.3: Vertical Slice: Media Streaming & Swarm File Transfers
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/file_transfer.proto`.
  - [ ] Implement Encrypted Blob Storage:
    - `StreamingAesGcmCryptoService` (`Percolator.Application.Ports.IBlobCryptoService`): EXIF scrubbing, chunked STREAM AEAD (64 KB blocks).
    - `LocalFileChunkStore` (`Percolator.Application.Ports.ILocalChunkStore`): content-addressed disk chunk storage (`%AppData%/Percolator/Blobs/{sha256Hex}.blob`).
    - `RelayBlobClient` (`Percolator.Application.Ports.IRelayBlobClient`): resumable upload and download with HTTP Range.
    - `PeerStreamBlobClient` (`Percolator.Application.Ports.IPeerStreamBlobClient`)
    - `ChannelRelayResolver` (`Percolator.Application.Ports.IChannelRelayResolver`)
  - [ ] Implement File Transfer Persistence & CQRS Query Services:
    - `SqliteTransferSessionRepository` (`Percolator.Apps.FileTransfer.Ports.ITransferSessionRepository`)
    - `SqliteFilterPolicyRepository` (`Percolator.Apps.FileTransfer.Ports.IFileTransferFilterPolicyRepository`)
    - `SqlFileTransferQueryService` (`Percolator.Apps.FileTransfer.Ports.IFileTransferQueryService`)
  - [ ] Implement Swarm Multi-Stream Engine & Desktop File Store:
    - `TcpMultiStreamPool` (`Percolator.Apps.FileTransfer.Ports.ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`): frames 0x01–0x0C.
    - `PortableDesktopFileStore` (`Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage`): pre-allocation, Merkle leaf scanner, Win32 reserved name sanitization.
    - `TokenBucketRateLimiter` (`Percolator.Apps.FileTransfer.Ports.ITransferRateLimiter`)
    - `NetworkInterfaceEndpointDiscoveryPort` (`Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort`)
    - `RelayTransferPipeClient` & Server Pipe Service
    - `RelaySwarmTrackerClient` (`Percolator.Apps.FileTransfer.Ports.IDistributedTrackerPort`)
  - [ ] Implement Transfer Maintenance Pruners:
    - `TransferOfferReaperWorker` (`Percolator.Apps.FileTransfer.Ports.ITransferOfferReaperService`): sweeps stale transfer offers older than 24h.
    - `SwarmEndpointRetentionPruner`: daily pruner for transient endpoints.
    - `RelayBlobRetentionPruner`: daily pruner for expired blobs and disk watermarks.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/FileTransfer/`)**:
  - `StreamingAesGcmCryptoServiceTests.ExifScrubbing_StripsGpsAndDeviceMetadata`: strips GPS and device tags from JPEG/PNG before upload.
  - `StreamingAesGcmCryptoServiceTests.ChunkedAead_SingleBitflip_FailsAuthentication`: injects 1 bitflip in 64 KB block; asserts authenticated decryption failure.
  - `PortableDesktopFileStoreTests.Sanitization_RejectsReservedWin32NamesAndAds`: rejects `CON`, `PRN`, `AUX`, `:` ADS, and path traversals without touching disk.
  - `PortableDesktopFileStoreTests.ZeroByteFile_MaterializesImmediately`: creates zero-byte file immediately without `.percolator-part`.
  - `PortableDesktopFileStoreTests.BitfieldScanner_MatchesMerkleLeaves`: validates existing chunks against Merkle leaf hashes.
  - `TcpMultiStreamPoolTests.FrameSerialization_All12Frames_RoundtripsSuccessfully`: serializes and deserializes all 12 frames (0x01–0x0C).
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.FileTransfer`

---

### Phase 5: Operational Workers, Two-Stage Composition Root & System Alignment

#### Chunk 5.1: Scheduled Operational Pruning Workers
- **Scope & Deliverables**:
  - [ ] Implement Scheduled `IHostedService` Pruning Workers:
    - `OutboxRetentionPruner`: Daily pruner for delivered outbox jobs older than 7 days.
    - `RelayPreKeyDirectoryPruner`: Daily pruner for expired prekey bundles from `IRelayPreKeyRepository`.
    - `RelayProfileSlotPruner`: Daily pruner for expired profile slots from `relay_profile_slots`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Maintenance/`)**:
  - `OutboxRetentionPrunerTests.DeliveredJobs_OlderThan7Days_Purged`: advances virtual clock by 8 days with `FakeTimeProvider`; asserts old delivered jobs purged while recent jobs preserved.
  - `PrunerLockSafetyTests.RetentionPruning_UsesBatchingUnderConcurrency`: deletion uses `LIMIT` clauses to avoid SQLite lock escalation under concurrent read workload.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Maintenance`

#### Chunk 5.2: Stage 2 DI Composition Root & Infrastructure Verification
- **Scope & Deliverables**:
  - [ ] Implement `Stage2HostFactory`:
    - Assembles the Stage 2 post-unlock `IHost`.
    - Binds the active `IMasterKeyHandle`, registers `SqlCipherPragmaInterceptor` with `PercolatorDbContext`, and executes `Database.MigrateAsync()` prior to yielding the operational container.
  - [ ] Implement `InfrastructureServiceCollectionExtensions.AddAuthenticatedInfrastructure(IServiceCollection, IConfiguration, IMasterKeyHandle)`:
    - Registers `IMasterKeyHandle` (instance passed from unlock).
    - Registers `SqlCipherPragmaInterceptor`.
    - Registers `PercolatorDbContext` configured with `SqlCipherPragmaInterceptor`.
    - Registers cryptographic engines, wire packers, serializers, repositories, and query services.
    - Registers transport dispatchers, gRPC channels, interceptors, and background workers.
    - Registers `Stage2HostFactory`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Composition/`)**:
  - `DependencyInjectionTests.All58Ports_ResolveFromContainerWithoutCycles`: resolves every planned infrastructure port from DI container; asserts non-null without circular dependency or missing registration.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Composition`

---

## 3. Testing Strategy & Anti-Pattern Guardrails

To guarantee robust infrastructure without introducing brittle tests that break on refactoring:
- **Strictly Banned Anti-Patterns**:
  - Never verify exact mock call counts unless testing idempotency.
  - Never use private member reflection.
  - Never assert serialized JSON or raw string output format; assert observable typed properties and state changes.
  - Never rely on wall-clock `Task.Delay` or timers; inject and advance `FakeTimeProvider`.

---

## 4. Port-to-Adapter Realization Matrix

| # | Port (Interface) | Defining Project | Concrete Adapter | Target Chunk |
|---|---|---|---|---|
| 1 | `ICryptoEngine` | `Percolator.Domain` | `SignalCryptoEngine` / `SodiumCryptoEngine` | Chunk 1.2 |
| 2 | `IZkProofEngine` | `Percolator.Domain` | `ZkgroupCryptographyService` | Chunk 1.2 |
| 3 | `IDateTimeProvider` | `Percolator.Domain.Common` | `SystemDateTimeProvider` | Chunk 1.1 |
| 4 | `IDatabaseUnlockCoordinator` | `Percolator.Application` | `SqlCipherDatabaseUnlockCoordinator` | Chunk 1.4 |
| 5 | `IAuthenticatedSession` | `Percolator.Application` | `AuthenticatedSession` | Chunk 1.4 |
| 6 | `IMasterKeyHandle` | `Percolator.Domain.Security.Ports` | `MasterKeyHandle` | Chunk 1.4 |
| 7 | `IDuressWipeHandler` | `Percolator.Application` | `DuressWipeHandler` | Chunk 1.3 |
| 8 | `IHeaderStorage` | `Percolator.Infrastructure.Vault` (Internal) | `ProtobufHeaderStorage` | Chunk 1.3 |
| 9 | `ISessionWirePacker` | `Percolator.Application` | `ProtobufSessionWirePacker` | Chunk 3.2 |
| 10 | `IPayloadSerializer` | `Percolator.PluginSdk` | `ProtobufPayloadSerializer` | Chunk 3.2 |
| 11 | `DbConnectionInterceptor` | `Microsoft.EntityFrameworkCore.Diagnostics` | `SqlCipherPragmaInterceptor` | Chunk 1.4 |
| 12 | `IOutboxRepository` | `Percolator.Application` | `SqliteOutboxRepository` | Chunk 2.3 |
| 13 | `IPeerContactRepository` | `Percolator.Domain` | `SqlitePeerContactRepository` | Chunk 2.4 |
| 14 | `IRatchetSessionRepository` | `Percolator.Application` | `SqliteRatchetSessionRepository` | Chunk 2.2 |
| 15 | `IGroupReceiverSessionRepository` | `Percolator.Application` | `SqliteGroupReceiverSessionRepository` | Chunk 2.2 |
| 16 | `IGroupSenderKeyRepository` | `Percolator.Application` | `SqliteGroupSenderKeyRepository` | Chunk 2.2 |
| 17 | `IPendingHandshakeRepository` | `Percolator.Application` | `SqlitePendingHandshakeRepository` | Chunk 2.2 |
| 18 | `IPrivatePreKeyStore` | `Percolator.Domain` | `SqlitePrivatePreKeyStore` | Chunk 2.2 |
| 19 | `ILocalIdentityKeyStore` | `Percolator.Application` | `SqliteLocalIdentityKeyStore` | Chunk 2.2 |
| 20 | `IGroupCredentialsRepository` | `Percolator.Domain` | `SqliteGroupCredentialsRepository` | Chunk 2.2 |
| 21 | `IChannelRepository` | `Percolator.Domain` | `SqliteChannelRepository` | Chunk 2.4 |
| 22 | `IRelayPreKeyRepository` | `Percolator.Domain` | `SqliteRelayPreKeyRepository` | Chunk 2.4 |
| 23 | `IRelayProfileRepository` | `Percolator.Domain` | `SqliteRelayProfileRepository` | Chunk 2.4 |
| 24 | `IPeerProfileRepository` | `Percolator.PluginSdk` | `SqlitePeerProfileRepository` | Chunk 2.4 |
| 25 | `IPeerMailboxRoutingRepository` | `Percolator.PluginSdk` | `SqlitePeerMailboxRoutingRepository` | Chunk 2.4 |
| 26 | `IRelayLedgerRepository` | `Percolator.Domain` | `SqliteRelayLedgerRepository` | Chunk 2.4 |
| 27 | `IChatMessageRepository` | `Percolator.Apps.Chat` | `SqlChatMessageRepository` | Chunk 4.1 |
| 28 | `IGroupInvitationRepository` | `Percolator.Apps.Chat` | `SqlGroupInvitationRepository` | Chunk 4.1 |
| 29 | `IUnknownGroupMessageCacheRepository` | `Percolator.Apps.Chat` | `SqlUnknownGroupMessageCacheRepository` | Chunk 4.1 |
| 30 | `ITransferSessionRepository` | `Percolator.Apps.FileTransfer` | `SqliteTransferSessionRepository` | Chunk 4.3 |
| 31 | `IFileTransferFilterPolicyRepository` | `Percolator.Apps.FileTransfer` | `SqliteFilterPolicyRepository` | Chunk 4.3 |
| 32 | `IRelayPeerDirectory` | `Percolator.Apps.Discovery` | `SqliteRelayPeerDirectory` | Chunk 4.2 |
| 33 | `IChatMessageQueryService` | `Percolator.Apps.Chat` | `SqlChatMessageQueryService` | Chunk 4.1 |
| 34 | `IConversationListQueryService` | `Percolator.Apps.Chat` | `SqlConversationListQueryService` | Chunk 4.1 |
| 35 | `IGroupInvitationQueryService` | `Percolator.Apps.Chat` | `SqlGroupInvitationQueryService` | Chunk 4.1 |
| 36 | `IOutboxQueryService` | `Percolator.Application` | `SqlOutboxQueryService` | Chunk 2.3 |
| 37 | `IPeerContactQueryService` | `Percolator.Application` | `SqlPeerContactQueryService` | Chunk 2.4 |
| 38 | `IFileTransferQueryService` | `Percolator.Apps.FileTransfer` | `SqlFileTransferQueryService` | Chunk 4.3 |
| 39 | `IIngressFilterService` | `Percolator.Application` | `IngressFilterService` | Chunk 3.4 |
| 40 | `IStreamRegistry` | `Percolator.Application` | `StreamRegistry` | Chunk 3.4 |
| 41 | `ITransportDispatcher` | `Percolator.Application` | `TransportDispatcher` | Chunk 3.4 |
| 42 | `ISealedEnvelopeUnwrapper` | `Percolator.Application` | `SealedEnvelopeUnwrapper` | Chunk 3.4 |
| 43 | `INearbyDiscoveryQueryClientPort` | `Percolator.Apps.Discovery` | `DiscoveryQueryClient` | Chunk 4.2 |
| 44 | `IDiscoveryHandshakePort` | `Percolator.Apps.Discovery` | `DiscoveryHandshakeAdapter` | Chunk 4.2 |
| 45 | `IPreKeyAvailabilityPort` | `Percolator.Apps.Discovery` | `PreKeyAvailabilityAdapter` | Chunk 4.2 |
| 46 | `IPreKeyBundleProvider` | `Percolator.Application` | `PreKeyBundleProviderAdapter` | Chunk 4.2 |
| 47 | `IProfileDeliveryClient` | `Percolator.PluginSdk` | `GrpcProfileDeliveryClient` | Chunk 4.2 |
| 48 | `IBlobCryptoService` | `Percolator.Application` | `StreamingAesGcmCryptoService` | Chunk 4.3 |
| 49 | `IRelayBlobClient` | `Percolator.Application` | `RelayBlobClient` | Chunk 4.3 |
| 50 | `IPeerStreamBlobClient` | `Percolator.Application` | `PeerStreamBlobClient` | Chunk 4.3 |
| 51 | `ILocalChunkStore` | `Percolator.Application` | `LocalFileChunkStore` | Chunk 4.3 |
| 52 | `IChannelRelayResolver` | `Percolator.Application` | `ChannelRelayResolver` | Chunk 4.3 |
| 53 | `ITransferStreamPool` / `Factory` / `Stream` | `Percolator.Apps.FileTransfer` | `TcpMultiStreamPool` & `TransferStream` | Chunk 4.3 |
| 54 | `INetworkEndpointDiscoveryPort` | `Percolator.Apps.FileTransfer` | `NetworkInterfaceEndpointDiscoveryPort` | Chunk 4.3 |
| 55 | `IDesktopFileStorage` | `Percolator.Apps.FileTransfer` | `PortableDesktopFileStore` | Chunk 4.3 |
| 56 | `ITransferRateLimiter` | `Percolator.Apps.FileTransfer` | `TokenBucketRateLimiter` | Chunk 4.3 |
| 57 | `IDistributedTrackerPort` | `Percolator.Apps.FileTransfer` | `RelaySwarmTrackerClient` | Chunk 4.3 |
| 58 | `ITransferOfferReaperService` | `Percolator.Apps.FileTransfer` | `TransferOfferReaperWorker` | Chunk 4.3 |
