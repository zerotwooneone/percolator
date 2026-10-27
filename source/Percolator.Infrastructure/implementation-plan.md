# Percolator.Infrastructure Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [ ] Implementation Gate: LOCKED (Refinement in progress)

---

## 1. Plan Overview & Architectural Context

`Percolator.Infrastructure` provides concrete technical adapters, persistent storage repositories, network transport clients/servers, cryptographic primitives, and binary serialization engines for the Percolator microkernel.

- **Technical Specifications**: All database schemas, wire framing protocols, Protobuf definitions, and cryptographic invariants are fully self-contained within this phased implementation roadmap.
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
│ Phase 1: Bootstrapping & Database Vault Lifecycle                      │
│ ├─ Chunk 1.1: Project Scaffolding & Core Platform Primitives           │
│ ├─ Chunk 1.2: Vault Protobuf Schema, Header Storage & Crypto-Shredder  │
│ ├─ Chunk 1.3: SQLCipher Native Keying & Connection Interceptor         │
│ └─ Chunk 1.4: Composable SQLCipher Unlock Orchestration Pipeline       │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 2: Core Relational Persistence & Identity Vault                  │
│ ├─ Chunk 2.1: SQLite DbContext, Baseline Migrations & Host Factory     │
│ ├─ Chunk 2.2: Vertical Slice: Identity Key Stores & Ratchet Sessions   │
│ ├─ Chunk 2.3: Vertical Slice: Outbox Jobs & Fast-Path Query Engine     │
│ ├─ Chunk 2.4: Vertical Slice: Contacts & Channel Repositories          │
│ └─ Chunk 2.5: Vertical Slice: Profiles & Relay Delivery Repositories   │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 3: Network Transport, Wire Protocols & Delivery Dispatching      │
│ ├─ Chunk 3.1: Core Cryptographic & ZK-Proof Engines (Signal & ZkGroup) │
│ ├─ Chunk 3.2: Core Delivery & Session Wire Schemas (Protos/)           │
│ ├─ Chunk 3.3: Generic Session Wire Packaging & Payload Serializer      │
│ ├─ Chunk 3.4: Transport Primitives, Interceptors & Endpoint Validation │
│ ├─ Chunk 3.5: Ingress Filtering & Transport Route Dispatching          │
│ └─ Chunk 3.6: gRPC Ingress Endpoints & Hosted Relay Workers            │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 4: Vertical Plugin Slices (End-to-End Capabilities)              │
│ ├─ Chunk 4.1: Vertical Slice: Chat Messaging, History & Padding Engine │
│ ├─ Chunk 4.2: Vertical Slice: Peer Discovery & Rendezvous Engine       │
│ ├─ Chunk 4.3: Vertical Slice: Encrypted Media Streaming & Blob Store   │
│ └─ Chunk 4.4: Vertical Slice: Swarm Multi-Stream File Transfer Engine  │
└───────────────────────────────────┬────────────────────────────────────┘
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 5: Operational Workers, Two-Stage Composition Root & System Verif│
│ ├─ Chunk 5.1: Scheduled Operational Pruning Workers                    │
│ └─ Chunk 5.2: Stage 2 DI Composition Root & Infrastructure Verification│
└────────────────────────────────────────────────────────────────────────┘
```

---

### Phase 1: Bootstrapping & Database Vault Lifecycle

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

#### Chunk 1.2: Vault Protobuf Schema, Header Storage & Crypto-Shredder
- **Technical Specifications & Wire Contracts**:
  - **Protobuf Layout (`Percolator.Infrastructure/Protos/DatabaseHeader.proto`)**:
    ```protobuf
    syntax = "proto3";
    package percolator.infrastructure;

    message DatabaseHeader {
      int32 version = 1;
      bytes salt = 2;               // 16-byte random salt for Argon2id
      bytes master_slot_nonce = 3;  // 12-byte ChaCha20-Poly1305 nonce
      bytes master_slot_payload = 4;// 48 bytes: Encrypted 32-byte master key + 16-byte Poly1305 tag
      bytes duress_slot_nonce = 5;  // 12-byte nonce (or random noise)
      bytes duress_slot_payload = 6;// 48 bytes: Encrypted 32-byte sentinel + tag (or dummy random noise)
    }
    ```
  - **Plausible Deniability Invariant**: When no duress password is set, `duress_slot_nonce` (12 bytes) and `duress_slot_payload` (48 bytes) are filled with `RandomNumberGenerator.GetBytes(60)`. Wire length is strictly ~120 bytes in all configurations.
  - **Header Storage Interface (`Percolator.Infrastructure.Vault.IHeaderStorage`)**:
    ```csharp
    namespace Percolator.Infrastructure.Vault;

    using Percolator.Domain.Common;

    internal interface IHeaderStorage
    {
        Task<DomainResult<DatabaseHeader>> ReadHeaderAsync(CancellationToken ct = default);
        Task<DomainResult> WriteHeaderAsync(DatabaseHeader header, CancellationToken ct = default);
        Task<DomainResult> OverwriteAndWipeAsync(CancellationToken ct = default);
    }
    ```
  - **Crypto-Shredding Pipeline (`DuressWipeHandler.ExecuteWipeAsync`)**:
    Under coercion, multi-pass overwrites of large SQLite database files cause conspicuous disk latency that tips off an adversary. Because `percolator.db` is already AES-256 encrypted, shredding the 120-byte `percolator.header` renders the database permanently unrecoverable in $< 2\text{ ms}$:
    ```csharp
    public async Task ExecuteWipeAsync(CancellationToken ct = default)
    {
        var headerPath = GetPath("percolator.header");
        if (File.Exists(headerPath))
        {
            var fileInfo = new FileInfo(headerPath);
            long length = fileInfo.Length;

            // 1. Overwrite header with CSPRNG random bytes and force physical disk controller flush
            await using (var fs = new FileStream(headerPath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[length];
                RandomNumberGenerator.Fill(buffer);
                await fs.WriteAsync(buffer, ct);
                fs.Flush(flushToDisk: true); // Triggers FlushFileBuffers / fsync

                // 2. Truncate to zero bytes
                fs.SetLength(0);
                fs.Flush(flushToDisk: true);
            }
            File.Delete(headerPath);
        }

        // 3. Immediately delete database and WAL files (all data is already unrecoverable ciphertext)
        var dbFiles = new[] { GetPath("percolator.db"), GetPath("percolator.db-wal"), GetPath("percolator.db-shm") };
        foreach (var path in dbFiles)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // Even if file deletion is blocked by OS lock, Kdb is already destroyed permanently
            }
        }

        // 4. Purge local cache and staging directories
        var cacheDir = GetPath("cache");
        if (Directory.Exists(cacheDir))
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch (IOException) { }
        }
    }
    ```
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/DatabaseHeader.proto`.
  - [ ] Define internal interface `Percolator.Infrastructure.Vault.IHeaderStorage`.
  - [ ] Implement `ProtobufHeaderStorage` (`IHeaderStorage`): reads/writes header with temp file staging and atomic replace.
  - [ ] Implement `DuressWipeHandler` (`Percolator.Application.Ports.IDuressWipeHandler`): instant crypto-shredding pipeline.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/`)**:
  - `ProtobufHeaderStorageTests.PlausibleDeniability_WireSize_IdenticalWithOrWithoutDuress`: header with dummy random noise vs active duress has identical 48-byte payload wire size; decryption of dummy duress slot throws `AuthenticationTagMismatchException`.
  - `ProtobufHeaderStorageTests.CorruptedHeader_TrapsGracefully`: 0-byte or truncated `percolator.header` traps `InvalidProtocolBufferException` returning structured failure without uncaught crashes.
  - `DuressWipeHandlerTests.ExecuteWipeAsync_OverwritesHeaderAndFlushes_InLessThan2Ms`: overwrites header with random bytes and flushes to disk in $< 2\text{ ms}$.
  - `DuressWipeHandlerTests.ExecuteWipeAsync_LockedDbFile_ContinuesWithoutThrowing`: simulates external file lock on `percolator.db`; catches `IOException` without throwing; destroys header permanently.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Vault.Storage`

#### Chunk 1.3: SQLCipher Native Keying & Connection Interceptor
- **Technical Specifications**:
  - **Memory Pinned Key Handle (`MasterKeyHandle`)**:
    - Allocates 32 bytes in unmanaged memory via `Marshal.AllocHGlobal(32)` and pins it to prevent GC relocation or managed string leakage.
    - Implements `ApplyKeyToConnection(DbConnection connection)`: casts to `SqliteConnection`, obtains raw unmanaged SQLite handle, and passes raw pointer directly to `SQLitePCL.raw.sqlite3_key`.
    - Implements `WithKeySpan(Action<ReadOnlySpan<byte>> action)` and `WithKeySpan<TResult>(ReadOnlySpanFunc<byte, TResult> func)` visitor methods for $O(1)$ envelope re-wrapping without heap allocation.
    - Implements `Dispose()`: scrubs unmanaged memory via `CryptographicOperations.ZeroMemory` before freeing handle. Subsequent access throws `ObjectDisposedException`.
  - **Connection Interceptor (`SqlCipherPragmaInterceptor`)**:
    ```csharp
    public sealed class SqlCipherPragmaInterceptor : DbConnectionInterceptor
    {
        private readonly IMasterKeyHandle _keyHandle;

        public SqlCipherPragmaInterceptor(IMasterKeyHandle keyHandle) => _keyHandle = keyHandle;

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (connection is SqliteConnection sqliteConn)
            {
                _keyHandle.ApplyKeyToConnection(sqliteConn);
                using var cmd = sqliteConn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                cmd.ExecuteNonQuery();
            }
        }
    }
    ```
- **Scope & Deliverables**:
  - [ ] Implement `MasterKeyHandle` (`Percolator.Domain.Security.Ports.IMasterKeyHandle`).
  - [ ] Implement `SqlCipherPragmaInterceptor` (`Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/Keying/`)**:
  - `MasterKeyHandleTests.Dispose_ZeroizesPinnedMemory`: unmanaged buffer memory is zeroized on dispose; subsequent access throws `ObjectDisposedException`.
  - `MasterKeyHandleTests.WithKeySpan_YieldsValid32ByteKeySpan`: visitor executes with exact 32-byte key span matching initial key.
  - `SqlCipherPragmaInterceptorTests.ConnectionOpened_AppliesKeyAndWalPragmas`: verifies interceptor applies raw key and WAL journal mode.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Vault.Keying`

#### Chunk 1.4: Composable SQLCipher Unlock Orchestration Pipeline
- **Collaborator Interface Specifications**:
  - `IVaultEnvelopePacker`:
    ```csharp
    namespace Percolator.Infrastructure.Vault;

    using Percolator.Domain.Common;
    using Percolator.Domain.Security.ValueObjects;

    public enum SlotTrialOutcome { MasterUnlocked, DuressTriggered, InvalidCredentials }
    public readonly record struct SlotTrialResult(SlotTrialOutcome Outcome);

    internal interface IVaultEnvelopePacker
    {
        DomainResult<DatabaseHeader> CreateEnvelope(
            VaultPassphrase masterPassword,
            VaultPassphrase? duressPassword,
            ReadOnlySpan<byte> databaseKeySpan);

        SlotTrialResult EvaluateSlots(
            DatabaseHeader header,
            VaultPassphrase candidatePassword,
            Span<byte> destinationDatabaseKeyBuffer);

        DomainResult<DatabaseHeader> RewrapMasterSlot(
            DatabaseHeader currentHeader,
            VaultPassphrase newPassword,
            ReadOnlySpan<byte> databaseKeySpan);

        DomainResult<DatabaseHeader> ConfigureDuressSlot(
            DatabaseHeader currentHeader,
            VaultPassphrase? duressPassword);
    }
    ```
  - `IDatabaseCanaryProbe`:
    ```csharp
    namespace Percolator.Infrastructure.Vault;

    using Microsoft.Data.Sqlite;

    public enum CanaryProbeResult { Verified, FailedNotADatabase, Corrupted }

    internal interface IDatabaseCanaryProbe
    {
        Task<CanaryProbeResult> ProbeAsync(SqliteConnection connection, CancellationToken ct = default);
    }
    ```
  - `IDatabaseMigrator`:
    ```csharp
    namespace Percolator.Infrastructure.Vault;

    using Percolator.Domain.Common;

    internal interface IDatabaseMigrator
    {
        Task<DomainResult> MigrateAsync(CancellationToken ct = default);
    }
    ```
  - `IStage2HostFactory`:
    ```csharp
    namespace Percolator.Infrastructure.Vault;

    using Microsoft.Extensions.Hosting;
    using Percolator.Domain.Security.Ports;

    internal interface IStage2HostFactory
    {
        IHost BuildAuthenticatedHost(IMasterKeyHandle masterKeyHandle);
    }
    ```
- **Technical Mechanics & Wire Decryption Code**:
  - **ChaCha20-Poly1305 Slot Trials & Exception Control Flow (`VaultEnvelopePacker.EvaluateSlots`)**:
    ```csharp
    // Fixed 32-byte sentinel for duress slot verification:
    private static readonly byte[] DuressSentinelBytes =
        SHA256.HashData(Encoding.UTF8.GetBytes("PERCOLATOR_VAULT_DURESS_SENTINEL_V1"));

    // Slot evaluation logic inside VaultEnvelopePacker:
    public SlotTrialResult EvaluateSlots(
        DatabaseHeader header,
        VaultPassphrase candidatePassword,
        Span<byte> destinationDatabaseKeyBuffer)
    {
        byte[] derivedCandidateKey = DeriveArgon2idKey(candidatePassword, header.Salt.Span);
        try
        {
            using var cipher = new ChaCha20Poly1305(derivedCandidateKey);
            Span<byte> candidatePlaintext = stackalloc byte[32];

            // 1. Evaluate Master Slot (48 bytes: [..32] ciphertext + [32..] tag)
            try
            {
                cipher.Decrypt(
                    header.MasterSlotNonce.Span,
                    header.MasterSlotPayload.Span[..32],
                    header.MasterSlotPayload.Span[32..],
                    candidatePlaintext);

                candidatePlaintext.CopyTo(destinationDatabaseKeyBuffer);
                CryptographicOperations.ZeroMemory(candidatePlaintext);
                return new SlotTrialResult(SlotTrialOutcome.MasterUnlocked);
            }
            catch (AuthenticationTagMismatchException)
            {
                // Tag mismatch: candidate key is not the master key
            }

            // 2. Evaluate Duress Slot
            try
            {
                cipher.Decrypt(
                    header.DuressSlotNonce.Span,
                    header.DuressSlotPayload.Span[..32],
                    header.DuressSlotPayload.Span[32..],
                    candidatePlaintext);

                if (CryptographicOperations.FixedTimeEquals(candidatePlaintext, DuressSentinelBytes))
                {
                    CryptographicOperations.ZeroMemory(candidatePlaintext);
                    return new SlotTrialResult(SlotTrialOutcome.DuressTriggered);
                }
            }
            catch (AuthenticationTagMismatchException)
            {
                // Active duress tag mismatch OR dummy random bytes failed authentication
            }

            return new SlotTrialResult(SlotTrialOutcome.InvalidCredentials);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedCandidateKey);
        }
    }
    ```
  - **Native SQLCipher Canary Error Handling (`DatabaseCanaryProbe.ProbeAsync`)**:
    When an invalid key is supplied, SQLCipher fails to decrypt page 1, failing SQLite's header magic check (`"SQLite format 3\000"`). SQLite does not define a `SQLITE_WRONGKEY` code; it throws `Microsoft.Data.Sqlite.SqliteException` with native error code `26` (`SQLITE_NOTADB: "file is not a database"` or `"file is encrypted or is not a database"`):
    ```csharp
    public sealed class DatabaseCanaryProbe : IDatabaseCanaryProbe
    {
        public async Task<CanaryProbeResult> ProbeAsync(SqliteConnection connection, CancellationToken ct = default)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            try
            {
                await cmd.ExecuteScalarAsync(ct);
                return CanaryProbeResult.Verified;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB
            {
                return CanaryProbeResult.FailedNotADatabase;
            }
            catch (SqliteException)
            {
                return CanaryProbeResult.Corrupted;
            }
        }
    }
    ```
- **Step-by-Step Collaborator Workflow Mapping**:
  - **`InitializeAsync(masterPassword, duressPassword, ct)`**:
    1. Generate random 32-byte $K_{\text{db}}$ into unmanaged memory via `MasterKeyHandle`.
    2. Call `_envelopePacker.CreateEnvelope(masterPassword, duressPassword, keySpan)` using Argon2id ($m=64\text{MB}, t=3, p=4$) and ChaCha20-Poly1305.
    3. Atomically write `DatabaseHeader` via `_headerStorage.WriteHeaderAsync`.
    4. **Cold Launch Self-Healing Invariant**: If `percolator.header` was missing or wiped, force unlink any orphaned `.db*` ciphertext files before creating the new database.
    5. Probe canary using probe connection keyed via `_keyHandle.ApplyKeyToConnection` through `_canaryProbe.ProbeAsync`.
    6. Execute `_migrator.MigrateAsync(ct)` to apply baseline schemas.
    7. Invoke `_hostFactory.BuildAuthenticatedHost(_masterKeyHandle)` and return `UnlockResult.Success(new AuthenticatedSession(host))`.
  - **`UnlockAsync(candidatePassword, ct)`**:
    1. Read `DatabaseHeader` via `_headerStorage.ReadHeaderAsync`.
    2. Allocate unmanaged pinned buffer in `MasterKeyHandle`.
    3. Evaluate slots via `_envelopePacker.EvaluateSlots(header, candidatePassword, keySpanBuffer)`:
       - Master slot decrypted $\rightarrow$ probe canary via `_canaryProbe.ProbeAsync`. If `Verified`, execute `_migrator.MigrateAsync(ct)`, assemble host via `_hostFactory.BuildAuthenticatedHost`, and return `UnlockResult.Success(session)`.
       - Duress slot decrypted and matches `DuressSentinelBytes` $\rightarrow$ execute `_wipeHandler.ExecuteWipeAsync(ct)`, transition status to `Uninitialized`, return `UnlockResult.DuressActivated()`.
       - All slot trials fail $\rightarrow$ zeroize key memory, return `UnlockResult.InvalidCredentials()`.
  - **`ChangeMasterPasswordAsync(currentPassword, newPassword, ct)` ($O(1)$ Re-Wrap)**:
    1. Validate active session is unlocked.
    2. Validate `currentPassword` against active key handle via slot trial.
    3. Call `_envelopePacker.RewrapMasterSlot(currentHeader, newPassword, activeKeySpan)`.
    4. **Duress Slot Preservation & Performance Guarantee**: Because `header.Salt` is retained, the existing duress slot remains 100% valid without requiring the user to re-enter their duress passphrase. Because $K_{\text{db}}$ remains identical, zero SQLite tables or pages are rewritten ($O(1)$ re-wrap completes in $< 200\text{ ms}$).
    5. Atomically overwrite header via `_headerStorage.WriteHeaderAsync`.
    6. Zeroize temporary key spans via `CryptographicOperations.ZeroMemory`.
  - **`ConfigureDuressPasswordAsync(duressPassword, ct)`**:
    1. Validate active session is unlocked.
    2. Validate `duressPassword` is not empty/whitespace and distinct from master password.
    3. Call `_envelopePacker.ConfigureDuressSlot(currentHeader, duressPassword)`.
    4. Atomically overwrite header via `_headerStorage.WriteHeaderAsync`.
  - **`LockAsync()`**:
    1. Stop all hosted services in Stage 2 `IHost`.
    2. Clear connection pools via `SqliteConnection.ClearAllPools()`.
    3. Dispose `_masterKeyHandle` (zeroizing unmanaged memory).
    4. Dispose Stage 2 `IHost`.
    5. Transition status to `Locked`.
- **Scope & Deliverables**:
  - [ ] Define interfaces `IVaultEnvelopePacker`, `IDatabaseCanaryProbe`, `IDatabaseMigrator`, and `IStage2HostFactory`.
  - [ ] Implement `VaultEnvelopePacker` (`IVaultEnvelopePacker`): Argon2id derivation ($m=64\text{MB}, t=3, p=4$), ChaCha20-Poly1305 slot encryption, and fixed 32-byte `DuressSentinelBytes` matching (`SHA256("PERCOLATOR_VAULT_DURESS_SENTINEL_V1")`).
  - [ ] Implement `DatabaseCanaryProbe` (`IDatabaseCanaryProbe`): executes `SELECT count(*) FROM sqlite_master;` on isolated probe connection, explicitly trapping native error code 26 (`SQLITE_NOTADB`).
  - [ ] Implement `SqlCipherDatabaseUnlockCoordinator` (`Percolator.Application.Ports.IDatabaseUnlockCoordinator`).
  - [ ] Implement `AuthenticatedSession` (`Percolator.Application.Ports.IAuthenticatedSession`).
  - [ ] Implement `InfrastructureServiceCollectionExtensions.AddBootstrapInfrastructure(IServiceCollection, IConfiguration)`:
    - Calls `SQLitePCL.Batteries_V2.Init()`.
    - Registers `SystemDateTimeProvider`, `ProtobufHeaderStorage`, `DuressWipeHandler`, `VaultEnvelopePacker`, `DatabaseCanaryProbe`, and `SqlCipherDatabaseUnlockCoordinator`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/Unlock/`)**:
  - `DatabaseCanaryProbeTests.InvalidKey_TrapsSqliteErrorCode26`: invalid key traps `SqliteException` with `SqliteErrorCode == 26` (`SQLITE_NOTADB`) and returns `CanaryProbeResult.FailedNotADatabase`.
  - `VaultEnvelopePackerTests.MasterPasswordChange_RewrapsSlot_PreservesDuress`: re-wraps master slot with new key; preserves salt and duress slot; decrypts successfully.
  - `SqlCipherUnlockTests.HappyPathMasterUnlock_CoordinatesCollaborators`: correct master password derives key, evaluates master slot, executes canary probe, invokes migrator, and returns `UnlockResult.Success(session)`.
  - `SqlCipherUnlockTests.InvalidPassword_ReturnsInvalidCredentials`: incorrect password fails master and duress slots; returns `UnlockResult.InvalidCredentials`.
  - `SqlCipherUnlockTests.BlankPassword_CreatesEncryptedDatabase_AndUnkeyedConnectionFails`: setup with `VaultPassphrase.Empty` creates encrypted database; raw unkeyed connection fails with native code 26 (`SQLITE_NOTADB`); unlock with empty passphrase succeeds.
  - `SqlCipherUnlockTests.LockAsync_ScrubsMemoryAndClearsPools`: calling `LockAsync()` clears connection pools (`SqliteConnection.ClearAllPools()`), zeroizes `MasterKeyHandle`, disposes host, transitions status to `Locked`.
  - `SqlCipherUnlockTests.ChangeMasterPasswordAsync_RewrapsMasterSlot_PreservesDuressSlot`: re-wraps master slot with new key; database remains readable; subsequent unlock requires new password; existing duress password remains valid.
  - `SqlCipherUnlockTests.ConfigureDuressPasswordAsync_IdenticalToMaster_RejectsCollision`: rejects duress password identical to master; header is untouched.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Vault.Unlock`

---

### Phase 2: Core Relational Persistence & Identity Vault

#### Chunk 2.1: SQLite DbContext, Baseline Migrations & Host Factory
- **Technical Specifications & Database Architecture**:
  - **`PercolatorDbContext` Architecture**:
    - Inherits `DbContext`, configured with `SqlCipherPragmaInterceptor` and connection pooling.
    - Configured with `ValueComparer<byte[]>` across all BLOB keys to prevent false dirty change-tracking mutations.
    - **Native Interop Thread Safety (Invariant 8)**: Register `IDbContextFactory<PercolatorDbContext>` alongside scoped `PercolatorDbContext`. Interop bridge adapters consume `IDbContextFactory<T>` to spin up short-lived isolated database contexts on unmanaged native callback threads, preventing concurrency clashes and cross-thread context contamination.
  - **Column-Level Encryption Value Converter (`AesGcmValueConverter`)**:
    - Encrypts private keys, ratchet chain keys, and sensitive secrets at rest before persisting to SQLite BLOB columns (Invariant 2).
    - **Strict Phase 3 Independence**: Zero dependency on Phase 3 `ICryptoEngine` or `SignalCryptoEngine`. Implemented purely using BCL `System.Security.Cryptography.AesGcm` (standard AES-256-GCM: 12-byte nonce, 16-byte authentication tag) and `RandomNumberGenerator`, consuming `IMasterKeyHandle.WithKeySpan` from Phase 1 (Chunk 1.3):
      ```csharp
      namespace Percolator.Infrastructure.Persistence.Converters;

      using System.Security.Cryptography;
      using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
      using Percolator.Domain.Security.Ports;

      public sealed class AesGcmValueConverter : ValueConverter<byte[], byte[]>
      {
          public AesGcmValueConverter(IMasterKeyHandle masterKeyHandle)
              : base(
                  plainBytes => Encrypt(plainBytes, masterKeyHandle),
                  cipherBytes => Decrypt(cipherBytes, masterKeyHandle))
          {
          }

          private static byte[] Encrypt(byte[]? plaintext, IMasterKeyHandle masterKeyHandle)
          {
              if (plaintext == null || plaintext.Length == 0) return Array.Empty<byte>();

              return masterKeyHandle.WithKeySpan(keySpan =>
              {
                  using var aes = new AesGcm(keySpan, AesGcm.TagByteSizes.MaxSize);
                  byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize); // 12 bytes
                  byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes
                  byte[] ciphertext = new byte[plaintext.Length];

                  aes.Encrypt(nonce, plaintext, ciphertext, tag);

                  // Payload layout: [12-byte Nonce] [16-byte Tag] [N-byte Ciphertext]
                  byte[] result = new byte[nonce.Length + tag.Length + ciphertext.Length];
                  Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
                  Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
                  Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
                  return result;
              });
          }

          private static byte[] Decrypt(byte[]? payload, IMasterKeyHandle masterKeyHandle)
          {
              if (payload == null || payload.Length < 28) return Array.Empty<byte>();

              return masterKeyHandle.WithKeySpan(keySpan =>
              {
                  using var aes = new AesGcm(keySpan, AesGcm.TagByteSizes.MaxSize);
                  ReadOnlySpan<byte> nonce = payload.AsSpan(0, 12);
                  ReadOnlySpan<byte> tag = payload.AsSpan(12, 16);
                  ReadOnlySpan<byte> ciphertext = payload.AsSpan(28);

                  byte[] plaintext = new byte[ciphertext.Length];
                  aes.Decrypt(nonce, ciphertext, tag, plaintext);
                  return plaintext;
              });
          }
      }
      ```
  - **`SqliteDatabaseMigrator` (`IDatabaseMigrator`)**:
    - Executes `await dbContext.Database.MigrateAsync(ct)` on the authenticated, encrypted connection.
  - **`BaselineStage2HostFactory` (`IStage2HostFactory`)**:
    - Configures post-unlock Stage 2 `IHost` with active `IMasterKeyHandle`, `SqlCipherPragmaInterceptor`, `PercolatorDbContext`, and `IDbContextFactory<PercolatorDbContext>`.
- **Scope & Deliverables**:
  - [ ] Implement `PercolatorDbContext` with baseline entity mappings.
  - [ ] Implement EF Core deterministic BLOB mapping with `ValueComparer<byte[]>`.
  - [ ] Implement `AesGcmValueConverter` for column-level encryption of private keys and ratchet chain keys using BCL `AesGcm` and `IMasterKeyHandle`.
  - [ ] Implement `SqliteDatabaseMigrator` (`Percolator.Infrastructure.Vault.IDatabaseMigrator`).
  - [ ] Create initial EF Core database migration for `percolator.db`.
  - [ ] Implement `BaselineStage2HostFactory` (`Percolator.Infrastructure.Vault.IStage2HostFactory`) registering scoped `PercolatorDbContext` and `IDbContextFactory<PercolatorDbContext>`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Core/`)**:
  - `AesGcmValueConverterTests.EncryptAndDecrypt_RoundtripsSuccessfully`: asserts plaintext roundtrips through BCL `AesGcmValueConverter` and validates ciphertext layout (`[12B Nonce][16B Tag][Ciphertext]`).
  - `PercolatorDbContextTests.ColumnLevelEncryption_StoresKeysAsCiphertextAtRest`: direct byte inspection of raw SQLite database confirms plaintext private keys are absent.
  - `PercolatorDbContextTests.BlobValueComparer_DetectsByteArrayEquality`: queries by byte array values and asserts match success.
  - `SqliteDatabaseMigratorTests.MigrateAsync_AppliesInitialSchema`: executes migration and verifies table existence.
  - `BaselineStage2HostFactoryTests.BuildHost_ResolvesDbContextAndFactoryWithKey`: builds authenticated host and resolves operational `PercolatorDbContext` and `IDbContextFactory<PercolatorDbContext>`.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Core`

#### Chunk 2.2: Vertical Slice: Identity Key Stores & Ratchet Sessions
- **Technical Specifications & Table Schemas (`vault_*`)**:
  - **DDL & Schema Definitions**:
    - `identity_profiles`:
      - `id` (`BLOB` 32 bytes PublicIdentityId, PK)
      - `display_name` (`TEXT`)
      - `role` (`INTEGER`: `Standard=0`, `RelayOperator=1`, `Bot=2`)
      - `state` (`INTEGER`: `Active=0`, `Suspended=1`, `Disabled=2`)
      - `created_at_utc` (`TEXT` ISO 8601)
      - `disabled_at_utc` (`TEXT` ISO 8601 nullable)
    - `local_devices`:
      - `owner_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `device_id` (`INTEGER`: `Primary=1`, `Secondary>1`)
      - `device_name` (`TEXT`)
      - `device_public_key` (`BLOB` 32 bytes IdentityKey)
      - `link_proof_signature` (`BLOB` 64 bytes DeviceLinkProof nullable)
      - `created_at_utc` (`TEXT` ISO 8601)
      - `last_seen_at_utc` (`TEXT` ISO 8601 nullable)
      - *Composite PK*: `PRIMARY KEY(owner_identity_id, device_id)`
      - *FK*: `FOREIGN KEY(owner_identity_id) REFERENCES identity_profiles(id) ON DELETE CASCADE`
    - `vault_identity_keys`:
      - `id` (`BLOB` 32 bytes PublicIdentityId, PK)
      - `private_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `public_key` (`BLOB` 32 bytes)
      - `created_at_utc` (`TEXT` ISO 8601)
    - `vault_signed_prekeys`:
      - `owner_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `device_id` (`INTEGER` DeviceId)
      - `private_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `public_key` (`BLOB` 32 bytes)
      - `signature` (`BLOB` 64 bytes)
      - `created_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(owner_identity_id, device_id)`
      - *FK*: `FOREIGN KEY(owner_identity_id) REFERENCES vault_identity_keys(id) ON DELETE CASCADE`
    - `vault_onetime_prekeys`:
      - `owner_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `device_id` (`INTEGER` DeviceId)
      - `key_id` (`INTEGER` uint32 OPK Identifier)
      - `private_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `public_key` (`BLOB` 32 bytes)
      - `is_consumed` (`INTEGER` boolean: `0=Available`, `1=Consumed`)
      - `consumed_at_utc` (`TEXT` ISO 8601 nullable)
      - `created_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(owner_identity_id, device_id, key_id)`
      - *FK*: `FOREIGN KEY(owner_identity_id) REFERENCES vault_identity_keys(id) ON DELETE CASCADE`
      - *Index*: `CREATE INDEX ix_vault_opk_unconsumed ON vault_onetime_prekeys(owner_identity_id, device_id, is_consumed);`
    - `vault_ratchet_sessions`:
      - `session_id` (`TEXT` GUID, PK)
      - `owner_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `owner_device_id` (`INTEGER` DeviceId)
      - `remote_peer_id` (`BLOB` 32 bytes PublicIdentityId)
      - `remote_device_id` (`INTEGER` DeviceId)
      - `root_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `send_chain_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `recv_chain_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `local_ephemeral_priv_ciphertext` (`BLOB` nullable, encrypted via `AesGcmValueConverter`)
      - `local_ephemeral_pub` (`BLOB` 32 bytes nullable)
      - `remote_ephemeral_pub` (`BLOB` 32 bytes nullable)
      - `sending_counter` (`INTEGER`)
      - `receiving_counter` (`INTEGER`)
      - `previous_chain_length` (`INTEGER`)
      - `ratchet_state_blob` (`BLOB`, encrypted domain binary snapshot via `AesGcmValueConverter`)
      - `updated_at_utc` (`TEXT` ISO 8601)
      - *Route Index*: `CREATE UNIQUE INDEX ix_vault_ratchet_route ON vault_ratchet_sessions(owner_identity_id, remote_peer_id, remote_device_id);`
    - `vault_group_sender_keys`:
      - `channel_id` (`TEXT` GUID ChannelId)
      - `author_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `author_device_id` (`INTEGER` DeviceId)
      - `key_id` (`INTEGER` uint32)
      - `iteration` (`INTEGER` uint32)
      - `chain_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `signing_priv_ciphertext` (`BLOB` nullable, encrypted via `AesGcmValueConverter`)
      - `author_signing_pub` (`BLOB` 32 bytes nullable)
      - `updated_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(channel_id, author_identity_id, author_device_id)`
    - `vault_group_receiver_sessions`:
      - `channel_id` (`TEXT` GUID ChannelId)
      - `author_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `author_device_id` (`INTEGER` DeviceId)
      - `key_id` (`INTEGER` uint32)
      - `author_signing_pub` (`BLOB` 32 bytes nullable)
      - `chain_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `receiving_counter` (`INTEGER`)
      - `skipped_keys_blob` (`BLOB` nullable, encrypted via `AesGcmValueConverter`)
      - `updated_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(channel_id, author_identity_id, author_device_id)`
    - `vault_group_credentials`:
      - `channel_id` (`TEXT` GUID ChannelId, PK)
      - `master_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `auth_credential_mac` (`BLOB` 32 bytes)
      - `local_blob_key_ciphertext` (`BLOB` nullable, encrypted via `AesGcmValueConverter`)
      - `updated_at_utc` (`TEXT` ISO 8601)
    - `vault_pending_handshakes`:
      - `recipient_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `sender_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `envelope_payload` (`BLOB`, encrypted via `AesGcmValueConverter`)
      - `created_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(recipient_identity_id, sender_identity_id)`
  - **Zero Phase 3 Wire Serializer Dependency**: Repositories persist domain value object byte arrays or internal binary representations directly. State encryption is handled by `AesGcmValueConverter` at the EF Core mapping boundary; repositories do not depend on Phase 3 `ISessionWirePacker` or protobuf schemas (`session.proto`).
- **Scope & Deliverables**:
  - [ ] Implement Cryptographic Vault Repositories:
    - `SqliteIdentityProfileRepository` (`Percolator.Domain.Identities.Ports.IIdentityProfileRepository`)
    - `SqliteLocalIdentityKeyStore` (`Percolator.Application.Ports.ILocalIdentityKeyStore`)
    - `SqlitePrivatePreKeyStore` (`Percolator.Domain.Identities.Ports.IPrivatePreKeyStore`)
    - `SqliteRatchetSessionRepository` (`Percolator.Application.Ports.IRatchetSessionRepository`)
    - `SqliteGroupReceiverSessionRepository` (`Percolator.Application.Ports.IGroupReceiverSessionRepository`)
    - `SqliteGroupSenderKeyRepository` (`Percolator.Application.Ports.IGroupSenderKeyRepository`)
    - `SqlitePendingHandshakeRepository` (`Percolator.Application.Ports.IPendingHandshakeRepository`)
    - `SqliteGroupCredentialsRepository` (`Percolator.Domain.Security.Ports.IGroupCredentialsRepository`)
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Vault/`)**:
  - `SqliteRatchetSessionRepositoryTests.SaveAndLoad_PreservesCiphertextAndState`: saves and reloads ratchet session state atomically; verifies stored fields are ciphertext at rest.
  - `SqliteGroupSenderKeyRepositoryTests.AtomicCommit_RollsBackOnFailure`: asserts all child records update together or roll back cleanly without orphan rows.
  - `SqliteLocalIdentityKeyStoreTests.StoreAndRetrieveIdentity_MatchesKey`: stores local identity key and asserts decrypted roundtrip.
  - `SqlitePrivatePreKeyStoreTests.MarkConsumed_UpdatesOpkStatus`: flags one-time prekey as consumed and asserts query excludes consumed keys.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Vault`

#### Chunk 2.3: Vertical Slice: Outbox Jobs & Fast-Path Query Engine
- **Technical Specifications & Table Schemas (`ops_*`)**:
  - **DDL & Schema Definitions**:
    - `ops_outbox_jobs`:
      - `id` (`TEXT` GUID, PK)
      - `owner_identity_id` (`BLOB` 32 bytes PublicIdentityId, indexed)
      - `channel_id` (`TEXT` GUID ChannelId)
      - `recipient_id` (`BLOB` 32 bytes nullable)
      - `delivery_route` (`INTEGER`: `Direct=0`, `Relay=1`)
      - `payload` (`BLOB`)
      - `status` (`INTEGER`: `Pending=0`, `InFlight=1`, `Delivered=2`, `Failed=3`, `PausedDormant=4`)
      - `retry_count` (`INTEGER` default 0)
      - `max_attempts` (`INTEGER` default 5)
      - `next_attempt_at_utc` (`TEXT` ISO 8601 nullable, indexed)
      - `created_at_utc` (`TEXT` ISO 8601)
      - `last_error` (`TEXT` nullable)
      - *Composite Index*: `CREATE INDEX ix_ops_outbox_due ON ops_outbox_jobs(status, next_attempt_at_utc);`
      - *Owner Index*: `CREATE INDEX ix_ops_outbox_owner ON ops_outbox_jobs(owner_identity_id, status);`
    - `ops_inbound_queue`:
      - `id` (`TEXT` GUID, PK)
      - `sender_id` (`BLOB` 32 bytes)
      - `app_id` (`INTEGER`)
      - `raw_payload` (`BLOB`)
      - `received_at_utc` (`TEXT` ISO 8601)
      - `processed_at_utc` (`TEXT` ISO 8601 nullable)
    - `ops_reserved_ports`:
      - `port_number` (`INTEGER` PK)
      - `allocated_at_utc` (`TEXT` ISO 8601)
      - `lease_expiry_utc` (`TEXT` ISO 8601)
  - **CQRS Read-Side Fast Path**:
    - `SqlOutboxQueryService` projects lightweight status summaries, counts, and retry timings via EF Core `.Select()` projection, strictly omitting the `payload` column to prevent LOH (Large Object Heap) memory churn during UI diagnostic polling.
- **Scope & Deliverables**:
  - [ ] Implement Outbox Persistence & CQRS Read Model:
    - `SqliteOutboxRepository` (`Percolator.Application.Delivery.Ports.IOutboxRepository`): write-side enqueue, dequeue batching, retry calculation tracking.
    - `SqlOutboxQueryService` (`Percolator.Application.Delivery.Ports.IOutboxQueryService`): fast-path diagnostic summary projecting without hydrating byte payload buffers.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Outbox/`)**:
  - `SqliteOutboxRepositoryTests.EnqueueAndDequeue_TransitionsStateCleanly`: jobs advance cleanly from `Queued` to `InFlight` and `Delivered`.
  - `SqliteOutboxRepositoryTests.RetryIndex_OptimizesPendingJobQueries`: executes pending retry query and verifies index usage on `ix_ops_outbox_retry`.
  - `SqlOutboxQueryServiceTests.GetOutboxSummary_ProjectsLightweightModel`: verifies projection contains counts and statuses without payload loading.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Outbox`

#### Chunk 2.4: Vertical Slice: Contacts & Channel Repositories
- **Technical Specifications & Table Schemas (`peer_contacts`, `chat_channels`)**:
  - **DDL & Schema Definitions**:
    - `peer_contacts`:
      - `owner_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `remote_peer_id` (`BLOB` 32 bytes PublicIdentityId)
      - `nickname` (`TEXT`)
      - `trust_level` (`INTEGER`: `Untrusted=0`, `Tofu=1`, `Verified=2`, `Blocked=3`)
      - `state` (`INTEGER`: `Active=0`, `PendingApproval=1`, `Rejected=2`)
      - `primary_public_key` (`BLOB` 32 bytes nullable IdentityKey)
      - `created_at_utc` (`TEXT` ISO 8601)
      - `last_seen_at_utc` (`TEXT` ISO 8601 nullable)
      - `updated_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(owner_identity_id, remote_peer_id)`
    - `peer_contact_devices`:
      - `owner_identity_id` (`BLOB` 32 bytes)
      - `remote_peer_id` (`BLOB` 32 bytes)
      - `device_id` (`INTEGER` DeviceId)
      - `registered_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(owner_identity_id, remote_peer_id, device_id)`
      - *FK*: `FOREIGN KEY(owner_identity_id, remote_peer_id) REFERENCES peer_contacts(owner_identity_id, remote_peer_id) ON DELETE CASCADE`
    - `chat_channels`:
      - `id` (`TEXT` GUID PK)
      - `type` (`INTEGER`: `Direct=0`, `Group=1`)
      - `name` (`TEXT`)
      - `owner_identity_id` (`BLOB` 32 bytes nullable, DirectChannel local owner)
      - `remote_peer_id` (`BLOB` 32 bytes nullable, DirectChannel remote peer)
      - `current_epoch` (`INTEGER` nullable, GroupChannel EpochNumber default 1)
      - `created_at_utc` (`TEXT` ISO 8601)
      - `last_activity_utc` (`TEXT` ISO 8601)
      - *Direct Channel Index*: `CREATE INDEX ix_chat_channels_direct ON chat_channels(owner_identity_id, remote_peer_id);`
    - `chat_channel_members`:
      - `channel_id` (`TEXT` GUID ChannelId, FK to `chat_channels(id)` ON DELETE CASCADE)
      - `member_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `role` (`INTEGER`: `Admin=0`, `Member=1`)
      - `joined_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(channel_id, member_identity_id)`
    - `chat_channel_payloads`:
      - `id` (`TEXT` GUID PayloadId, PK)
      - `channel_id` (`TEXT` GUID ChannelId, FK to `chat_channels(id)` ON DELETE CASCADE)
      - `author_id` (`BLOB` 32 bytes PublicIdentityId)
      - `author_device_id` (`INTEGER` DeviceId)
      - `encrypted_payload` (`BLOB`)
      - `sent_at_utc` (`TEXT` ISO 8601)
      - *Payload History Index*: `CREATE INDEX ix_channel_payloads_history ON chat_channel_payloads(channel_id, sent_at_utc DESC);`
- **Scope & Deliverables**:
  - [ ] Implement Contacts & Channel Repositories:
    - `SqlitePeerContactRepository` (`Percolator.Domain.Identities.Ports.IPeerContactRepository`): persists contact trust and profile key metadata.
    - `SqlPeerContactQueryService` (`Percolator.Application.Ports.IPeerContactQueryService`): fast-path summary and detail read models.
    - `SqliteChannelRepository` (`Percolator.Domain.Channels.Ports.IChannelRepository`): persists channel identity, type, and metadata.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Contacts/`)**:
  - `SqlitePeerContactRepositoryTests.SaveContact_UpdatesTrustLevelAndTimestamps`: updates contact state and persists atomically.
  - `SqlPeerContactQueryServiceTests.GetContacts_ProjectsSummaryList`: projects contacts and pending requests into summary view.
  - `SqliteChannelRepositoryTests.SaveChannel_PersistsParticipants`: persists channel and associated participant metadata.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Contacts`

#### Chunk 2.5: Vertical Slice: Profiles & Relay Delivery Repositories
- **Technical Specifications & Table Schemas (`relay_*`, `peer_profiles`, `peer_mailbox_routing`)**:
  - **DDL & Schema Definitions**:
    - `relay_prekeys`:
      - `owner_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `device_id` (`INTEGER` DeviceId)
      - `identity_key` (`BLOB` 32 bytes)
      - `signed_prekey` (`BLOB`)
      - `signed_prekey_signature` (`BLOB` 64 bytes DeviceLinkProof)
      - `one_time_prekeys_json` (`TEXT` serialized via BCL `System.Text.Json`)
      - `updated_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(owner_identity_id, device_id)`
    - `relay_profile_slots`:
      - `blinded_locator` (`BLOB` 32 bytes PK)
      - `write_token_hash` (`BLOB` 32 bytes)
      - `ciphertext` (`BLOB` 1040 bytes padded)
      - `nonce` (`BLOB` 12 bytes)
      - `revision` (`INTEGER`)
      - `expires_at_utc` (`TEXT` ISO 8601)
      - *Expiry Index*: `CREATE INDEX ix_relay_profile_expiry ON relay_profile_slots(expires_at_utc);`
    - `relay_ledger_epochs`:
      - `channel_id` (`TEXT` GUID ChannelId, PK)
      - `relay_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `current_epoch` (`INTEGER` EpochNumber)
      - `encrypted_roster_blob` (`BLOB` EncryptedEntriesBlob)
      - `zk_public_params` (`BLOB` ZkGroupPublicParams)
      - `updated_at_utc` (`TEXT` ISO 8601)
      - *Relay Ledger Index*: `CREATE INDEX ix_relay_ledger_relay ON relay_ledger_epochs(relay_identity_id);`
    - `relay_ledger_routing_tokens`:
      - `channel_id` (`TEXT` GUID ChannelId, FK to `relay_ledger_epochs(channel_id)` ON DELETE CASCADE)
      - `routing_token` (`BLOB` 32 bytes BlindedRoutingToken)
      - *Composite PK*: `PRIMARY KEY(channel_id, routing_token)`
    - `peer_profiles`:
      - `peer_id` (`BLOB` 32 bytes PK)
      - `display_name` (`TEXT`)
      - `status_bio` (`TEXT` nullable)
      - `avatar_reference_blob_id` (`BLOB` 32 bytes nullable BlobReference)
      - `revision` (`INTEGER` uint32)
      - `updated_at_utc` (`TEXT` ISO 8601)
    - `peer_profile_snapshots`:
      - `peer_id` (`BLOB` 32 bytes PublicIdentityId)
      - `source_endpoint` (`TEXT`)
      - `source_type` (`INTEGER`: `DirectP2P=1`, `Relay=2`)
      - `blinded_locator` (`BLOB` 32 bytes BlindedProfileLocator)
      - `metadata_json` (`TEXT`)
      - `last_observed_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(peer_id, source_endpoint)`
    - `peer_mailbox_routing`:
      - `peer_id` (`BLOB` 32 bytes PublicIdentityId)
      - `relay_endpoint` (`TEXT`)
      - `routing_token` (`BLOB` 32 bytes BlindedRoutingToken)
      - `delivery_token` (`BLOB` 32 bytes DeliveryToken)
      - `last_observed_at_utc` (`TEXT` ISO 8601)
      - `last_success_at_utc` (`TEXT` ISO 8601 nullable)
      - `backpressure_cooldown_until_utc` (`TEXT` ISO 8601 nullable)
      - `updated_at_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(peer_id, relay_endpoint)`
  - **Strict Persistence Scope & Zero Network Service Coupling**:
    - Chunk 2.5 is strictly bounded to relational SQLite EF Core entities, migrations, and local storage repositories.
    - All network transport endpoints (`UnauthenticatedDeliveryEndpoint`), gRPC stubs, and duplex relay workers (`RelayMailboxStreamWorker`) are strictly excluded and located in Phase 3 (Chunk 3.6).
    - Serialized prekeys use BCL `System.Text.Json` for `one_time_prekeys_json`, eliminating any dependency on Phase 3 Protobuf types.
- **Scope & Deliverables**:
  - [ ] Implement Profiles & Relay Delivery Repositories:
    - `SqliteRelayPreKeyRepository` (`Percolator.Domain.Delivery.Ports.IRelayPreKeyRepository`)
    - `SqliteRelayProfileRepository` (`Percolator.Domain.Delivery.Ports.IRelayProfileRepository`)
    - `SqliteRelayLedgerRepository` (`Percolator.Domain.Delivery.Ports.IRelayLedgerRepository`)
    - `SqlitePeerProfileRepository` (`Percolator.PluginSdk.Profiles.IPeerProfileRepository`)
    - `SqlitePeerMailboxRoutingRepository` (`Percolator.PluginSdk.Profiles.IPeerMailboxRoutingRepository`)
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Relay/`)**:
  - `SqliteRelayPreKeyRepositoryTests.PublishAndFetch_MatchesBundle`: publishes prekey bundle and verifies retrieval by routing token.
  - `SqliteRelayProfileRepositoryTests.CommitProfile_ValidatesWriteToken`: verifies write token hash check on profile upload.
  - `SqlitePeerMailboxRoutingRepositoryTests.UpdateRouting_PersistsEndpoint`: stores and updates peer mailbox routing endpoint.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Persistence.Relay`

---

### Phase 3: Network Transport, Wire Protocols & Delivery Dispatching

#### Chunk 3.1: Core Cryptographic & ZK-Proof Engines (Signal & ZkGroup)
- **Technical Specifications & Cryptographic Invariants**:
  - **Cryptographic Port Implementation (`SignalCryptoEngine` / `SodiumCryptoEngine`)**:
    - Realizes `Percolator.Domain.Security.Ports.ICryptoEngine`.
    - **Ed25519 Signatures (RFC 8032)**: `VerifyEd25519Signature(IdentityKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)` (using domain value object `IdentityKey`) and `SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)` returning `byte[]`.
    - **Curve25519 / X25519 Diffie-Hellman (RFC 7748)**: `ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)` returning domain value object `SharedSecret`, and `DeriveX3dhMasterSecret(...)`.
    - **Symmetric Ratchet Advances**: HKDF SHA-256 HMAC root key updates returning `(ChainKey NextRootKey, ChainKey DerivedChainKey) KdfRk(ChainKey currentRootKey, SharedSecret dhSecret)` and symmetric chain key steps returning `(ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey)`.
    - **AES-256-GCM AEAD**: Authenticated encryption/decryption with 12-byte nonce and 16-byte Poly1305/GCM tag (`EncryptAesGcm`, `DecryptAesGcm`).
    - **Deterministic Memory Zeroization (Invariant 3)**: Wipe sensitive ephemeral key spans, secret scalars, and intermediate KDF buffers via `CryptographicOperations.ZeroMemory`.
    - **Cryptographic Logging Guardrail (Invariant 5)**: Redact all keys, hashes, and sensitive spans when `CryptographyOptions.EnableCryptographicMaterialLogging = false`.
    - **RFC 4122 Big-Endian UUID Normalization (Invariant 7)**: Utilize `Guid.ToByteArray(bigEndian: true)` (from Chunk 1.1) when converting CLR `Guid` identifiers into raw cryptographic byte arrays.
  - **Zero-Knowledge Presentation Proofs (`ZkgroupCryptographyService`)**:
    - Realizes `Percolator.Domain.Security.Ports.IZkProofEngine`.
    - Wraps native `Signal.Interop` / `libzkgroup` C-ABI using safe unmanaged handles.
    - Generates and verifies zero-knowledge group membership proofs (`VerifyGroupPresentation`, `GenerateGroupPresentation`) against `RelayGroupLedger` epochs without leaking individual member identity keys to relays.
  - **Strict Forward Independence**:
    - Depends strictly on standard BCL cryptography (`System.Security.Cryptography`) and native interop bindings. Zero dependency on network transports, outbox repositories, or Phase 4 plugins.
- **Scope & Deliverables**:
  - [ ] Implement `SignalCryptoEngine` (`Percolator.Domain.Security.Ports.ICryptoEngine`).
  - [ ] Implement `ZkgroupCryptographyService` (`Percolator.Domain.Security.Ports.IZkProofEngine`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Security/`)**:
  - `SignalCryptoEngineTests.Ed25519_SignAndVerify_MatchesRfc8032Vectors`: known test vectors match RFC 8032. Low-brittle guarantee: asserts mathematical outputs; zero mock coupling.
  - `SignalCryptoEngineTests.Curve25519_DiffieHellman_MatchesRfc7748Vectors`: known test vectors match RFC 7748. Low-brittle guarantee: asserts mathematical outputs.
  - `SignalCryptoEngineTests.AesGcm_EncryptDecrypt_SucceedsAndRejectsBitflip`: encrypts payload, decrypts successfully; single bitflip throws authenticated tag mismatch.
  - `ZkgroupCryptographyServiceTests.VerifyGroupPresentation_ValidProof_ReturnsSuccess`: presentation proof generates and validates against group public credentials.
  - `SignalCryptoEngineTests.LoggingGuardrail_RedactsSensitiveKeys_WhenLoggingDisabled`: asserts logger redacts all key bytes when material logging is disabled.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Security.Crypto`

#### Chunk 3.2: Core Delivery & Session Wire Schemas (`Protos/`)
- **Technical Specifications & Wire Contracts**:
  - Configure `Google.Protobuf` (v3.25.2) and `Grpc.Tools` (v2.57.0) in `Percolator.Infrastructure.csproj`.
  - **Core Wire Schemas (Organized by Trust Boundary & Authentication Model)**:
    - **1. `unauthenticated_delivery.proto` (Public Edge / Sealed Sender & Capability Ingress)**:
      ```protobuf
      syntax = "proto3";
      package percolator.infrastructure;
      import "google/protobuf/timestamp.proto";

      message SealedEnvelopeProto {
        bytes recipient_routing_token = 1;
        bytes delivery_token = 2;
        bytes encrypted_package = 3;
        google.protobuf.Timestamp expires_at_utc = 4;
      }

      message DeliveryAckProto {
        bool success = 1;
        string error_message = 2;
      }

      message EnqueueAckProto {
        bool accepted = 1;
        google.protobuf.Timestamp expires_at_utc = 2;
      }

      message ProfileFetchRequestProto {
        bytes blinded_locator = 1;
      }

      message ProfileFetchResponseProto {
        bool found = 1;
        bytes nonce = 2;
        bytes ciphertext = 3; // 1040 B padded block
        uint32 revision = 4;
        google.protobuf.Timestamp expires_at_utc = 5;
      }

      message ProfileUploadRequestProto {
        bytes blinded_locator = 1;
        bytes write_token = 2;
        uint32 revision = 3;
        bytes nonce = 4;
        bytes ciphertext = 5;
        google.protobuf.Timestamp expires_at_utc = 6;
      }

      message ProfileUploadResponseProto {
        bool success = 1;
        string error_message = 2;
      }

      message ProfileDeleteRequestProto {
        bytes blinded_locator = 1;
        bytes write_token = 2;
      }

      message ProfileDeleteResponseProto {
        bool success = 1;
        string error_message = 2;
      }

      message BlindBlobDownloadRequestProto {
        bytes blob_id = 1; // 32-byte SHA-256 blob locator
      }

      message BlindBlobChunkProto {
        bytes chunk_data = 1;
        uint32 chunk_index = 2;
        bool is_final_chunk = 3;
      }

      service UnauthenticatedDeliveryService {
        rpc DeliverDirect(SealedEnvelopeProto) returns (DeliveryAckProto);
        rpc EnqueueMailbox(SealedEnvelopeProto) returns (EnqueueAckProto);
        rpc GetProfile(ProfileFetchRequestProto) returns (ProfileFetchResponseProto);
        rpc UploadProfile(ProfileUploadRequestProto) returns (ProfileUploadResponseProto);
        rpc DeleteProfile(ProfileDeleteRequestProto) returns (ProfileDeleteResponseProto);
        rpc DownloadBlindBlob(BlindBlobDownloadRequestProto) returns (stream BlindBlobChunkProto);
      }
      ```
    - **2. `authenticated_relay.proto` (Mailbox Ownership, Pre-Key Directory & Stream Management)**:
      ```protobuf
      syntax = "proto3";
      package percolator.infrastructure;
      import "google/protobuf/timestamp.proto";

      message QueuedSealedEnvelopeProto {
        string queue_id = 1;
        bytes envelope_payload = 2;
        google.protobuf.Timestamp enqueued_at_utc = 3;
      }

      message AuthChallengeProto {
        bytes nonce = 1;
        google.protobuf.Timestamp expires_at_utc = 2;
      }

      message AuthResponseProto {
        bytes signature = 1;
      }

      message AuthResultProto {
        bool authenticated = 1;
        string reason = 2;
      }

      message MailboxAckProto {
        string queue_id = 1;
        bool acknowledged = 2;
      }

      message MailboxStreamClientMessage {
        oneof payload {
          AuthResponseProto auth_response = 1;
          MailboxAckProto ack = 2;
        }
      }

      message MailboxStreamServerMessage {
        oneof payload {
          AuthChallengeProto auth_challenge = 1;
          AuthResultProto auth_result = 2;
          QueuedSealedEnvelopeProto envelope = 3;
        }
      }

      message RegisterMailboxRequest {
        bytes owner_identity_key = 1;
        bytes routing_token = 2;
        bytes authorized_delivery_token = 3;
        bytes signature = 4;
      }

      message RegisterMailboxResponse {
        bool success = 1;
      }

      message DrainMailboxRequest {
        bytes routing_token = 1;
        bytes auth_token = 2;
      }

      message DrainMailboxResponse {
        repeated QueuedSealedEnvelopeProto envelopes = 1;
      }

      message OneTimePreKeyProto {
        uint32 key_id = 1;
        bytes public_key = 2;
      }

      message PublishPreKeyBundleRequest {
        bytes identity_key = 1;
        bytes signed_prekey = 2;
        bytes prekey_signature = 3;
        uint32 signed_prekey_id = 4;
        repeated OneTimePreKeyProto one_time_prekeys = 5;
        bytes signature = 6;
      }

      message PublishPreKeyBundleResponse {
        bool success = 1;
      }

      message FetchPreKeyBundleRequest {
        bytes recipient_routing_token = 1;
        bytes requester_identity_key = 2;
        bytes requester_signature = 3;
        google.protobuf.Timestamp timestamp_utc = 4;
      }

      message FetchPreKeyBundleResponse {
        bytes identity_key = 1;
        bytes signed_prekey = 2;
        bytes prekey_signature = 3;
        uint32 signed_prekey_id = 4;
        optional bytes one_time_prekey = 5;
        optional uint32 one_time_prekey_id = 6;
      }

      service AuthenticatedRelayService {
        rpc ConnectMailboxStream(stream MailboxStreamClientMessage) returns (stream MailboxStreamServerMessage);
        rpc RegisterMailbox(RegisterMailboxRequest) returns (RegisterMailboxResponse);
        rpc DrainMailbox(DrainMailboxRequest) returns (DrainMailboxResponse);
        rpc PublishPreKeyBundle(PublishPreKeyBundleRequest) returns (PublishPreKeyBundleResponse);
        rpc FetchPreKeyBundle(FetchPreKeyBundleRequest) returns (FetchPreKeyBundleResponse);
      }
      ```
    - **3. `anonymous_group.proto` (Zero-Knowledge Group Relay Service)**:
      ```protobuf
      syntax = "proto3";
      package percolator.infrastructure;

      message GroupStreamClientMessage {
        bytes conversation_id = 1;
        uint32 current_epoch = 2;
        bytes zk_presentation_proof = 3;
      }

      message GroupBroadcastEnvelope {
        bytes conversation_id = 1;
        uint32 epoch = 2;
        uint32 sender_key_iteration = 3;
        bytes ciphertext = 4;
        bytes author_signature = 5;
      }

      message EpochMutationNotification {
        bytes conversation_id = 1;
        uint32 new_epoch = 2;
      }

      message GroupStreamServerMessage {
        oneof payload {
          GroupBroadcastEnvelope broadcast = 1;
          EpochMutationNotification mutation = 2;
        }
      }

      message DispatchGroupMessageRequest {
        bytes conversation_id = 1;
        uint32 epoch = 2;
        bytes zk_presentation_proof = 3;
        uint32 sender_key_iteration = 4;
        bytes ciphertext = 5;
        bytes author_signature = 6;
      }

      message DispatchGroupMessageResponse {
        bool accepted = 1;
      }

      message CommitRosterMutationRequest {
        bytes conversation_id = 1;
        uint32 base_epoch = 2;
        bytes new_roster_blob = 3;
        repeated bytes new_routing_tokens = 4;
        bytes zk_presentation_proof = 5;
      }

      message CommitRosterMutationResponse {
        bool committed = 1;
        uint32 new_epoch = 2;
      }

      message FetchGroupStateRequest {
        bytes conversation_id = 1;
        bytes zk_presentation_proof = 2;
      }

      message FetchGroupStateResponse {
        uint32 current_epoch = 1;
        bytes encrypted_roster_blob = 2;
      }

      service AnonymousGroupRelayService {
        rpc SubscribeGroupStream(GroupStreamClientMessage) returns (stream GroupStreamServerMessage);
        rpc DispatchGroupMessage(DispatchGroupMessageRequest) returns (DispatchGroupMessageResponse);
        rpc CommitRosterMutation(CommitRosterMutationRequest) returns (CommitRosterMutationResponse);
        rpc FetchGroupState(FetchGroupStateRequest) returns (FetchGroupStateResponse);
      }
      ```
    - **4. `session.proto` (Inner Cryptographic Packaging)**:
      ```protobuf
      syntax = "proto3";
      package percolator.infrastructure;

      message RatchetHeaderProto {
        bytes ratchet_public_key = 1;
        uint32 counter = 2;
        uint32 previous_chain_length = 3;
      }

      message DirectSessionPackageProto {
        RatchetHeaderProto header = 1;
        bytes nonce = 2;
        bytes ciphertext = 3;
      }

      message GroupSessionPackageProto {
        bytes conversation_id = 1;
        uint32 iteration = 2;
        bytes ciphertext = 3;
        bytes author_signature = 4;
      }

      message HandshakeInvitationPackageProto {
        bytes initiator_identity_key = 1;
        bytes initiator_ephemeral_key = 2;
        uint32 signed_prekey_id = 3;
        optional uint32 onetime_prekey_id = 4;
        bytes encrypted_payload = 5;
      }
      ```
  - **Explicit Phase 4 Boundary Exclusion**:
    - Application plugin schemas (`chat.proto`, `discovery.proto`, `file_transfer.proto`) are strictly excluded from Phase 3 and deferred to Phase 4 (Chunks 4.1, 4.2, 4.4).
- **Scope & Deliverables**:
  - [ ] Configure `Google.Protobuf` and `Grpc.Tools` in `Percolator.Infrastructure.csproj`.
  - [ ] Author Protobuf schemas in `Percolator.Infrastructure/Protos/`: `unauthenticated_delivery.proto`, `authenticated_relay.proto`, `anonymous_group.proto`, and `session.proto`.
  - [ ] Verify clean compilation and code generation of typed C# messages and gRPC base services.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Protos/`)**:
  - `CoreProtoCompilationTests.AllProtos_GenerateValidClrTypes`: tests that core message types instantiate and default-serialize without schema errors.
- **Verification Gate**:
  - `dotnet build Percolator.Infrastructure`

#### Chunk 3.3: Generic Session Wire Packaging & Payload Serializer
- **Technical Specifications & Interface Boundaries**:
  - **`ProtobufSessionWirePacker` (`Percolator.Application.Ports.ISessionWirePacker`)**:
    - Packs typed session structures into wire bytes (`ReadOnlyMemory<byte>`): `PackDirectRatchetMessage(RatchetHeader, ReadOnlyMemory<byte>)`, `PackGroupMessage(ChannelId, uint, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>)`, and `PackHandshakeMessage(HandshakeHeader, ReadOnlyMemory<byte>)` using `session.proto` wire structures (`DirectSessionPackageProto`, `GroupSessionPackageProto`, `HandshakeInvitationPackageProto`).
    - Unpacks incoming session wire frames into typed domain envelopes: `UnpackDirectRatchetMessage`, `UnpackGroupMessage`, and `UnpackHandshakeMessage` returning `DomainResult<InboundDirectEnvelope>`, `DomainResult<InboundGroupEnvelope>`, and `DomainResult<InboundHandshakeEnvelope>`.
    - **Strict Phase 4 & Transport Decoupling**: Exclusively handles inner cryptographic session framing. Completely decoupled from outer sealed envelope dispatching (`SealedEnvelopeProto`, routing tokens) and application-layer plugins (`chat.proto`).
  - **`ProtobufPayloadSerializer` (`Percolator.PluginSdk.IPayloadSerializer`)**:
    - High-performance binary serialization using `Google.Protobuf.CodedOutputStream` and `IBufferWriter<byte>`.
    - Generic span-based deserialization using `Google.Protobuf.MessageParser<T>`.
    - Completely decoupled from concrete plugin schemas; implements a clean compile-time boundary for arbitrary protobuf-serializable plugin payloads.
- **Scope & Deliverables**:
  - [ ] Implement `ProtobufSessionWirePacker` (`Percolator.Application.Ports.ISessionWirePacker`).
  - [ ] Implement `ProtobufPayloadSerializer` (`Percolator.PluginSdk.IPayloadSerializer`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Packaging/`)**:
  - `ProtobufSessionWirePackerTests.RoundtripDirectSession_PacksAndUnpacksCorrectly`: packs ratchet header and ciphertext into wire bytes and unwraps into typed `InboundDirectEnvelope`.
  - `ProtobufSessionWirePackerTests.GroupSessionPackage_PacksIterationAndSignature`: packages group message iteration, signature, and ciphertext into `GroupSessionPackageProto`.
  - `ProtobufPayloadSerializerTests.GenericPayloadSerialization_RoundtripsCorrectly`: serializes and deserializes generic payloads.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Packaging`

#### Chunk 3.4: Transport Primitives, Interceptors & Endpoint Validation
- **Technical Specifications & Concrete Mechanics**:
  - **Dynamic Port Range Probing (5200–5299)**:
    - Coordinates with Phase 2's `ops_reserved_ports` table:
      1. Query `ops_reserved_ports` via `PercolatorDbContext` to filter out currently leased ports.
      2. Probe ports sequentially using `TcpListener` with dual-mode IPv6 and exclusive binding:
         ```csharp
         var listener = new TcpListener(IPAddress.IPv6Any, port);
         listener.Server.DualMode = true;
         listener.Server.ExclusiveAddressUse = true;
         listener.Start();
         ```
      3. If `SocketException` (`AddressAlreadyInUse`), advance to next port.
      4. Upon successful bind, commit lease to `ops_reserved_ports` with 30-minute renewal heartbeat.
  - **gRPC Interceptor Pipeline**:
    - **`DeliveryCertificateAuthInterceptor`**:
      - Executes on authenticated relay calls (`authenticated_relay.proto`).
      - Extracts metadata headers: `x-percolator-identity` (hex-encoded `PublicIdentityId`), `x-percolator-timestamp` (Unix ms), and `x-percolator-signature`.
      - Rejects if timestamp delta exceeds $\pm 60\text{ seconds}$ from relay UTC.
      - Verifies Ed25519 signature over `method_path || timestamp_bytes || request_payload_hash` using `SignalCryptoEngine` (Chunk 3.1).
  - **Transport Primitives**:
    - `InMemoryTransportCertificateProvider`: Ephemeral ECDSA P-256 self-signed certificate with loopback SANs generated in memory with zero disk I/O.
    - `CallbackEndpointValidator`: Validates peer endpoints before connecting; blocks loopback, RFC 1918 private subnets, and link-local addresses unless explicit dev flags (`AllowLoopback = true`, `AllowLan = true`) are set.
    - `PeerGrpcChannelFactory`: Thread-safe channel cache using `ConcurrentDictionary<string, Lazy<GrpcChannel>>` with 2-second timeout and clean socket disposal.
    - `PrimaryContainerServiceActivator<T>`: Bridges Kestrel gRPC listener container to host DI container (`IServiceProvider`) with scoped lifetime cleanup.
- **Scope & Deliverables**:
  - [ ] Implement `InMemoryTransportCertificateProvider`.
  - [ ] Implement `CallbackEndpointValidator`.
  - [ ] Implement dynamic port range probing (5200–5299) and lease renewal on `ops_reserved_ports`.
  - [ ] Implement `PeerGrpcChannelFactory`.
  - [ ] Implement `DeliveryCertificateAuthInterceptor`.
  - [ ] Implement `PrimaryContainerServiceActivator<T>`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Primitives/`)**:
  - `CallbackEndpointValidatorTests.RejectsPrivateAndLoopbackIps_UnlessDevFlag`: rejects loopback and RFC 1918 IPs by default.
  - `DeliveryCertificateAuthInterceptorTests.TimestampSkewExceeded_RejectsRequest`: rejects signatures with timestamp delta > 60s.
  - `PortProberTests.PortContention_BindsNextAvailablePort`: simulates port collision and verifies lease assignment on subsequent free port.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Transport.Primitives`

#### Chunk 3.5: Ingress Filtering & Transport Route Dispatching
- **Technical Specifications & Collaborator Mapping**:
  - **`IngressFilterService` (`Percolator.Application.Ports.IIngressFilterService`)**:
    - Edge filtering before cryptographic operations: queries `IPeerContactRepository` (from Phase 2 Chunk 2.4) to check `PeerTrustLevel.Blocked`. Enforces sender rate limits and identity dormancy status.
  - **`StreamRegistry` (`Percolator.Application.Ports.IStreamRegistry`)**:
    - Tracks active gRPC duplex and server-streaming connections for direct peers and home relays.
    - Detects connection drops and handles flow-control saturation (`StreamWriteResult.Backpressured`).
  - **`TransportDispatcher` (`Percolator.Application.Delivery.Ports.ITransportDispatcher`)**:
    - Consumes domain delivery ports and routes messages across three transport paths:
      1. Direct 1:1: dispatches `DeliverDirect` over ephemeral TLS via `UnauthenticatedDeliveryService.DeliverDirect`.
      2. Relayed 1:1: dispatches `EnqueueMailbox` to recipient's designated home relay using `DeliveryToken`.
      3. Relayed Group: dispatches `DispatchGroupMessage` to shared group relay accompanied by ZK presentation proof from `IZkProofEngine` (Chunk 3.1).
  - **`SealedEnvelopeUnwrapper` (`Percolator.Application.Ports.ISealedEnvelopeUnwrapper`)**:
    - Bridges relay `MailboxEnvelope` instances to strongly typed `InboundEnvelope` domain objects.
- **Scope & Deliverables**:
  - [ ] Implement `IngressFilterService` (`Percolator.Application.Ports.IIngressFilterService`).
  - [ ] Implement `StreamRegistry` (`Percolator.Application.Ports.IStreamRegistry`).
  - [ ] Implement `TransportDispatcher` (`Percolator.Application.Delivery.Ports.ITransportDispatcher`).
  - [ ] Implement `SealedEnvelopeUnwrapper` (`Percolator.Application.Ports.ISealedEnvelopeUnwrapper`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Dispatching/`)**:
  - `IngressFilterServiceTests.BlockedSender_RejectsEnvelopeBeforeCrypto`: envelopes from blocked identities rejected before any cryptographic operations.
  - `StreamRegistryTests.BackpressureSaturation_ReturnsBackpressured`: queue overflow returns `StreamWriteResult.Backpressured`.
  - `TransportDispatcherTests.DirectVsRelayRoute_ResolvesCorrectAdapter`: resolves direct TLS vs relay delivery token cleanly.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Transport.Dispatching`

#### Chunk 3.6: gRPC Ingress Endpoints & Hosted Relay Workers
- **Technical Specifications & Service Endpoints**:
  - **`UnauthenticatedDeliveryEndpoint` (ASP.NET Core gRPC Service on relays and direct peer listening ports)**:
    - Realizes `UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceBase`.
    - Handles `DeliverDirect`, `EnqueueMailbox`, `GetProfile`, `UploadProfile`, `DeleteProfile`, and `DownloadBlindBlob`.
    - **Strict Boundary Framing**: Interacts strictly with transport layer pipes and Phase 2 repositories (`relay_profile_slots` via `IRelayProfileRepository`). Zero application-level media decoding, image transformations, or file reassembly (strictly located in Phase 4 Chunks 4.3 and 4.4).
  - **Duplex & Streaming Background Workers (`IHostedService`)**:
    - `RelayMailboxStreamWorker`: Duplex streaming connection to home relay (`ConnectMailboxStream`) with automatic reconnection and jittered exponential backoff.
    - `RelayGroupStreamWorker`: Live subscription stream (`SubscribeGroupStream`) with ZK presentation proofs for active group channels.
- **Scope & Deliverables**:
  - [ ] Implement `UnauthenticatedDeliveryEndpoint` (`UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceBase`).
  - [ ] Implement `RelayMailboxStreamWorker` (`IHostedService`).
  - [ ] Implement `RelayGroupStreamWorker` (`IHostedService`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Endpoints/`)**:
  - `UnauthenticatedDeliveryEndpointTests.DeliverDirect_ValidEnvelope_ReturnsSuccessAck`: validates RPC execution and ack generation.
  - `UnauthenticatedDeliveryEndpointTests.UploadProfile_PersistsSlot`: verifies profile slot persistence via `IRelayProfileRepository`.
  - `RelayMailboxStreamWorkerTests.ConnectionDrop_RetriesWithExponentialBackoff`: tests automatic reconnection retry sequence.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Transport.Endpoints`

---

### Phase 4: Vertical Plugin Slices (End-to-End Capabilities)

#### Chunk 4.1: Vertical Slice: Chat Messaging, History & Padding Engine
- **Technical Specifications & Table Schemas (`chat_*`)**:
  - **DDL & Schema Definitions**:
    - `chat_messages`:
      - `id` (`TEXT` GUID PayloadId, PK)
      - `channel_id` (`TEXT` GUID, indexed)
      - `author_id` (`BLOB` 32 bytes PublicIdentityId)
      - `timestamp_unix_ms` (`INTEGER` ulong)
      - `content` (`TEXT`)
      - `reply_parent_id` (`TEXT` GUID nullable)
      - `reply_author_id` (`BLOB` 32 bytes nullable PublicIdentityId)
      - `mentions_json` (`TEXT` nullable)
      - `link_preview_json` (`TEXT` nullable)
      - `expire_after_seconds` (`INTEGER` nullable)
      - `expires_at_unix_ms` (`INTEGER` ulong nullable, indexed)
      - `read_at_unix_ms` (`INTEGER` ulong nullable)
      - `edited_at_unix_ms` (`INTEGER` ulong nullable)
      - `is_tombstone` (`INTEGER` boolean default 0)
      - `deleted_at_unix_ms` (`INTEGER` ulong nullable)
      - `deleted_by` (`BLOB` 32 bytes nullable PublicIdentityId)
      - *Composite Timeline Index*: `CREATE INDEX ix_chat_timeline ON chat_messages(channel_id, timestamp_unix_ms);`
      - *Disappearing Message Eviction Index*: `CREATE INDEX ix_chat_expiry ON chat_messages(expires_at_unix_ms) WHERE expires_at_unix_ms IS NOT NULL;`
    - `chat_reactions`:
      - `message_id` (`TEXT` GUID)
      - `reactor_id` (`BLOB` 32 bytes)
      - `emoji` (`TEXT` 32 chars)
      - `timestamp_unix_ms` (`INTEGER` ulong)
      - *Composite PK*: `PRIMARY KEY(message_id, reactor_id, emoji)`
      - *FK*: `FOREIGN KEY(message_id) REFERENCES chat_messages(id) ON DELETE CASCADE`
    - `chat_attachments`:
      - `id` (`TEXT` GUID, PK)
      - `message_id` (`TEXT` GUID, FK to `chat_messages(id)` ON DELETE CASCADE)
      - `blob_sha256` (`BLOB` 32 bytes)
      - `file_name` (`TEXT`)
      - `file_size_bytes` (`INTEGER`)
      - `mime_type` (`TEXT`)
    - `chat_group_invitations`:
      - `id` (`TEXT` GUID InvitationId, PK)
      - `target_group_channel_id` (`TEXT` GUID ChannelId)
      - `group_name` (`TEXT`)
      - `inviter_id` (`BLOB` 32 bytes)
      - `relay_endpoint` (`TEXT` nullable)
      - `expires_at_unix_ms` (`INTEGER` ulong)
      - `status` (`INTEGER`: `Pending=0`, `Accepted=1`, `Declined=2`, `Expired=3`)
      - `responded_at_unix_ms` (`INTEGER` ulong nullable)
    - `chat_unknown_group_messages`:
      - `id` (`TEXT` GUID PayloadId, PK)
      - `channel_id` (`TEXT` GUID ChannelId)
      - `author_id` (`BLOB` 32 bytes PublicIdentityId)
      - `encrypted_payload` (`BLOB`)
      - `received_at_unix_ms` (`INTEGER` ulong)
      - `expires_at_unix_ms` (`INTEGER` ulong)
      - *Expiry Index*: `CREATE INDEX ix_unknown_group_expiry ON chat_unknown_group_messages(expires_at_unix_ms);`
  - **Wire Contracts (`Percolator.Infrastructure/Protos/chat.proto`)**:
    - `ChatPayloadEnvelopeProto`: carries payload type and byte array wrapped with stepped bucket padding.
    - `ChatMessageProto`: text content, timestamp, and message flags.
    - `ReplyContextProto`: strictly carries `parent_message_id` (16 bytes GUID) and `author_identity_id` (32 bytes); quote snippet text is strictly omitted from the wire (Invariant 6).
    - `MentionProto`, `MediaAttachmentProto`, `MessageEditProto`, `MessageTombstoneProto`, `ReactionUpdateProto`, `ReadReceiptProto`, `SystemNoticeProto`, `GroupInvitationProto`, `GroupInvitationResponseProto`.
  - **Stepped Bucket Padding Framing**:
    - `ChatPayloadFramingDecorator` decorates `IPayloadSerializer` (from Phase 3 Chunk 3.3).
    - Serialized frames are padded out to discrete power-of-two / stepped bucket sizes: 256 B, 512 B, 1024 B, 2048 B, and 4096 B. Payloads exceeding 4096 B are rejected or split into attachment blobs.
  - **CQRS Fast-Path Read Models & Background Pruners**:
    - CQRS read model `SqlChatMessageQueryService` projects timeline pages with keyset / offset pagination without hydrating heavy attachment blobs.
    - Background workers (`IHostedService`): `DisappearingMessageEvictionWorker` (sweeps expired messages every 30s using `ix_chat_expiry`), `UnknownGroupMessageCachePruner` (sweeps unreferenced group messages older than 48h), and `AbandonedInvitePruner`.
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/chat.proto`.
  - [ ] Implement `ChatPayloadFramingDecorator` implementing stepped bucket padding (256, 512, 1024, 2048, 4096 B).
  - [ ] Implement Chat Persistence & Fast-Path CQRS Query Services:
    - `SqlChatMessageRepository` (`Percolator.Apps.Chat.Ports.IChatMessageRepository`)
    - `SqlChatMessageQueryService` (`Percolator.Apps.Chat.Ports.IChatMessageQueryService`)
    - `SqlConversationListQueryService` (`Percolator.Apps.Chat.Ports.IConversationListQueryService`)
    - `SqlGroupInvitationRepository` (`Percolator.Apps.Chat.Ports.IGroupInvitationRepository`)
    - `SqlGroupInvitationQueryService` (`Percolator.Apps.Chat.Ports.IGroupInvitationQueryService`)
    - `SqlUnknownGroupMessageCacheRepository` (`Percolator.Apps.Chat.Ports.IUnknownGroupMessageCacheRepository`)
  - [ ] Implement Chat Lifecycle Background Pruners:
    - `DisappearingMessageEvictionWorker`
    - `UnknownGroupMessageCachePruner`
    - `AbandonedInvitePruner`
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/Chat/`)**:
  - `ChatPayloadFramingDecoratorTests.SteppedBucketPadding_ExpandsToExpectedBucketSize`: serialized chat frames expand to discrete 256/512/1024/2048/4096 B boundaries.
  - `ProtobufChatSerializerTests.ReplyContext_ExcludesQuoteSnippetFromWire`: quote snippet text is strictly absent from wire payload (only parent ID and author ID).
  - `SqlChatMessageRepositoryTests.DeleteMessage_CascadesAttachmentDeletion`: deleting a message cascades and deletes attachment records in SQLite.
  - `SqlChatMessageQueryServiceTests.PagedTimeline_ClampsToLimitAndOffset`: limit/offset paging clamps accurately to boundary without aggregate hydration.
  - `SqlConversationListQueryServiceTests.UnreadBadgeCount_AggregatesPastReadReceipt`: count aggregates messages strictly past read receipt timestamp.
  - `DisappearingMessageEvictionWorkerTests.ExpiredMessages_PurgedAfterVirtualClockAdvance`: advances virtual clock by 31s with `FakeTimeProvider`; asserts expired messages are deleted.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.Chat`

#### Chunk 4.2: Vertical Slice: Peer Discovery & Rendezvous Engine
- **Technical Specifications & Wire Contracts**:
  - **Wire Contracts (`Percolator.Infrastructure/Protos/discovery.proto`)**:
    - `NearbyDiscoveryQueryProto`: `uint32 max_results = 1;` (strictly clamped to range 1–100 to prevent denial-of-service/scraping), `bytes requester_token = 2;`
    - `NearbyDiscoveryResponseProto`: `repeated PeerDiscoveryDescriptorProto peers = 1;`
    - `PeerDiscoveryDescriptorProto`: `bytes peer_identity_id = 1;`, `string alias = 2;`, `repeated DeliveryRouteProto routes = 3;`, `bytes profile_key = 4;`
    - `DeliveryRouteProto`: `int32 route_type = 1;`, `string endpoint = 2;`, `bytes routing_token = 3;`
    - `DiscoveryRegistrationProto`: `bytes peer_identity_id = 1;`, `PeerDiscoveryDescriptorProto descriptor = 2;`, `google.protobuf.Timestamp lease_expiry_utc = 3;`, `bytes signature = 4;`
    - `BlindedRendezvousQueryProto`: `bytes blinded_locator = 1;`
    - `BlindedRendezvousResponseProto`: `bool found = 1;`, `bytes encrypted_contact_card = 2;`
  - **Private Rendezvous Service**:
    - 24-hour UTC epoch locator rotation: computes deterministic blinded rendezvous locators via $\text{HMAC-SHA256}(\text{IdentityPublicKey}, \text{EpochBytes})$ where $\text{Epoch} = \lfloor\text{UnixTime} / 86400\rfloor$.
    - Enables contact discovery without public identity leakage on directory servers.
  - **Relay Directory Persistence & Maintenance**:
    - `relay_peer_directory`:
      - `blinded_locator` (`BLOB` 32 bytes BlindedLocator, PK)
      - `peer_identity_id` (`BLOB` 32 bytes PublicIdentityId)
      - `descriptor_blob` (`BLOB`)
      - `lease_expiry_utc` (`TEXT` ISO 8601, indexed)
      - `signature` (`BLOB` 64 bytes)
      - *Lease Index*: `CREATE INDEX ix_peer_directory_lease ON relay_peer_directory(lease_expiry_utc);`
    - `SqliteRelayPeerDirectory`: stores peer discovery descriptors mapped to blinded locators with TTL leases.
    - `RelayDiscoveryRegistrationPruner`: scheduled worker purging registrations past `lease_expiry_utc`.
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/discovery.proto`.
  - [ ] Implement `SqliteRelayPeerDirectory` (`Percolator.Apps.Discovery.Features.RelayDirectoryHosting.IRelayPeerDirectory`).
  - [ ] Implement `DiscoveryQueryClient` (`Percolator.Apps.Discovery.Ports.INearbyDiscoveryQueryClientPort`).
  - [ ] Implement `DiscoveryHandshakeAdapter` (`Percolator.Apps.Discovery.Ports.IDiscoveryHandshakePort`).
  - [ ] Implement `PreKeyAvailabilityAdapter` (`Percolator.Apps.Discovery.Ports.IPreKeyAvailabilityPort`).
  - [ ] Implement `PreKeyBundleProviderAdapter` (`Percolator.Application.Ports.IPreKeyBundleProvider`).
  - [ ] Implement `GrpcProfileDeliveryClient` (`Percolator.PluginSdk.Profiles.IProfileDeliveryClient`).
  - [ ] Implement `PrivateRendezvousService`: 24-hour UTC epoch blinded locator generator.
  - [ ] Implement `RelayDiscoveryRegistrationPruner` (`IHostedService`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/Discovery/`)**:
  - `PrivateRendezvousServiceTests.BlindedLocator_DeterministicWithin24hEpoch`: computes matching blinded locator within epoch; rolls over deterministically across 24h boundary.
  - `DiscoveryQueryClientTests.NearbyQuery_ClampsCountTo100`: verifies query parameters are strictly clamped to max 100 results.
  - `SqliteRelayPeerDirectoryTests.RegisterAndQuery_MatchesLocator`: registers peer descriptor and queries by blinded locator.
  - `RelayDiscoveryRegistrationPrunerTests.StaleRegistrations_PurgedOnSchedule`: verifies expired peer directory leases are deleted.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.Discovery`

#### Chunk 4.3: Vertical Slice: Encrypted Media Streaming & Blob Store
- **Technical Specifications & Chunked STREAM AEAD**:
  - **STREAM AEAD Binary Framing**:
    - Chunked encryption using AES-256-GCM in 64 KB (65,536 B) blocks.
    - Per-chunk 12-byte nonce composed of 8-byte stream prefix + 4-byte big-endian sequential chunk counter.
    - Final block flag encoded into authenticated additional data (AAD) to prevent truncation or stream splicing attacks.
  - **Media Privacy & Metadata Sanitization**:
    - JPEG / PNG / MP4 privacy scrub: EXIF, IPTC, XMP, GPS coordinates, and camera serial numbers are scrubbed from raw media streams prior to chunking and encryption.
  - **Content-Addressed Local Blob Store**:
    - Files stored at `%AppData%/Percolator/Blobs/{sha256Hex}.blob`.
    - Commits use atomic file staging: payload written to `.tmp` extension, flushed to disk, and moved into place via atomic file replace.
  - **Network Blob Transfer Clients**:
    - `RelayBlobClient` (`IRelayBlobClient`): uploads via `POST /blobs` and downloads via `GET /blobs/{sha256}` supporting HTTP `Range` headers for resumption.
    - `PeerStreamBlobClient` (`IPeerStreamBlobClient`): direct peer-to-peer chunk streaming over ephemeral TLS.
    - `ChannelRelayResolver` (`IChannelRelayResolver`): resolves designated blob relay endpoints.
    - `RelayBlobRetentionPruner`: background pruner enforcing blob expiration and local storage quota watermarks.
- **Scope & Deliverables**:
  - [ ] Implement `StreamingAesGcmCryptoService` (`Percolator.Application.Ports.IBlobCryptoService`).
  - [ ] Implement `LocalFileChunkStore` (`Percolator.Application.Ports.ILocalChunkStore`).
  - [ ] Implement `RelayBlobClient` (`Percolator.Application.Ports.IRelayBlobClient`).
  - [ ] Implement `PeerStreamBlobClient` (`Percolator.Application.Ports.IPeerStreamBlobClient`).
  - [ ] Implement `ChannelRelayResolver` (`Percolator.Application.Ports.IChannelRelayResolver`).
  - [ ] Implement `RelayBlobRetentionPruner` (`IHostedService`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/BlobStorage/`)**:
  - `StreamingAesGcmCryptoServiceTests.ExifScrubbing_StripsGpsAndDeviceMetadata`: strips GPS and device tags from JPEG/PNG before upload.
  - `StreamingAesGcmCryptoServiceTests.ChunkedAead_SingleBitflip_FailsAuthentication`: injects 1 bitflip in 64 KB block; asserts authenticated decryption failure.
  - `StreamingAesGcmCryptoServiceTests.ChunkedAead_Truncation_FailsFinalBlockAuthentication`: truncated stream missing final block flag fails verification.
  - `RelayBlobClientTests.ResumableDownload_AppliesRangeHeader`: HTTP Range header returns byte slice from requested offset without re-downloading from byte 0.
  - `LocalFileChunkStoreTests.AtomicCommit_WritesTempFileAndRenames`: validates atomic write to `%AppData%/Percolator/Blobs/{sha256Hex}.blob`.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.BlobStorage`

#### Chunk 4.4: Vertical Slice: Swarm Multi-Stream File Transfer Engine
- **Technical Specifications, Table Schemas & Binary Framing**:
  - **DDL & Schema Definitions (`ft_*`)**:
    - `ft_transfer_sessions`:
      - `transfer_id` (`TEXT` GUID, PK)
      - `channel_id` (`TEXT` GUID)
      - `manifest_hash` (`BLOB` 32 bytes)
      - `total_size_bytes` (`INTEGER`)
      - `transferred_size_bytes` (`INTEGER`)
      - `status` (`INTEGER`: `Pending=0`, `InProgress=1`, `Completed=2`, `Failed=3`, `Cancelled=4`)
      - `direction` (`INTEGER`: `Inbound=0`, `Outbound=1`)
      - `created_at_utc` (`TEXT` ISO 8601)
    - `ft_manifest_files`:
      - `id` (`TEXT` GUID, PK)
      - `transfer_id` (`TEXT` GUID, FK to `ft_transfer_sessions(transfer_id)` ON DELETE CASCADE)
      - `relative_path` (`TEXT`)
      - `file_size_bytes` (`INTEGER`)
      - `merkle_root` (`BLOB` 32 bytes)
    - `ft_transfer_bitfields`:
      - `transfer_id` (`TEXT` GUID, PK, FK to `ft_transfer_sessions(transfer_id)` ON DELETE CASCADE)
      - `bitfield_bytes` (`BLOB`)
      - `total_pieces` (`INTEGER`)
      - `verified_pieces` (`INTEGER`)
    - `ft_swarm_endpoints`:
      - `transfer_id` (`TEXT` GUID)
      - `peer_id` (`BLOB` 32 bytes)
      - `endpoint` (`TEXT`)
      - `last_seen_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(transfer_id, peer_id)`
    - `ft_filter_policies`:
      - `peer_id` (`BLOB` 32 bytes, PK)
      - `auto_accept_under_bytes` (`INTEGER`)
      - `is_blocked` (`INTEGER` boolean)
  - **Swarm Binary Framing Protocol (0x01–0x0C)**:
    - Fixed 5-byte header: `[Opcode: 1 Byte] [Payload Length: 4 Bytes Big-Endian UInt] [Payload: Variable Length]`
    - Maximum payload size: `262,144 bytes` (256 KB); standard piece chunk size: `65,536 bytes` (64 KB).
    - Opcode framing definitions:
      - `0x01` `STREAM_HANDSHAKE`: `[TransferId: 16 B GUID] [PeerIdentityId: 32 B] [SessionAuthToken: 32 B]` (big-endian UUID normalization via `Guid.ToByteArray(bigEndian: true)`)
      - `0x02` `BITFIELD`: `[BitfieldBytes: N B]` (1 bit per piece, byte-padded)
      - `0x03` `HAVE`: `[PieceIndex: UInt32 BE]`
      - `0x04` `REQUEST_CHUNK`: `[PieceIndex: UInt32 BE] [ChunkOffset: UInt32 BE] [Length: UInt32 BE]`
      - `0x05` `CHUNK_DATA`: `[PieceIndex: UInt32 BE] [ChunkOffset: UInt32 BE] [RawBytes: N B]`
      - `0x06` `CANCEL_CHUNK`: `[PieceIndex: UInt32 BE] [ChunkOffset: UInt32 BE]`
      - `0x07` `CHOKE`: (Zero payload)
      - `0x08` `UNCHOKE`: (Zero payload)
      - `0x09` `INTERESTED`: (Zero payload)
      - `0x0A` `NOT_INTERESTED`: (Zero payload)
      - `0x0B` `KEEP_ALIVE`: (Zero payload)
      - `0x0C` `PEX_PEERS`: `[Count: UInt16 BE] repeated: [IPLength: 1 B] [IPBytes: 4 or 16 B] [Port: UInt16 BE] [Flags: 1 B]`
  - **Wire Contracts (`Percolator.Infrastructure/Protos/file_transfer.proto`)**:
    - `TransferManifestProto`: concatenated 32-byte leaf array without per-leaf tags, total size, piece count, piece hash list.
    - `FileEntryProto`: relative file path, size, Merkle root.
    - `FileTransferOfferProto`: transfer ID, manifest hash, sender identity ID, total bytes.
    - `FileTransferAcceptProto`: transfer ID, receiver bitfield, candidate endpoints.
    - `FileTransferCancelProto`: transfer ID, cancellation reason code.
  - **Portable Desktop File Store Guarantees**:
    - Pre-allocation via `FileStream.SetLength` for cross-platform zero-fill file sizing.
    - Zero-byte files materialize immediately without temporary staging.
    - Active piece staging via `.percolator-part` extension with atomic rename upon full verification.
    - Win32 reserved device name and NTFS ADS sanitization: rejects paths containing `CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`, `:` ADS streams, or directory traversals (`..`).
  - **Swarm Multiplexing & Rate Limiting**:
    - `TcpMultiStreamPool`: manages 4–8 multiplexed TCP connections with `TCP_NODELAY = true`.
    - `TokenBucketRateLimiter`: smooth bandwidth allocation across active streams.
    - `RelayTransferPipeClient`: 4–8 parallel streams over relay transport with `X-Pipe-Auth` authentication.
    - `RelaySwarmTrackerClient`: tracker client with sliding lease expirations.
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/file_transfer.proto`.
  - [ ] Implement File Transfer Persistence & CQRS Query Services:
    - `SqliteTransferSessionRepository` (`Percolator.Apps.FileTransfer.Ports.ITransferSessionRepository`)
    - `SqliteFilterPolicyRepository` (`Percolator.Apps.FileTransfer.Ports.IFileTransferFilterPolicyRepository`)
    - `SqlFileTransferQueryService` (`Percolator.Apps.FileTransfer.Ports.IFileTransferQueryService`)
  - [ ] Implement Swarm Multi-Stream Protocol Engine & Desktop File Store:
    - `TcpMultiStreamPool` (`Percolator.Apps.FileTransfer.Ports.ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`)
    - `PortableDesktopFileStore` (`Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage`)
    - `TokenBucketRateLimiter` (`Percolator.Apps.FileTransfer.Ports.ITransferRateLimiter`)
    - `NetworkInterfaceEndpointDiscoveryPort` (`Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort`)
    - `RelayTransferPipeClient`
    - `RelaySwarmTrackerClient` (`Percolator.Apps.FileTransfer.Ports.IDistributedTrackerPort`)
  - [ ] Implement Swarm Lifecycle Pruners:
    - `TransferOfferReaperWorker` (`Percolator.Apps.FileTransfer.Ports.ITransferOfferReaperService`)
    - `SwarmEndpointRetentionPruner` (`IHostedService`)
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/FileTransfer/`)**:
  - `TcpMultiStreamPoolTests.FrameSerialization_All12Frames_RoundtripsSuccessfully`: serializes and deserializes all 12 frames (0x01–0x0C).
  - `PortableDesktopFileStoreTests.Sanitization_RejectsReservedWin32NamesAndAds`: rejects `CON`, `PRN`, `AUX`, `:` ADS, and path traversals without touching disk.
  - `PortableDesktopFileStoreTests.ZeroByteFile_MaterializesImmediately`: creates zero-byte file immediately without `.percolator-part`.
  - `PortableDesktopFileStoreTests.BitfieldScanner_MatchesMerkleLeaves`: validates existing chunks against Merkle leaf hashes.
  - `TransferOfferReaperWorkerTests.StaleOffers_OlderThan24h_Reaped`: advances virtual clock by 25h with `FakeTimeProvider`; asserts stale offers marked cancelled.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.FileTransfer`

---

### Phase 5: Operational Workers, Two-Stage Composition Root & System Alignment

#### Chunk 5.1: Scheduled Operational Pruning Workers
- **Technical Specifications & Database Maintenance Mechanics**:
  - **SQLite WAL Concurrency & Lock Escalation Avoidance**:
    - Under SQLite WAL mode, large unbatched `DELETE` operations lock the database against writer concurrency, stall checkpointing, and lead to UI thread latency.
    - All background pruning workers MUST consume `IDbContextFactory<PercolatorDbContext>` (from Chunk 2.1) rather than a long-lived scoped `DbContext` or `IServiceScopeFactory`, creating short-lived, single-operation contexts.
    - Eviction runs in chunks using `LIMIT` (batch size: 500 rows per transaction) looping until zero rows match or `CancellationToken` cancels:
      ```csharp
      // Batch deletion pattern in background workers:
      while (!ct.IsCancellationRequested)
      {
          await using var context = await _contextFactory.CreateDbContextAsync(ct);
          var expiredIds = await context.OutboxJobs
              .Where(j => j.Status == OutboxStatus.Delivered && j.CreatedAtUtc <= cutoffTime)
              .OrderBy(j => j.CreatedAtUtc)
              .Select(j => j.Id)
              .Take(500)
              .ToListAsync(ct);

          if (expiredIds.Count == 0) break;

          context.OutboxJobs.RemoveRange(expiredIds.Select(id => new OutboxJob { Id = id }));
          await context.SaveChangesAsync(ct);
          
          if (expiredIds.Count < 500) break;
      }
      ```
  - **Pruning Worker Implementations**:
    - `OutboxRetentionPruner` (`IHostedService`):
      - Sweeps delivered outbox jobs (`ops_outbox_jobs`) older than 7 days (`TimeSpan.FromDays(7)`). Preserves pending, in-flight, or failed jobs for inspection/retry.
      - Scheduled interval: 24 hours (or on virtual clock tick via `TimeProvider`).
    - `RelayPreKeyDirectoryPruner` (`IHostedService`):
      - Sweeps consumed or stale one-time prekeys and expired signed prekey bundles from `relay_prekeys` where `updated_at_utc <= cutoffTime` (older than 30 days).
      - Scheduled interval: 24 hours.
    - `RelayProfileSlotPruner` (`IHostedService`):
      - Sweeps expired profile slots from `relay_profile_slots` where `expires_at_utc <= DateTimeOffset.UtcNow`.
      - Scheduled interval: 1 hour (utilizes indexed lookup on `expires_at_utc`).
- **Scope & Deliverables**:
  - [ ] Implement Scheduled `IHostedService` Pruning Workers:
    - `OutboxRetentionPruner` (`Microsoft.Extensions.Hosting.BackgroundService`)
    - `RelayPreKeyDirectoryPruner` (`Microsoft.Extensions.Hosting.BackgroundService`)
    - `RelayProfileSlotPruner` (`Microsoft.Extensions.Hosting.BackgroundService`)
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Maintenance/`)**:
  - `OutboxRetentionPrunerTests.DeliveredJobs_OlderThan7Days_Purged`: advances virtual clock by 8 days with `FakeTimeProvider`; asserts old delivered jobs purged while recent jobs preserved.
  - `OutboxRetentionPrunerTests.FailedAndPendingJobs_PreservedRegardlessOfAge`: asserts failed/queued jobs are never deleted by retention pruner.
  - `RelayProfileSlotPrunerTests.ExpiredSlots_PurgedInBatches`: seeds 1,200 expired profile slots; executes pruner; verifies all 1,200 slots deleted across 500-item batches without SQLite lock exceptions.
  - `PrunerLockSafetyTests.RetentionPruning_UsesBatchingUnderConcurrency`: deletion uses `LIMIT` clauses to avoid SQLite lock escalation under concurrent read workload.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Maintenance`

#### Chunk 5.2: Stage 2 DI Composition Root & Infrastructure Verification
- **Technical Specifications & Lifetime Architecture**:
  - **Two-Stage Container Separation Protocol**:
    - *Stage 1 (Pre-Unlock Bootstrap Container)*: Contains zero SQLite DbContexts, zero repositories, and zero network/sync workers. Registers `SQLitePCL.Batteries_V2.Init()`, `SignalCryptoEngine`, `SystemDateTimeProvider`, `ProtobufHeaderStorage`, `DuressWipeHandler`, and `SqlCipherDatabaseUnlockCoordinator`.
    - *Stage 2 (Authenticated Host - `IHost`)*: Built post-unlock via `Stage2HostFactory`. Hosts `IMasterKeyHandle` (scoped to session in pinned unmanaged memory), `SqlCipherPragmaInterceptor`, `PercolatorDbContext` (scoped/pooled), all 58 infrastructure ports, and `IHostedService` background workers.
    - *Stage 2 (Authenticated Host - `IHost`)*: Built post-unlock via `Stage2HostFactory`. Hosts `IMasterKeyHandle` (scoped to session in pinned unmanaged memory), `SqlCipherPragmaInterceptor`, `PercolatorDbContext` (scoped/pooled), all 59 infrastructure ports, and `IHostedService` background workers.
  - **Service Lifetime Registration Topology**:
    - **Singletons**:
      - `IMasterKeyHandle` (instance passed from unlock coordinator)
      - `IDbContextFactory<PercolatorDbContext>` & `PercolatorDbContextOptions`
      - Core Cryptography & Time: `ICryptoEngine`, `IZkProofEngine`, `IDateTimeProvider`
      - Networking & Stream Management: `StreamRegistry`, `TcpMultiStreamPool`, `TokenBucketRateLimiter`, `LocalFileChunkStore`
      - Interceptors: `DeliveryCertificateAuthInterceptor`
      - Protocol Serializers: `ProtobufSessionWirePacker`, `ProtobufPayloadSerializer`, `ChatPayloadFramingDecorator`
    - **Scoped**:
      - `PercolatorDbContext` (pooled context per scope)
      - All 59 Repositories & CQRS Query Services (Outbox, Contacts, Ratchet, Channels, Relay prekeys/profiles/ledger, Chat, File Transfer)
      - Workflow & Dispatching: `TransportDispatcher`, `IngressFilterService`, `SealedEnvelopeUnwrapper`, `DiscoveryQueryClient`, `DiscoveryHandshakeAdapter`, `StreamingAesGcmCryptoService`, `RelayBlobClient`, `PeerStreamBlobClient`, `ChannelRelayResolver`, `RelayTransferPipeClient`, `RelaySwarmTrackerClient`
    - **Hosted Services (`services.AddHostedService<T>()`)**:
      - All 10 background pruning, syncing, and lifecycle workers across the application:
        1. `OutboxRetentionPruner` (Chunk 5.1)
        2. `RelayPreKeyDirectoryPruner` (Chunk 5.1)
        3. `RelayProfileSlotPruner` (Chunk 5.1)
        4. `DisappearingMessageEvictionWorker` (Chunk 4.1)
        5. `UnknownGroupMessageCachePruner` (Chunk 4.1)
        6. `AbandonedInvitePruner` (Chunk 4.1)
        7. `RelayDiscoveryRegistrationPruner` (Chunk 4.2)
        8. `RelayBlobRetentionPruner` (Chunk 4.3)
        9. `TransferOfferReaperWorker` (Chunk 4.4)
        10. `SwarmEndpointRetentionPruner` (Chunk 4.4)
  - **Platform Separation Boundary & Windows Extensibility Hook**:
    - Standard `AddAuthenticatedInfrastructure` registers 100% portable baseline implementations: `PortableDesktopFileStore` (`IDesktopFileStorage`) and `DefaultTlsCertificateProvider` (`ITlsCertificateProvider`).
    - Extensibility point: `Percolator.Infrastructure.Windows` (`net10.0-windows`) provides extension method `AddWindowsInfrastructureOverrides(this IServiceCollection services)` which swaps in:
      1. `WindowsSparseDesktopFileStore`: Win32 `FSCTL_SET_SPARSE` (`DeviceIoControl` `0x000900C4`) for instant zero-write file pre-allocation on NTFS.
      2. `WindowsSChannelCertificateProvider`: Temporary PKCS#12 `.pfx` on-disk export with CNG key deletion and antivirus lock retries for Windows SChannel TLS.
  - **Full Container Verification Guarantee**:
    - `Stage2HostFactory` builds `IHost` with service provider validation (`ValidateScopes = true`, `ValidateOnBuild = true`), guaranteeing zero captive dependencies, zero missing registrations, and zero circular dependencies.
- **Scope & Deliverables**:
  - [ ] Implement `Stage2HostFactory`:
    - Extends `BaselineStage2HostFactory` to wire the full suite of operational adapters, repositories, query services, gRPC channels, and background pruners across all feature slices.
  - [ ] Implement `InfrastructureServiceCollectionExtensions.AddAuthenticatedInfrastructure(IServiceCollection, IConfiguration, IMasterKeyHandle)`:
    - Registers all 59 infrastructure ports into the Stage 2 container with correct lifetime scopes.
    - Registers all 10 background `IHostedService` workers.
    - Exposes extension point for `Percolator.Infrastructure.Windows` overrides.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Composition/`)**:
  - `DependencyInjectionTests.All59Ports_ResolveFromContainerWithoutCycles`: resolves every planned infrastructure port from DI container; asserts non-null without circular dependency or missing registration.
  - `DependencyInjectionTests.NoCaptiveDependencies_ScopedServicesNeverInjectedIntoSingletons`: enforces container scope validation (`ValidateScopes = true`); verifies no scoped DbContext or repository is injected into singleton services or pruners.
  - `DependencyInjectionTests.All10HostedServices_RegisteredAndStartable`: resolves `IEnumerable<IHostedService>` and asserts exact count of 10 registered background workers.
  - `DependencyInjectionTests.WindowsOverrides_CanSwapFileStoreAndCertProvider`: calls optional Windows overlay registrations; asserts `IDesktopFileStorage` resolves to `WindowsSparseDesktopFileStore`.
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
| 1 | `ICryptoEngine` | `Percolator.Domain` | `SignalCryptoEngine` / `SodiumCryptoEngine` | Chunk 3.1 |
| 2 | `IZkProofEngine` | `Percolator.Domain` | `ZkgroupCryptographyService` | Chunk 3.1 |
| 3 | `IDateTimeProvider` | `Percolator.Domain.Common` | `SystemDateTimeProvider` | Chunk 1.1 |
| 4 | `IDatabaseUnlockCoordinator` | `Percolator.Application` | `SqlCipherDatabaseUnlockCoordinator` | Chunk 1.4 |
| 5 | `IAuthenticatedSession` | `Percolator.Application` | `AuthenticatedSession` | Chunk 1.4 |
| 6 | `IMasterKeyHandle` | `Percolator.Domain.Security.Ports` | `MasterKeyHandle` | Chunk 1.3 |
| 7 | `IDuressWipeHandler` | `Percolator.Application` | `DuressWipeHandler` | Chunk 1.2 |
| 8 | `IHeaderStorage` | `Percolator.Infrastructure.Vault` (Internal) | `ProtobufHeaderStorage` | Chunk 1.2 |
| 9 | `ISessionWirePacker` | `Percolator.Application` | `ProtobufSessionWirePacker` | Chunk 3.3 |
| 10 | `IPayloadSerializer` | `Percolator.PluginSdk` | `ProtobufPayloadSerializer` | Chunk 3.3 |
| 11 | `DbConnectionInterceptor` | `Microsoft.EntityFrameworkCore.Diagnostics` | `SqlCipherPragmaInterceptor` | Chunk 1.3 |
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
| 22 | `IRelayPreKeyRepository` | `Percolator.Domain` | `SqliteRelayPreKeyRepository` | Chunk 2.5 |
| 23 | `IRelayProfileRepository` | `Percolator.Domain` | `SqliteRelayProfileRepository` | Chunk 2.5 |
| 24 | `IPeerProfileRepository` | `Percolator.PluginSdk` | `SqlitePeerProfileRepository` | Chunk 2.5 |
| 25 | `IPeerMailboxRoutingRepository` | `Percolator.PluginSdk` | `SqlitePeerMailboxRoutingRepository` | Chunk 2.5 |
| 26 | `IRelayLedgerRepository` | `Percolator.Domain` | `SqliteRelayLedgerRepository` | Chunk 2.5 |
| 27 | `IChatMessageRepository` | `Percolator.Apps.Chat` | `SqlChatMessageRepository` | Chunk 4.1 |
| 28 | `IGroupInvitationRepository` | `Percolator.Apps.Chat` | `SqlGroupInvitationRepository` | Chunk 4.1 |
| 29 | `IUnknownGroupMessageCacheRepository` | `Percolator.Apps.Chat` | `SqlUnknownGroupMessageCacheRepository` | Chunk 4.1 |
| 30 | `ITransferSessionRepository` | `Percolator.Apps.FileTransfer` | `SqliteTransferSessionRepository` | Chunk 4.4 |
| 31 | `IFileTransferFilterPolicyRepository` | `Percolator.Apps.FileTransfer` | `SqliteFilterPolicyRepository` | Chunk 4.4 |
| 32 | `IRelayPeerDirectory` | `Percolator.Apps.Discovery` | `SqliteRelayPeerDirectory` | Chunk 4.2 |
| 33 | `IChatMessageQueryService` | `Percolator.Apps.Chat` | `SqlChatMessageQueryService` | Chunk 4.1 |
| 34 | `IConversationListQueryService` | `Percolator.Apps.Chat` | `SqlConversationListQueryService` | Chunk 4.1 |
| 35 | `IGroupInvitationQueryService` | `Percolator.Apps.Chat` | `SqlGroupInvitationQueryService` | Chunk 4.1 |
| 36 | `IOutboxQueryService` | `Percolator.Application` | `SqlOutboxQueryService` | Chunk 2.3 |
| 37 | `IPeerContactQueryService` | `Percolator.Application` | `SqlPeerContactQueryService` | Chunk 2.4 |
| 38 | `IFileTransferQueryService` | `Percolator.Apps.FileTransfer` | `SqlFileTransferQueryService` | Chunk 4.4 |
| 39 | `IIngressFilterService` | `Percolator.Application` | `IngressFilterService` | Chunk 3.5 |
| 40 | `IStreamRegistry` | `Percolator.Application` | `StreamRegistry` | Chunk 3.5 |
| 41 | `ITransportDispatcher` | `Percolator.Application.Delivery.Ports.ITransportDispatcher` | `TransportDispatcher` | Chunk 3.5 |
| 42 | `ISealedEnvelopeUnwrapper` | `Percolator.Application` | `SealedEnvelopeUnwrapper` | Chunk 3.5 |
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
| 53 | `ITransferStreamPool` / `Factory` / `Stream` | `Percolator.Apps.FileTransfer` | `TcpMultiStreamPool` & `TransferStream` | Chunk 4.4 |
| 54 | `INetworkEndpointDiscoveryPort` | `Percolator.Apps.FileTransfer` | `NetworkInterfaceEndpointDiscoveryPort` | Chunk 4.4 |
| 55 | `IDesktopFileStorage` | `Percolator.Apps.FileTransfer` | `PortableDesktopFileStore` | Chunk 4.4 |
| 56 | `ITransferRateLimiter` | `Percolator.Apps.FileTransfer` | `TokenBucketRateLimiter` | Chunk 4.4 |
| 57 | `IDistributedTrackerPort` | `Percolator.Apps.FileTransfer` | `RelaySwarmTrackerClient` | Chunk 4.4 |
| 58 | `ITransferOfferReaperService` | `Percolator.Apps.FileTransfer` | `TransferOfferReaperWorker` | Chunk 4.4 |
| 59 | `IIdentityProfileRepository` | `Percolator.Domain` | `SqliteIdentityProfileRepository` | Chunk 2.2 |
