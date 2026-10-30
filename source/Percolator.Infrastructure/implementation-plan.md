# Percolator.Infrastructure Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [x] Implementation Gate: UNLOCKED

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
  - [x] Scaffold `Percolator.Infrastructure/Percolator.Infrastructure.csproj` targeting `net10.0`, nullable enabled, referencing `Percolator.Domain`, `Percolator.Application`, and `Percolator.PluginSdk`.
    - Package dependencies:
      - `Microsoft.EntityFrameworkCore.Sqlite.Core` (v10.x)
      - `SQLitePCLRaw.bundle_e_sqlcipher` (v2.1.x+)
      - `Konscious.Security.Cryptography.Argon2` (v1.3.x)
      - `Google.Protobuf` (v3.25.2)
      - `Grpc.Tools` (v2.57.0)
  - [x] Implement `SystemDateTimeProvider` (`Percolator.Domain.Common.IDateTimeProvider`) returning `DateTimeOffset.UtcNow`.
  - [x] Implement RFC 4122 Big-Endian UUID extension (`Guid.ToByteArray(bigEndian: true)`).
  - [x] Scaffold `Percolator.Infrastructure.Tests` test project targeting `net10.0` with xUnit, FluentAssertions, and FakeItEasy.
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
      int32 argon2_memory_kb = 2;   // e.g. 65536 (64 MB)
      int32 argon2_iterations = 3;  // e.g. 3
      int32 argon2_parallelism = 4; // e.g. 4
      bytes master_slot_salt = 5;   // 16-byte random salt for Argon2id
      bytes master_slot_nonce = 6;  // 12-byte ChaCha20-Poly1305 nonce
      bytes master_slot_payload = 7;// 48 bytes: Encrypted 32-byte master key + 16-byte Poly1305 tag
      bytes duress_slot_salt = 8;   // 16-byte random salt (or CSPRNG noise if disabled)
      bytes duress_slot_nonce = 9;  // 12-byte nonce (or random noise)
      bytes duress_slot_payload = 10;// 48 bytes: Encrypted 32-byte sentinel + tag (or dummy random noise)
    }
    ```
  - **Plausible Deniability Invariant**: When no duress password is set, `duress_slot_salt` (16 bytes), `duress_slot_nonce` (12 bytes), and `duress_slot_payload` (48 bytes) are filled with `RandomNumberGenerator.GetBytes(76)`. Wire length is strictly ~140 bytes in all configurations.
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
  - [x] Author Protobuf schema `Percolator.Infrastructure/Protos/DatabaseHeader.proto`.
  - [x] Define internal interface `Percolator.Infrastructure.Vault.IHeaderStorage`.
  - [x] Implement `ProtobufHeaderStorage` (`IHeaderStorage`): reads/writes header with temp file staging and atomic replace.
  - [x] Implement `DuressWipeHandler` (`Percolator.Application.Ports.IDuressWipeHandler`): instant crypto-shredding pipeline.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/`)**:
  - `ProtobufHeaderStorageTests.PlausibleDeniability_WireSize_IdenticalWithOrWithoutDuress`: header with dummy random noise vs active duress has identical 76-byte duress slot wire size and uniform total header length; decryption of dummy duress slot throws `AuthenticationTagMismatchException`.
  - `ProtobufHeaderStorageTests.CorruptedHeader_TrapsGracefully`: 0-byte or truncated `percolator.header` traps `InvalidProtocolBufferException` returning structured failure without uncaught crashes.
  - `DuressWipeHandlerTests.ExecuteWipeAsync_DestroysHeaderAndDeletesDatabaseAndCacheFiles`: asserts observable state changes—`percolator.header` is destroyed, `percolator.db*` and `cache/` are deleted, and subsequent `ProtobufHeaderStorage.ReadHeaderAsync` returns failure without flaky wall-clock timing assertions.
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
                using var cmd = sqliteConn.CreateCommand();
                cmd.CommandText = "PRAGMA cipher_memory_security = ON;";
                cmd.ExecuteNonQuery();

                _keyHandle.ApplyKeyToConnection(sqliteConn);

                cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
                cmd.ExecuteNonQuery();
            }
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (connection is SqliteConnection sqliteConn)
            {
                await using var cmd = sqliteConn.CreateCommand();
                cmd.CommandText = "PRAGMA cipher_memory_security = ON;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                _keyHandle.ApplyKeyToConnection(sqliteConn);

                cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
    ```
- **Scope & Deliverables**:
  - [x] Implement `MasterKeyHandle` (`Percolator.Domain.Security.Ports.IMasterKeyHandle`).
  - [x] Implement `SqlCipherPragmaInterceptor` (`Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/Keying/`)**:
  - `MasterKeyHandleTests.Dispose_ZeroizesPinnedMemory`: unmanaged buffer memory is zeroized on dispose; subsequent access throws `ObjectDisposedException`.
  - `MasterKeyHandleTests.WithKeySpan_YieldsValid32ByteKeySpan`: visitor executes with exact 32-byte key span matching initial key.
  - `SqlCipherPragmaInterceptorTests.ConnectionOpened_AppliesKeyAndSecurityPragmas`: verifies interceptor applies raw key, WAL/synchronous, foreign keys, and memory security PRAGMAs.
  - `SqlCipherPragmaInterceptorTests.ConnectionOpenedAsync_AppliesKeyAndSecurityPragmas`: verifies asynchronous interceptor hook applies raw key and security PRAGMAs.
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
        // 1. Evaluate Master Slot (derive with master_slot_salt and header KDF parameters)
        byte[] derivedMasterCandidateKey = DeriveArgon2idKey(
            candidatePassword,
            header.MasterSlotSalt.Span,
            header.Argon2MemoryKb,
            header.Argon2Iterations,
            header.Argon2Parallelism);
        try
        {
            using var cipher = new ChaCha20Poly1305(derivedMasterCandidateKey);
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
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedMasterCandidateKey);
        }

        // 2. Evaluate Duress Slot (derive with duress_slot_salt and header KDF parameters)
        byte[] derivedDuressCandidateKey = DeriveArgon2idKey(
            candidatePassword,
            header.DuressSlotSalt.Span,
            header.Argon2MemoryKb,
            header.Argon2Iterations,
            header.Argon2Parallelism);
        try
        {
            using var cipher = new ChaCha20Poly1305(derivedDuressCandidateKey);
            Span<byte> candidatePlaintext = stackalloc byte[32];
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
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedDuressCandidateKey);
        }

            return new SlotTrialResult(SlotTrialOutcome.InvalidCredentials);
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
    2. Call `_envelopePacker.CreateEnvelope(masterPassword, duressPassword, keySpan)` generating decoupled 16-byte salts for master and duress slots, encoding Argon2id parameters ($m=64\text{MB}, t=3, p=4$) and ChaCha20-Poly1305 ciphertexts.
    3. Atomically write `DatabaseHeader` via `_headerStorage.WriteHeaderAsync`.
    4. **Cold Launch Self-Healing Invariant**: If `percolator.header` was missing or wiped, force unlink any orphaned `.db*` ciphertext files before creating the new database.
    5. **Canary & Migration Memory Guard**: Probe canary via `_canaryProbe.ProbeAsync` and execute `_migrator.MigrateAsync(ct)` inside a `try-catch/finally` block. If canary verification fails (`FailedNotADatabase` / `Corrupted`) or `MigrateAsync` fails/throws, immediately dispose `_masterKeyHandle` (zeroizing unmanaged memory), reset `_masterKeyHandle = null`, and abort initialization.
    6. On verified completion, invoke `_hostFactory.BuildAuthenticatedHost(_masterKeyHandle)` and return `UnlockResult.Success(new AuthenticatedSession(host))`.
  - **`UnlockAsync(candidatePassword, ct)`**:
    1. Read `DatabaseHeader` via `_headerStorage.ReadHeaderAsync`.
    2. Allocate unmanaged pinned buffer in `MasterKeyHandle`.
    3. Evaluate slots via `_envelopePacker.EvaluateSlots(header, candidatePassword, keySpanBuffer)`:
       - Master slot decrypted $\rightarrow$ **Canary & Migration Guard**: Probe canary via `_canaryProbe.ProbeAsync`. If canary returns `FailedNotADatabase` or `Corrupted`, or if `_migrator.MigrateAsync(ct)` fails/throws, immediately dispose `_masterKeyHandle`, zeroize unmanaged memory, reset `_masterKeyHandle = null`, and return `UnlockResult.InvalidCredentials()`. If `Verified` and migrated successfully, assemble host via `_hostFactory.BuildAuthenticatedHost` and return `UnlockResult.Success(session)`.
       - Duress slot decrypted and matches `DuressSentinelBytes` $\rightarrow$ zeroize key memory, execute `_wipeHandler.ExecuteWipeAsync(ct)`, transition status to `Uninitialized`, return `UnlockResult.DuressActivated()`.
       - All slot trials fail $\rightarrow$ zeroize key memory, dispose `MasterKeyHandle`, return `UnlockResult.InvalidCredentials()`.
  - **`ChangeMasterPasswordAsync(currentPassword, newPassword, ct)` ($O(1)$ Re-Wrap)**:
    1. Validate active session is unlocked.
    2. Validate `currentPassword` against active key handle via slot trial.
    3. Call `_envelopePacker.RewrapMasterSlot(currentHeader, newPassword, activeKeySpan)`.
    4. **Salt Rotation & Duress Slot Preservation**: Generates a fresh random 16-byte `master_slot_salt` and fresh nonce for the master slot. Because `header.DuressSlotSalt` and duress payload remain completely untouched, the duress slot remains 100% valid without requiring the user to supply or re-enter the duress passphrase. Because $K_{\text{db}}$ remains identical, zero SQLite tables or pages are rewritten ($O(1)$ re-wrap completes in $< 200\text{ ms}$).
    5. Atomically overwrite header via `_headerStorage.WriteHeaderAsync`.
    6. Zeroize temporary key spans via `CryptographicOperations.ZeroMemory`.
  - **`ConfigureDuressPasswordAsync(currentMasterPassword, duressPassword, ct)`**:
    1. Validate active session is unlocked.
    2. **Re-Authentication Guard**: Validate `currentMasterPassword` unlocks master slot before permitting duress reconfiguration (prevents malicious duress trap or sabotage on unattended unlocked terminals).
    3. Validate `duressPassword` is distinct from `currentMasterPassword` (`!VaultPassphrase.ConstantTimeEquals(currentMasterPassword, duressPassword)`).
    4. If setting/updating: derive fresh 16-byte `duress_slot_salt`, fresh nonce, and encrypt `DuressSentinelBytes`. If clearing (`null` or empty): populate duress slot with 76 bytes of CSPRNG noise.
    5. Call `_envelopePacker.ConfigureDuressSlot(currentHeader, duressPassword)`.
    6. Atomically overwrite header via `_headerStorage.WriteHeaderAsync`.
    7. *Port Note*: Update `IDatabaseUnlockCoordinator.ConfigureDuressPasswordAsync(VaultPassphrase currentMasterPassword, VaultPassphrase? duressPassword, CancellationToken ct = default)` to enforce re-authentication.
  - **`LockAsync()`**:
    1. Stop all hosted services in Stage 2 `IHost` (`await host.StopAsync(ct)`).
    2. Dispose Stage 2 `IHost` (releasing all pooled `PercolatorDbContext` and `IDbContextFactory` connections back to the ADO.NET pool).
    3. Clear connection pools via `SqliteConnection.ClearAllPools()` (closing all physical SQLCipher connections and purging decrypted page caches).
    4. Dispose `_masterKeyHandle` (zeroizing unmanaged memory last to prevent `ObjectDisposedException` during host teardown).
    5. Transition status to `Locked`.
- **Scope & Deliverables**:
  - [x] Define interfaces `IVaultEnvelopePacker`, `IDatabaseCanaryProbe`, `IDatabaseMigrator`, and `IStage2HostFactory`.
  - [x] Implement `VaultEnvelopePacker` (`IVaultEnvelopePacker`): Argon2id derivation ($m=64\text{MB}, t=3, p=4$), ChaCha20-Poly1305 slot encryption, and fixed 32-byte `DuressSentinelBytes` matching (`SHA256("PERCOLATOR_VAULT_DURESS_SENTINEL_V1")`).
  - [x] Implement `DatabaseCanaryProbe` (`IDatabaseCanaryProbe`): executes `SELECT count(*) FROM sqlite_master;` on isolated probe connection, explicitly trapping native error code 26 (`SQLITE_NOTADB`).
  - [x] Implement `SqlCipherDatabaseUnlockCoordinator` (`Percolator.Application.Ports.IDatabaseUnlockCoordinator`).
  - [x] Implement `AuthenticatedSession` (`Percolator.Application.Ports.IAuthenticatedSession`).
  - [x] Implement `InfrastructureServiceCollectionExtensions.AddBootstrapInfrastructure(IServiceCollection, IConfiguration)`:
    - Calls `SQLitePCL.Batteries_V2.Init()`.
    - Registers `SystemDateTimeProvider`, `ProtobufHeaderStorage`, `DuressWipeHandler`, `VaultEnvelopePacker`, `DatabaseCanaryProbe`, and `SqlCipherDatabaseUnlockCoordinator`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Vault/Unlock/`)**:
  - `DatabaseCanaryProbeTests.InvalidKey_TrapsSqliteErrorCode26`: invalid key traps `SqliteException` with `SqliteErrorCode == 26` (`SQLITE_NOTADB`) and returns `CanaryProbeResult.FailedNotADatabase`.
  - `VaultEnvelopePackerTests.MasterPasswordChange_RotatesMasterSalt_PreservesDuress`: generates fresh master salt, re-wraps master slot with new key, preserves independent duress salt and duress slot; both decrypt successfully.
  - `SqlCipherUnlockTests.HappyPathMasterUnlock_CoordinatesCollaborators`: correct master password derives key, evaluates master slot, executes canary probe, invokes migrator, and returns `UnlockResult.Success(session)`.
  - `SqlCipherUnlockTests.InvalidPassword_ReturnsInvalidCredentials`: incorrect password fails master and duress slots; returns `UnlockResult.InvalidCredentials`.
  - `SqlCipherUnlockTests.CanaryOrMigrationFailure_DisposesAndZeroizesMasterKeyHandle`: verifies handle disposal and unmanaged memory zeroization when canary returns not a database or migration throws.
  - `SqlCipherUnlockTests.BlankPassword_CreatesEncryptedDatabase_AndUnkeyedConnectionFails`: setup with `VaultPassphrase.Empty` creates encrypted database; raw unkeyed connection fails with native code 26 (`SQLITE_NOTADB`); unlock with empty passphrase succeeds.
  - `SqlCipherUnlockTests.LockAsync_ScrubsMemoryAndClearsPools`: calling `LockAsync()` clears connection pools (`SqliteConnection.ClearAllPools()`), zeroizes `MasterKeyHandle`, disposes host, transitions status to `Locked`.
  - `SqlCipherUnlockTests.ChangeMasterPasswordAsync_RewrapsMasterSlot_PreservesDuressSlot`: re-wraps master slot with new key; database remains readable; subsequent unlock requires new password; existing duress password remains valid.
  - `SqlCipherUnlockTests.ConfigureDuressPasswordAsync_RequiresMasterReAuthentication`: validates master password re-authentication before updating duress slot; rejects invalid master password.
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
                  Span<byte> colKey = stackalloc byte[32];
                  HKDF.DeriveKey(HashAlgorithmName.SHA256, keySpan, colKey, salt: default, info: "Percolator.ColumnLevelEncryption.v1"u8);
                  try
                  {
                      using var aes = new AesGcm(colKey, AesGcm.TagByteSizes.MaxSize);
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
                  }
                  finally
                  {
                      CryptographicOperations.ZeroMemory(colKey);
                  }
              });
          }

          private static byte[] Decrypt(byte[]? payload, IMasterKeyHandle masterKeyHandle)
          {
              if (payload == null || payload.Length < 28) return Array.Empty<byte>();

              return masterKeyHandle.WithKeySpan(keySpan =>
              {
                  Span<byte> colKey = stackalloc byte[32];
                  HKDF.DeriveKey(HashAlgorithmName.SHA256, keySpan, colKey, salt: default, info: "Percolator.ColumnLevelEncryption.v1"u8);
                  try
                  {
                      using var aes = new AesGcm(colKey, AesGcm.TagByteSizes.MaxSize);
                      ReadOnlySpan<byte> nonce = payload.AsSpan(0, 12);
                      ReadOnlySpan<byte> tag = payload.AsSpan(12, 16);
                      ReadOnlySpan<byte> ciphertext = payload.AsSpan(28);

                      byte[] plaintext = new byte[ciphertext.Length];
                      aes.Decrypt(nonce, ciphertext, tag, plaintext);
                      return plaintext;
                  }
                  finally
                  {
                      CryptographicOperations.ZeroMemory(colKey);
                  }
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
  - `AesGcmValueConverterTests.EncryptAndDecrypt_RoundtripsSuccessfully`: asserts plaintext roundtrips through BCL `AesGcmValueConverter` with HKDF subkey derivation and validates ciphertext layout (`[12B Nonce][16B Tag][Ciphertext]`).
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
      - `private_key_ciphertext` (`BLOB`, encrypted via `AesGcmValueConverter`; immediately wiped to `Array.Empty<byte>()` when `TryConsumeOneTimePreKeyPrivateAsync` sets `is_consumed = 1` to enforce X3DH forward secrecy)
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
      - *Route Index*: `CREATE UNIQUE INDEX ix_vault_ratchet_route ON vault_ratchet_sessions(owner_identity_id, owner_device_id, remote_peer_id, remote_device_id);`
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
      - *Composite PK*: `PRIMARY KEY(recipient_identity_id, sender_identity_id)` (persisted with upsert semantics in `SqlitePendingHandshakeRepository` to replace existing in-flight invitations upon subsequent contact requests from the same sender).
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
  - `SqlitePrivatePreKeyStoreTests.MarkConsumed_WipesPrivateKeyAndUpdatesOpkStatus`: flags one-time prekey as consumed, wipes `private_key_ciphertext` for X3DH forward secrecy, and asserts subsequent queries exclude consumed keys.
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
      - `lease_token` (`TEXT` GUID PortLeaseToken)
      - `allocated_at_utc` (`TEXT` ISO 8601)
      - `lease_expiry_utc` (`TEXT` ISO 8601)
  - **CQRS Read-Side Fast Path**:
    - `SqlOutboxQueryService` projects lightweight status summaries, counts, and retry timings via EF Core `.Select()` projection, strictly omitting the `payload` column to prevent LOH (Large Object Heap) memory churn during UI diagnostic polling.
    - **Delivered Payload Scrubbing**: When `SqliteOutboxRepository.SaveAsync` persists a job with `OutboxStatus.Delivered`, it clears `payload = Array.Empty<byte>()` in SQLite so delivered message wire bytes are not retained during the 7-day diagnostic summary window.
- **Scope & Deliverables**:
  - [ ] Implement Outbox Persistence & CQRS Read Model:
    - `SqliteOutboxRepository` (`Percolator.Application.Delivery.Ports.IOutboxRepository`): write-side enqueue, dequeue batching, retry calculation tracking.
    - `SqlOutboxQueryService` (`Percolator.Application.Delivery.Ports.IOutboxQueryService`): fast-path diagnostic summary projecting without hydrating byte payload buffers.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Persistence/Outbox/`)**:
  - `SqliteOutboxRepositoryTests.EnqueueAndDequeue_TransitionsStateCleanly`: jobs advance cleanly from `Queued` to `InFlight` and `Delivered`.
  - `SqliteOutboxRepositoryTests.GetDuePendingJobsAsync_ReturnsOnlyDuePendingJobs_OrderedByNextAttempt`: seeds `Pending` (due vs. future `NextAttemptAtUtc`), `InFlight`, `Delivered`, and `PausedDormant` jobs; asserts only due `Pending` jobs are returned up to `limit`.
  - `SqliteOutboxRepositoryTests.SaveAsync_WhenDelivered_ScrubsPayloadBytesInStorage`: verifies that transitioning a job to `OutboxStatus.Delivered` scrubs `ops_outbox_jobs.payload` to empty bytes while `SqlOutboxQueryService` still projects `OutboxJobSummaryReadModel`.
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
      - *Retention Index*: `CREATE INDEX ix_relay_prekeys_updated ON relay_prekeys(updated_at_utc);`
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
  - `SqliteRelayPreKeyRepositoryTests.SaveAndConsumeBundle_DequeuesOneTimePreKeyAtomically`: saves `RelayHostedPreKeyBundle` by `(OwnerId, DeviceId)` with multiple OPKs, consumes once and saves, and asserts `AvailableOneTimePreKeyCount` decrements and the consumed OPK is never re-served.
  - `SqliteRelayProfileRepositoryTests.CommitProfile_ValidatesWriteToken`: verifies write token hash check on profile upload.
  - `SqliteRelayLedgerRepositoryTests.SaveAndLoadLedger_PreservesEpochAndActiveRoutingTokens`: saves and reloads `RelayGroupLedger` across epoch mutations, verifying `CurrentEpoch`, `EncryptedRosterBlob`, and `ActiveRoutingTokens` round-trip accurately.
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
  - `ZkgroupCryptographyServiceTests.VerifyGroupPresentation_WrongEpochOrTamperedChallenge_ReturnsFalse`: verifies negative cryptographic boundary—proofs generated for epoch $E$ or challenge $C_1$ are rejected when verified against epoch $E+1$ or challenge $C_2$.
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

      message FetchPreKeyBundleRequest {
        bytes recipient_routing_token = 1;
        bytes delivery_token = 2; // Capability token preventing anonymous OPK starvation
      }

      message FetchPreKeyBundleResponse {
        bytes identity_key = 1;
        bytes signed_prekey = 2;
        bytes prekey_signature = 3;
        uint32 signed_prekey_id = 4;
        optional bytes one_time_prekey = 5;
        optional uint32 one_time_prekey_id = 6;
      }

      service UnauthenticatedDeliveryService {
        rpc DeliverDirect(SealedEnvelopeProto) returns (DeliveryAckProto);
        rpc EnqueueMailbox(SealedEnvelopeProto) returns (EnqueueAckProto);
        rpc FetchPreKeyBundle(FetchPreKeyBundleRequest) returns (FetchPreKeyBundleResponse);
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
        bytes routing_token = 1;
        bytes challenge_hmac = 2; // HMAC-SHA256(drain_token, challenge_nonce)
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
        bytes routing_token = 1;
        bytes authorized_delivery_token = 2;
        bytes drain_token = 3;
      }

      message RegisterMailboxResponse {
        bool success = 1;
      }

      message DrainMailboxRequest {
        bytes routing_token = 1;
        bytes drain_token = 2;
      }

      message DrainMailboxResponse {
        repeated QueuedSealedEnvelopeProto envelopes = 1;
      }

      message OneTimePreKeyProto {
        uint32 key_id = 1;
        bytes public_key = 2;
      }

      message PreKeyBundlePayloadProto {
        bytes owner_identity_id = 1;
        uint32 device_id = 2;
        bytes identity_key = 3;
        uint32 signed_prekey_id = 4;
        bytes signed_prekey = 5;
        bytes prekey_signature = 6;
        repeated OneTimePreKeyProto one_time_prekeys = 7;
        google.protobuf.Timestamp published_at_utc = 8;
      }

      message PublishPreKeyBundleRequest {
        bytes payload_bytes = 1; // Serialized PreKeyBundlePayloadProto wire bytes
        bytes signature = 2;     // Ed25519 signature over payload_bytes
      }

      message PublishPreKeyBundleResponse {
        bool success = 1;
      }

      service AuthenticatedRelayService {
        rpc ConnectMailboxStream(stream MailboxStreamClientMessage) returns (stream MailboxStreamServerMessage);
        rpc RegisterMailbox(RegisterMailboxRequest) returns (RegisterMailboxResponse);
        rpc DrainMailbox(DrainMailboxRequest) returns (DrainMailboxResponse);
        rpc PublishPreKeyBundle(PublishPreKeyBundleRequest) returns (PublishPreKeyBundleResponse);
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
    - **5. `profile.proto` (Profile Metadata & Blob Reference Packaging)**:
      ```protobuf
      syntax = "proto3";
      package percolator.profile.v1;

      message BlobReferenceProto {
        bytes blob_id_sha256 = 1;
        bytes decryption_key = 2;
        string mime_type = 3;
      }

      message ProfileMetadataPayloadProto {
        string display_name = 1;
        string bio = 2;
        optional string status_text = 3;
        optional BlobReferenceProto avatar_blob = 4;
      }
      ```
  - **Explicit Phase 4 Boundary Exclusion**:
    - Application plugin schemas (`chat.proto`, `discovery.proto`, `file_transfer.proto`) are strictly excluded from Phase 3 and deferred to Phase 4 (Chunks 4.1, 4.2, 4.4).
- **Scope & Deliverables**:
  - [ ] Configure `Google.Protobuf` and `Grpc.Tools` in `Percolator.Infrastructure.csproj`.
  - [ ] Author Protobuf schemas in `Percolator.Infrastructure/Protos/`: `unauthenticated_delivery.proto`, `authenticated_relay.proto`, `anonymous_group.proto`, `session.proto`, and `profile.proto`.
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
  - **`ProtobufProfileSerializer` (`Percolator.PluginSdk.Profiles.IProfileMetadataSerializer`)**:
    - Serializes and deserializes `ProfileMetadataPayload` (`DisplayName`, `Bio`, optional `StatusText`, optional `BlobReference`) using `profile.proto` (`ProfileMetadataPayloadProto`, `BlobReferenceProto`).
    - Enforces domain value object boundaries (`BlobId`, `AttachmentKey`, `MimeType`) during deserialization.
- **Scope & Deliverables**:
  - [ ] Implement `ProtobufSessionWirePacker` (`Percolator.Application.Ports.ISessionWirePacker`).
  - [ ] Implement `ProtobufPayloadSerializer` (`Percolator.PluginSdk.IPayloadSerializer`).
  - [ ] Implement `ProtobufProfileSerializer` (`Percolator.PluginSdk.Profiles.IProfileMetadataSerializer`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Packaging/`)**:
  - `ProtobufSessionWirePackerTests.RoundtripDirectSession_PacksAndUnpacksCorrectly`: packs ratchet header and ciphertext into wire bytes and unwraps into typed `InboundDirectEnvelope`.
  - `ProtobufSessionWirePackerTests.GroupSessionPackage_PacksIterationAndSignature`: packages group message iteration, signature, and ciphertext into `GroupSessionPackageProto`.
  - `ProtobufPayloadSerializerTests.GenericPayloadSerialization_RoundtripsCorrectly`: serializes and deserializes generic payloads.
  - `ProtobufProfileSerializerTests.ProfileMetadataAndBlobReference_RoundtripsCorrectly`: serializes and deserializes `ProfileMetadataPayload` with optional `BlobReference` via `profile.proto`.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Packaging`

#### Chunk 3.4: Transport Primitives, Interceptors & Endpoint Validation
- **Technical Specifications & Concrete Mechanics**:
  - **Hexagonal Port Reservation Architecture**:
    - *Scope Note*: Implementing application code is not normally a part of the infrastructure implementation plan, but defining the application port `IPortReservationService` in `Percolator.Application.Ports` has been explicitly approved by the user to enforce strict Hexagonal / Onion architecture boundaries.
    - **Application Port (`Percolator.Application.Ports.IPortReservationService`)**:
      ```csharp
      public readonly record struct PortNumber(int Value);
      public readonly record struct PortLeaseToken(Guid Value);
      public readonly record struct PortRange(PortNumber Start, PortNumber End);
      public sealed record PortLease(PortNumber Port, PortLeaseToken LeaseToken, DateTimeOffset ExpiresAtUtc);

      public interface IPortReservationService
      {
          Task<DomainResult<PortLease>> AcquirePortLeaseAsync(PortRange range, CancellationToken ct = default);
          Task<DomainResult> RenewPortLeaseAsync(PortNumber port, PortLeaseToken leaseToken, CancellationToken ct = default);
          Task<DomainResult> ReleasePortLeaseAsync(PortNumber port, PortLeaseToken leaseToken, CancellationToken ct = default);
          Task<DomainResult> PurgeExpiredLeasesAsync(CancellationToken ct = default);
      }
      ```
    - **Infrastructure Adapter (`Percolator.Infrastructure.Transport.SqlitePortReservationService`)**:
      - Implements `IPortReservationService` coordinating transactional SQLite leases in `ops_reserved_ports` with OS socket probing.
      - 1. Queries `ops_reserved_ports` via `PercolatorDbContext` to filter out currently active leases (`WHERE lease_expiry_utc > UtcNow`).
      - 2. Probes candidate ports sequentially using `TcpListener` with dual-mode IPv6 and exclusive binding (`Stop()` immediately after availability verification so Kestrel can bind the socket without `AddressAlreadyInUse` contention):
         ```csharp
         var listener = new TcpListener(IPAddress.IPv6Any, port);
         listener.Server.DualMode = true;
         if (OperatingSystem.IsWindows())
         {
             listener.Server.ExclusiveAddressUse = true;
         }
         listener.Start();
         listener.Stop();
         ```
      - 3. If `SocketException` (`AddressAlreadyInUse`), advances to next port.
      - 4. Upon successful probe, commits `(port_number, lease_token, allocated_at_utc, lease_expiry_utc)` to `ops_reserved_ports` with a 30-minute renewal heartbeat and returns `DomainResult<PortLease>.Success(...)`.
      - 5. `PurgeExpiredLeasesAsync`: executes `DELETE FROM ops_reserved_ports WHERE lease_expiry_utc <= UtcNow`.
    - **Startup Lease Purging**: During Stage 2 bootstrap initialization (prior to Kestrel listener startup), the coordinator invokes `_portReservationService.PurgeExpiredLeasesAsync()`, preventing port exhaustion following abnormal termination without adding an extra long-running `IHostedService`.
  - **Native In-Band Relay Authentication**:
    - Endpoints on `AuthenticatedRelayService` (`authenticated_relay.proto`) utilize native, cryptographic in-band authentication instead of transport header-signing interceptors:
      - **Capability-Authenticated Mailbox RPCs (`RegisterMailbox`, `DrainMailbox`, `ConnectMailboxStream`)**: Align directly with `RelayMailboxQueue` capability tokens (`DeliveryToken` and secret `DrainToken`), ensuring the relay never correlates a `BlindedRoutingToken` with an `IdentityKey`. `RegisterMailbox` registers `(routing_token, authorized_delivery_token, drain_token)`. `ConnectMailboxStream` issues a single-use CSPRNG `AuthChallengeProto` (`nonce`, `expires_at_utc`) and verifies `AuthResponseProto` (`routing_token`, `challenge_hmac = HMAC-SHA256(drain_token, nonce)`), which is inherently immune to clock drift and replay attacks.
      - **Protobuf-Native Signed PreKey Publication (`PublishPreKeyBundle`)**: Client serializes `PreKeyBundlePayloadProto` (containing `owner_identity_id`, `device_id`, `identity_key`, `signed_prekey`, `prekey_signature`, `one_time_prekeys`, and `published_at_utc`) into `payload_bytes` and signs `payload_bytes` with Ed25519. The relay verifies `signature` directly over raw `payload_bytes` before deserializing, and enforces `published_at_utc > stored.updated_at_utc` to prevent key rollback/replay attacks.
  - **Transport Primitives**:
    - `InMemoryTransportCertificateProvider` (`Percolator.Infrastructure.Transport.ITransportCertificateProvider`): Ephemeral ECDSA P-256 self-signed certificate with loopback SANs generated in memory with zero disk I/O.
    - `CallbackEndpointValidator`: Validates peer endpoints before connecting; blocks loopback (`127.0.0.0/8`, `::1`), RFC 1918 private subnets, IPv4-mapped IPv6, IPv6 Unique Local Addresses (`fc00::/7`), link-local (`169.254.0.0/16`, `fe80::/10`), and unspecified wildcard addresses (`0.0.0.0`, `::`) unless explicit dev flags (`AllowLoopback = true`, `AllowLan = true`) are set. Mitigates DNS rebinding by resolving hostnames once during validation and connecting directly to the validated `IPEndPoint`.
    - `PeerGrpcChannelFactory`: Thread-safe bounded channel cache (capacity: 100 channels) with 5-minute sliding expiration for idle connections, 2-second connect timeout, and clean socket disposal upon eviction.
    - `PrimaryContainerServiceActivator<T>`: Bridges Kestrel gRPC listener container to host DI container (`IServiceProvider`) with scoped lifetime cleanup.
- **Scope & Deliverables**:
  - [ ] Implement `IPortReservationService` (`Percolator.Application.Ports.IPortReservationService`) in `Percolator.Application` (approved application port extension).
  - [ ] Implement `SqlitePortReservationService` (`IPortReservationService`).
  - [ ] Define `ITransportCertificateProvider` (`Percolator.Infrastructure.Transport.ITransportCertificateProvider`) and implement `InMemoryTransportCertificateProvider`.
  - [ ] Implement `CallbackEndpointValidator`.
  - [ ] Implement `PeerGrpcChannelFactory`.
  - [ ] Implement `PrimaryContainerServiceActivator<T>`.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Primitives/`)**:
  - `CallbackEndpointValidatorTests.RejectsPrivateAndLoopbackIps_UnlessDevFlag`: rejects loopback, RFC 1918, IPv4-mapped IPv6, IPv6 ULA, and unspecified addresses by default; verifies direct `IPEndPoint` connection target.
  - `PeerGrpcChannelFactoryTests.IdleChannel_EvictedAfterSlidingExpiration`: verifies channel eviction and disposal after idle period and respects capacity ceiling.
  - `SqlitePortReservationServiceTests.PortContention_BindsNextAvailablePort`: simulates port collision and verifies lease assignment on subsequent free port.
  - `SqlitePortReservationServiceTests.PurgeExpiredLeases_RemovesStaleEntries_PreservesActiveLeases`: verifies stale leases are purged while active leases remain untouched.
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
  - `SealedEnvelopeUnwrapperTests.Unwrap_CorruptedWirePayload_ReturnsDomainFailureWithoutThrowing`: verifies malformed wire bytes return a structured `DomainResult.Failure` instead of throwing uncaught exceptions.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Transport.Dispatching`

#### Chunk 3.6: gRPC Ingress Endpoints & Hosted Relay Workers
- **Technical Specifications & Service Endpoints**:
  - **`UnauthenticatedDeliveryEndpoint` (ASP.NET Core gRPC Service on relays and direct peer listening ports)**:
    - Realizes `UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceBase`.
    - Handles `DeliverDirect`, `EnqueueMailbox`, `FetchPreKeyBundle`, `GetProfile`, `UploadProfile`, `DeleteProfile`, and `DownloadBlindBlob`.
    - **Anonymous Capability-Gated PreKey Fetch**: `FetchPreKeyBundle` requires both `recipient_routing_token` and the recipient's `delivery_token` capability (verified in constant time before calling `RelayHostedPreKeyBundle.ConsumeBundle()`), preventing anonymous One-Time PreKey starvation while keeping the initiator's identity hidden from the relay.
    - **Strict Boundary Framing & Blob Edge Validation**: Interacts strictly with transport layer pipes, Phase 2 repositories (`IRelayProfileRepository`, `IRelayPreKeyRepository`), and `ILocalChunkStore`. In `DownloadBlindBlob`, incoming `blob_id` is validated at the edge to be strictly 32 bytes (`BlobId.FromSha256`), rejecting malformed lengths with `RpcException(new Status(StatusCode.InvalidArgument, ...))` and addressing by lowercase hex (`[0-9a-f]{64}`). Resolution delegates to application port `ILocalChunkStore` (implemented in Phase 4 Chunk 4.3 by `LocalFileChunkStore`) to stream raw content-addressed ciphertext blocks with zero transport filesystem access and zero application-level media decoding.
  - **`AuthenticatedRelayEndpoint` & `AnonymousGroupRelayEndpoint` (ASP.NET Core gRPC Services on relays)**:
    - `AuthenticatedRelayEndpoint` (`AuthenticatedRelayService.AuthenticatedRelayServiceBase`): handles `ConnectMailboxStream`, `RegisterMailbox`, `DrainMailbox`, and `PublishPreKeyBundle` using in-band capability and Protobuf-native signature verification.
    - `AnonymousGroupRelayEndpoint` (`AnonymousGroupRelayService.AnonymousGroupRelayServiceBase`): handles `SubscribeGroupStream`, `DispatchGroupMessage`, `CommitRosterMutation`, and `FetchGroupState` gated by `IZkProofEngine` presentation proofs against `IRelayLedgerRepository`.
  - **Duplex & Streaming Background Workers (`IHostedService`)**:
    - `RelayMailboxStreamWorker`: Duplex streaming connection to home relay (`ConnectMailboxStream`) with automatic reconnection and jittered exponential backoff.
    - `RelayGroupStreamWorker`: Live subscription stream (`SubscribeGroupStream`) with ZK presentation proofs for active group channels.
- **Scope & Deliverables**:
  - [ ] Implement `UnauthenticatedDeliveryEndpoint` (`UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceBase`).
  - [ ] Implement `AuthenticatedRelayEndpoint` (`AuthenticatedRelayService.AuthenticatedRelayServiceBase`).
  - [ ] Implement `AnonymousGroupRelayEndpoint` (`AnonymousGroupRelayService.AnonymousGroupRelayServiceBase`).
  - [ ] Implement `RelayMailboxStreamWorker` (`IHostedService`).
  - [ ] Implement `RelayGroupStreamWorker` (`IHostedService`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Transport/Endpoints/`)**:
  - `UnauthenticatedDeliveryEndpointTests.DeliverDirect_ValidEnvelope_ReturnsSuccessAck`: validates RPC execution and ack generation.
  - `UnauthenticatedDeliveryEndpointTests.UploadProfile_PersistsSlot`: verifies profile slot persistence via `IRelayProfileRepository`.
  - `UnauthenticatedDeliveryEndpointTests.DownloadBlindBlob_MalformedId_ThrowsInvalidArgument`: verifies non-32-byte blob identifiers return `StatusCode.InvalidArgument` at the edge before lookup.
  - `UnauthenticatedDeliveryEndpointTests.FetchPreKeyBundle_InvalidDeliveryToken_RejectsWithoutConsumingOpk`: verifies invalid capability token is rejected before dequeuing a One-Time PreKey.
  - `AuthenticatedRelayEndpointTests.PublishPreKeyBundle_StaleTimestamp_RejectsRollbackReplay`: verifies replayed bundle with older `published_at_utc` is rejected.
  - `AnonymousGroupRelayEndpointTests.CommitRosterMutation_ValidProofAdvancesEpoch_StaleEpochReturnsConflict`: covers positive epoch advancement and negative `EPOCH_CONFLICT` / invalid ZK proof rejection on `AnonymousGroupRelayEndpoint`.
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
      - `encryption_key_ciphertext` (`BLOB`, 32-byte out-of-band attachment AEAD key encrypted at rest via `AesGcmValueConverter`)
      - `base_nonce` (`BLOB` 12 bytes, `BlobReference.BaseNonce`)
      - `storage_mode` (`INTEGER`: `Inline=0`, `RelayBlob=1`, `DirectPeerStream=2`)
      - `relay_endpoint_uri` (`TEXT` nullable)
      - `ciphertext_size_bytes` (`INTEGER`)
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
    - `ChatPayloadEnvelopeProto`: `string type = 1;`, `bytes data = 2;`, `bytes padding = 3;` (maps 1:1 with `Percolator.Apps.Chat.Features.Messaging.ChatPayloadEnvelopeDto`).
    - `ChatMessageProto`: text content, timestamp, and message flags.
    - `ReplyContextProto`: strictly carries `parent_message_id` (16 bytes GUID) and `author_identity_id` (32 bytes); quote snippet text is strictly omitted from the wire (Invariant 6).
    - `MentionProto`, `LinkPreviewProto` (`url`, `title`, `description`, `thumbnail_blob_sha256`, `thumbnail_encryption_key`, `thumbnail_base_nonce`, `thumbnail_storage_mode`, `thumbnail_relay_endpoint_uri`, `thumbnail_ciphertext_size_bytes`, `thumbnail_mime_type`), `MediaAttachmentProto` (includes `blob_sha256`, `encryption_key`, `base_nonce`, `storage_mode`, `relay_endpoint_uri`, `ciphertext_size_bytes`, `file_name`, `file_size_bytes`, `mime_type`), `MessageEditProto`, `MessageTombstoneProto`, `ReactionUpdateProto`, `ReadReceiptProto`, `SystemNoticeProto`, `GroupInvitationProto`, `GroupInvitationResponseProto`.
    - `ProfileCapabilityProto`: `channel_id`, `author_id`, `profile_key_bytes`, `blinded_locator_bytes`, `mailbox_routing_token_bytes`, `revision`, `timestamp_unix_ms`, `author_public_key_bytes`, `source_endpoint`, `source_type` (maps 1:1 with `Percolator.Apps.Chat.Features.Profiles.ProfileCapabilityDto` for `ChatPayloadType.ProfileCapability = 9`).
  - **Application-Layer Stepped Bucket Padding Integration**:
    - Application-layer `ChatService` (`Percolator.Apps.Chat.Features.Messaging.ChatService`) already wraps and pads every outbound chat payload via `SteppedPadding.CreatePaddedEnvelopeBytes` to discrete buckets (`256 B`, `512 B`, `1024 B`, `2048 B`, `4096 B`, and multiples of `4096 B` via `((unpaddedLength + 4095) / 4096) * 4096` for messages up to `ChatMessage.MaxContentLength = 16_000` chars).
    - `ProtobufPayloadSerializer` (from Chunk 3.3) serializes `ChatPayloadEnvelopeDto` (`Type`, `Data`, `Padding`) and inner chat DTOs directly to `chat.proto` binary messages without double-padding or artificially rejecting valid `> 4096 B` multi-block frames.
  - **CQRS Fast-Path Read Models & Background Pruners**:
    - CQRS read model `SqlChatMessageQueryService` projects timeline pages with keyset / offset pagination without hydrating heavy attachment blobs.
    - **Disappearing Message Cryptographic Purge**: When `DisappearingMessageEvictionWorker` sweeps expired messages (every 30s via `ix_chat_expiry`), it queries `chat_attachments.blob_sha256` prior to cascade deletion, invokes `ILocalChunkStore.DeleteBlobAsync(blobId)` to shred any locally cached encrypted media files, and purges corresponding `chat_channel_payloads` rows so expired messages and their attachment keys are permanently unrecoverable on disk.
    - **Unknown Group Message Cache Quota & TTL**: `UnknownGroupMessageCachePruner` aligns with `ChatPayloadHandler.DefaultUnknownGroupCacheTtlMs` (`24 hours`), and `SqlUnknownGroupMessageCacheRepository` enforces a bounded capacity limit (max `500` cached envelopes in `chat_unknown_group_messages`, evicting oldest by `received_at_unix_ms`) to prevent disk exhaustion from random `ChannelId` floods.
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/chat.proto`.
  - [ ] Configure `<ProjectReference>` to `Percolator.Apps.Chat` and `Percolator.Apps.Discovery` in `Percolator.Infrastructure.csproj`.
  - [ ] Extend `ProtobufPayloadSerializer` with deterministic `chat.proto` mappings for `ChatPayloadEnvelopeDto` and all `Percolator.Apps.Chat` wire DTOs.
  - [ ] Implement Chat Persistence & Fast-Path CQRS Query Services:
    - `SqlChatMessageRepository` (`Percolator.Apps.Chat.Features.Messaging.IChatMessageRepository`)
    - `SqlChatMessageQueryService` (`Percolator.Apps.Chat.Features.Messaging.IChatMessageQueryService`)
    - `SqlConversationListQueryService` (`Percolator.Apps.Chat.Features.Messaging.IConversationListQueryService`)
    - `SqlGroupInvitationRepository` (`Percolator.Apps.Chat.Features.GroupInvitations.IGroupInvitationRepository`)
    - `SqlGroupInvitationQueryService` (`Percolator.Apps.Chat.Features.GroupInvitations.IGroupInvitationQueryService`)
    - `SqlUnknownGroupMessageCacheRepository` (`Percolator.Apps.Chat.Ingress.IUnknownGroupMessageCacheRepository`)
  - [ ] Implement Chat Lifecycle Background Pruners:
    - `DisappearingMessageEvictionWorker`
    - `UnknownGroupMessageCachePruner`
    - `AbandonedInvitePruner`
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/Chat/`)**:
  - `ProtobufChatSerializerTests.PaddedEnvelope_PreservesSteppedBucketSizeAndSupports16KCharMessages`: round-trips `SteppedPadding`-wrapped `ChatPayloadEnvelopeDto` across 256–4096 B buckets and 4096 B multiples (up to 16,000-char messages) without double-padding or truncation.
  - `ProtobufChatSerializerTests.ReplyContext_ExcludesQuoteSnippetFromWire`: quote snippet text is strictly absent from wire payload (only parent ID and author ID).
  - `SqlChatMessageRepositoryTests.SaveAndDeleteMessage_EncryptsAttachmentKeyAtRestAndCascadesDeletion`: verifies `chat_attachments.encryption_key_ciphertext` is encrypted in raw SQLite storage and cascade-deleted with the parent message.
  - `SqlChatMessageQueryServiceTests.PagedTimeline_ClampsToLimitAndOffset`: limit/offset paging clamps accurately to boundary without aggregate hydration.
  - `SqlConversationListQueryServiceTests.UnreadBadgeCount_AggregatesPastReadReceipt`: count aggregates messages strictly past read receipt timestamp.
  - `DisappearingMessageEvictionWorkerTests.ExpiredMessages_PurgesRowsChannelPayloadsAndLocalCachedBlobs`: advances virtual clock by 31s with `FakeTimeProvider`; asserts expired messages, `chat_attachments`, `chat_channel_payloads`, and local cached blobs via `ILocalChunkStore` are purged.
  - `SqlUnknownGroupMessageCacheRepositoryTests.CacheEnvelope_Exceeding500Cap_EvictsOldestAndPrunesAt24Hours`: verifies 500-envelope bounded quota and 24-hour TTL pruning.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.Chat`

#### Chunk 4.2: Vertical Slice: Peer Discovery & Rendezvous Engine
- **Technical Specifications & Wire Contracts**:
  - **Wire Contracts (`Percolator.Infrastructure/Protos/discovery.proto`)**:
    - `DiscoveryRegistrationProto`: `bytes peer_id = 1;` (16/32-byte `PublicIdentityId`), `int32 visibility = 2;` (`Hidden = 0`, `Public = 1`, maps to `DiscoveryRegistrationPayload`).
    - `NearbyDiscoveryQueryProto`: `bytes requester_id = 1;`, `int32 requested_limit = 2;` (strictly clamped to range `1–100` via `DiscoveryResultLimit` to prevent scraping/DoS).
    - `NearbyDiscoveryResponseProto`: `repeated bytes discoverable_peer_ids = 1;` (maps to `NearbyDiscoveryResponsePayload`).
    - `BlindedRendezvousPublishProto`: `bytes blinded_locator = 1;` (32 bytes), `bytes encrypted_route = 2;` (29–2076 bytes: `[12B Nonce || Ciphertext || 16B Tag]`, maps to `BlindedRendezvousPublishPayload`).
    - `BlindedRendezvousQueryProto`: `repeated bytes blinded_locators = 1;` (batch query strictly clamped to `MaxQueryLocators = 100`, maps to `BlindedRendezvousQueryPayload`).
    - `BlindedRendezvousMatchProto`: `bytes blinded_locator = 1;`, `bytes encrypted_route = 2;`
    - `BlindedRendezvousResponseProto`: `repeated BlindedRendezvousMatchProto matches = 1;` (maps to `BlindedRendezvousResponsePayload`).
  - **Private Rendezvous Cryptographic Invariants (`BlindedLocator` & `EncryptedRendezvousRoute`)**:
    - Relies on `BlindedLocator.Derive` (`Percolator.Apps.Discovery.Features.PrivateRendezvous`) for 24-hour UTC epoch locator rotation across the 3-epoch clock-skew tolerance window $[E - 1, E, E + 1]$ where $E = \lfloor\text{UnixSeconds} / 86400\rfloor$:
      $$\text{BlindedLocator} = \text{HMAC-SHA256}\bigl(K_{\text{rendezvous}}, \text{"percolator-discovery-rendezvous-v1"} \parallel \text{EpochDay}_{\text{BE64}} \parallel \text{PublicIdentityId}_{16\text{B}}\bigr)$$
    - Route payloads (`EncryptedRendezvousRoute`) are encrypted with AES-256-GCM (`[12B Nonce || Ciphertext || 16B Tag]`) using a key derived via $\text{HKDF-SHA256}(\text{ikm}: K_{\text{rendezvous}}, \text{salt}: \text{EpochDay}_{\text{BE64}}, \text{info}: \text{"percolator-rendezvous-route-key-v1"} \parallel \text{PublisherPeerId}_{16\text{B}})$ and bind `BlindedLocator.Span` (32 bytes) as **Associated Authenticated Data (AAD)** so a relay or third party cannot transplant an encrypted route to another locator.
  - **Decoupled Relay Persistence (`relay_peer_directory` vs. `relay_rendezvous_entries`)**:
    - `relay_peer_directory`:
      - `peer_id` (`BLOB` 16/32 bytes `PublicIdentityId`, PK)
      - `visibility` (`INTEGER`: `Hidden = 0`, `Public = 1`)
      - `has_authenticated_handshake` (`INTEGER` boolean)
      - `has_active_signed_prekey` (`INTEGER` boolean)
      - `registered_at_utc` (`TEXT` ISO 8601)
      - `last_updated_at_utc` (`TEXT` ISO 8601, indexed for 24-hour TTL eviction)
      - *Lease Index*: `CREATE INDEX ix_peer_directory_updated ON relay_peer_directory(last_updated_at_utc);`
    - `relay_rendezvous_entries`:
      - `blinded_locator` (`BLOB` 32 bytes `BlindedLocator`, PK)
      - `encrypted_route` (`BLOB` 29–2076 bytes `EncryptedRendezvousRoute`)
      - `published_at_utc` (`TEXT` ISO 8601, indexed for 48-hour TTL eviction)
      - *Expiry Index*: `CREATE INDEX ix_rendezvous_published ON relay_rendezvous_entries(published_at_utc);`
    - `SqliteRelayPeerDirectory` (`IRelayPeerDirectory`): persists opt-in public directory registrations (`relay_peer_directory`), enforces 24-hour TTL (`DefaultRegistrationTtl`), re-verifies `IPreKeyAvailabilityPort.HasActiveSignedPreKeyAsync` and `IDiscoveryAdmissionGate.Evaluate` both on registration and dynamically during `GetDiscoverablePeersAsync`, and excludes the querying `requesterId` from results.
    - `SqlitePrivateRendezvousService` (`IPrivateRendezvousService`): persists blinded rendezvous locators (`relay_rendezvous_entries`) with 48-hour TTL (`DefaultEntryTtl`), enforces a bounded table capacity (`DefaultMaxEntries = 10_000`), and clamps batch resolution queries in `ResolveRendezvousAsync` to `MaxQueryLocators = 100`.
    - `RelayDiscoveryRegistrationPruner`: scheduled worker purging `relay_peer_directory` rows older than 24h and `relay_rendezvous_entries` rows older than 48h.
    - **Discovery Admission Gating & Anti-Impersonation**:
      - Anti-impersonation is enforced by the authenticated E2EE session ingress layer (`DiscoveryPayloadHandler` verifies `payload.PeerId.Equals(context.SenderIdentityId)` on registration and `query.RequesterId.Equals(context.SenderIdentityId)` on queries before delegating to `IRelayPeerDirectory`). Setting visibility to `Hidden` immediately removes the peer's row from `relay_peer_directory`.
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/discovery.proto`.
  - [ ] Implement `SqliteRelayPeerDirectory` (`Percolator.Apps.Discovery.Features.RelayDirectoryHosting.IRelayPeerDirectory`).
  - [ ] Implement `SqlitePrivateRendezvousService` (`Percolator.Apps.Discovery.Features.PrivateRendezvous.IPrivateRendezvousService`).
  - [ ] Implement `DiscoveryQueryClient` (`Percolator.Apps.Discovery.Ports.INearbyDiscoveryQueryClientPort`).
  - [ ] Implement `PreKeyAvailabilityAdapter` (`Percolator.Apps.Discovery.Ports.IPreKeyAvailabilityPort`).
  - [ ] Implement `PreKeyBundleProviderAdapter` (`Percolator.Application.Ports.IPreKeyBundleProvider`).
  - [ ] Implement `GrpcProfileDeliveryClient` (`Percolator.PluginSdk.Profiles.IProfileDeliveryClient`).
  - [ ] Implement `RelayDiscoveryRegistrationPruner` (`IHostedService`).
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/Discovery/`)**:
  - `DiscoveryQueryClientTests.NearbyQuery_ClampsCountTo100`: verifies query parameters are strictly clamped to max 100 results.
  - `SqlitePrivateRendezvousServiceTests.PublishAndResolve_MatchesBlindedLocatorAndEnforces100QueryCap`: publishes `BlindedLocator` + `EncryptedRendezvousRoute` and resolves across batch query while clamping batch size to 100.
  - `SqliteRelayPeerDirectoryTests.RegisterAndQuery_EnforcesAdmissionMatrixAndDynamicPreKeyCheck`: verifies admission gate rejects missing Signed PreKeys or unauthenticated handshakes, removes peers when visibility changes to `Hidden`, and dynamically excludes peers whose Signed PreKey expires before `GetDiscoverablePeersAsync`.
  - `PreKeyAvailabilityAdapterTests.HasActiveSignedPreKey_ChecksPresenceWithoutConsumingOneTimePreKeys`: verifies discovery prekey availability checks never dequeue or deplete One-Time PreKeys from `RelayHostedPreKeyBundle`.
  - `GrpcProfileDeliveryClientTests.BatchProfileFetch_SinglePeerFailure_DoesNotAbortRemainingPeers`: verifies fault tolerance across multi-peer profile fetches when an individual peer or relay endpoint fails.
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
    - **Resource Starvation & Storage Quotas (`DomainResult` Enforcement)**:
      - `StreamingAesGcmCryptoService`, `RelayBlobClient`, `PeerStreamBlobClient`, and `LocalFileChunkStore` enforce a maximum single-blob size ceiling (default: `100 MB`) while reading/writing streams, and `LocalFileChunkStore` enforces a configurable local cache quota ceiling (default: `5 GB`). Streams exceeding the 100 MB ceiling or 5 GB quota abort immediately, delete incomplete `.tmp` staging files, and return `DomainResult.Failure` (`BLOB_TOO_LARGE` / `BLOB_QUOTA_EXCEEDED`) rather than throwing unhandled exceptions.
  - **Content-Addressed Local Blob Store**:
    - Files stored at `%AppData%/Percolator/Blobs/{sha256Hex}.blob`.
    - Commits use atomic file staging: payload written to `.tmp` extension, flushed to disk, and moved into place via atomic file replace.
  - **Network Blob Transfer Clients**:
    - `RelayBlobClient` (`IRelayBlobClient`): uploads and downloads encrypted blind blobs via `UnauthenticatedDeliveryService` (`UploadBlindBlob` / `DownloadBlindBlob` client/server gRPC streaming) with byte-offset resumption and pre-flight `ciphertext_size_bytes <= 100 MB` validation.
    - `PeerStreamBlobClient` (`IPeerStreamBlobClient`): direct peer-to-peer chunk streaming over ephemeral TLS with strict 100 MB stream read caps.
    - `ChannelRelayResolver` (`IChannelRelayResolver`): resolves designated blob relay endpoints.
    - `RelayBlobRetentionPruner`: background pruner enforcing blob TTL expiration and sweeping oldest-accessed `.blob` files when total storage exceeds the 5 GB local cache watermark.
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
  - `RelayBlobClientTests.StreamUploadAndDownload_RoundtripsChunksAndRejectsOversizedStream`: verifies gRPC `BlindBlobChunkProto` streaming round-trip and asserts streams exceeding 100 MB abort with `DomainResult.Failure`.
  - `LocalFileChunkStoreTests.AtomicCommit_WritesTempFileAndRenames`: validates atomic write to `%AppData%/Percolator/Blobs/{sha256Hex}.blob`.
  - `LocalFileChunkStoreTests.UploadExceedingQuota_ReturnsDomainFailureAndDeletesTempFile`: verifies writes exceeding 100 MB blob size or 5 GB storage quota return `DomainResult.Failure` and clean up `.tmp` files without disk exhaustion.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Plugins.BlobStorage`

#### Chunk 4.4: Vertical Slice: Swarm Multi-Stream File Transfer Engine
- **Technical Specifications, Table Schemas & Binary Framing**:
  - **DDL & Schema Definitions (`ft_*`)**:
    - `ft_transfer_sessions`:
      - `transfer_id` (`TEXT` GUID `TransferSessionId`, PK)
      - `channel_id` (`TEXT` GUID `ChannelId`)
      - `local_identity_id` (`BLOB` 32 bytes `PublicIdentityId`)
      - `initiator_id` (`BLOB` 32 bytes `PublicIdentityId`)
      - `topology_type` (`INTEGER`: `OneToOne=1`, `MultiPeerSwarm=2`)
      - `recipients_json` (`TEXT` serialized list of `PublicIdentityId`)
      - `role` (`INTEGER` `TransferRole`: `Sender=1`, `Receiver=2`)
      - `state` (`INTEGER` `TransferSessionState`: `Offered=1`, `PendingConsent=2`, `Active=3`, `Paused=4`, `Completed=5`, `SeedComplete=6`, `Cancelled=7`, `Rejected=8`, `Expired=9`, `Failed=10`)
      - `root_name` (`TEXT` `RootBundleName`)
      - `total_size_bytes` (`INTEGER` `TotalBundleBytes`)
      - `chunk_size_bytes` (`INTEGER` `ChunkSizeBytes`)
      - `merkle_root` (`BLOB` 32 bytes `MerkleRoot`)
      - `concatenated_leaves` (`BLOB` $N \times 32$ bytes `ChunkHash` leaf array)
      - `selected_files_json` (`TEXT` nullable `SelectedFileIndices`)
      - `target_seed_ratio` (`REAL` `SeedRatio`)
      - `uploaded_size_bytes` (`INTEGER` `TotalBundleBytes`)
      - `local_root_path` (`TEXT` nullable `RootDirectoryPath`)
      - `wan_policy` (`INTEGER` `DirectWanPolicy`: `DisabledByDefault=1`, `ExplicitlyAllowed=2`)
      - `transfer_key_ciphertext` (`BLOB`, 32-byte `TransferKey` encrypted at rest via `AesGcmValueConverter`)
      - `base_nonce` (`BLOB` 12 bytes `BaseNonce`)
      - `stream_auth_token_ciphertext` (`BLOB`, 32-byte `StreamAuthToken` encrypted at rest via `AesGcmValueConverter`)
      - `relay_endpoint_uri` (`TEXT` nullable `RelayPipeEndpointUri`)
      - `relay_pipe_token_ciphertext` (`BLOB` nullable, 32-byte `RelayPipeToken` encrypted at rest via `AesGcmValueConverter`)
      - `direct_candidates_json` (`TEXT` serialized `IReadOnlyList<DirectEndpointCandidate>`)
      - `created_at_utc` (`TEXT` ISO 8601)
      - `completed_at_utc` (`TEXT` ISO 8601 nullable)
    - `ft_manifest_files`:
      - `transfer_id` (`TEXT` GUID, FK to `ft_transfer_sessions(transfer_id)` ON DELETE CASCADE)
      - `file_index` (`INTEGER` `FileIndex`)
      - `relative_path` (`TEXT` `SanitizedRelativePath`)
      - `file_size_bytes` (`INTEGER` `FileSizeBytes`) // Path sanitization: rejects paths with '..', leading slashes, Win32 reserved names, or ADS streams
      - `start_offset` (`INTEGER` `BundleByteOffset`)
      - `last_modified_unix_seconds` (`INTEGER` `FileLastModifiedUtc`)
      - *Composite PK*: `PRIMARY KEY(transfer_id, file_index)`
    - `ft_transfer_bitfields`:
      - `transfer_id` (`TEXT` GUID, PK, FK to `ft_transfer_sessions(transfer_id)` ON DELETE CASCADE)
      - `bitfield_bytes` (`BLOB`)
      - `total_pieces` (`INTEGER`)
      - `verified_pieces` (`INTEGER`)
    - `ft_swarm_endpoints`:
      - `rendezvous_id` (`BLOB` 32 bytes SwarmRendezvousId)
      - `peer_id` (`BLOB` 32 bytes PublicIdentityId)
      - `endpoint` (`TEXT` candidate IP/port or URI)
      - `peer_role` (`INTEGER`: `Leecher=0`, `Seeder=1`)
      - `last_seen_utc` (`TEXT` ISO 8601)
      - *Composite PK*: `PRIMARY KEY(rendezvous_id, peer_id)`
      - *Lease Expiry Index*: `CREATE INDEX ix_swarm_endpoints_expiry ON ft_swarm_endpoints(last_seen_utc);`
    - `ft_filter_policies`:
      - `owner_id` (`BLOB` 32 bytes PublicIdentityId, PK)
      - `blocked_peers_json` (`TEXT` serialized set of PublicIdentityId)
      - `blocked_relays_json` (`TEXT` serialized set of RelayPipeEndpointUri)
      - `updated_at_utc` (`TEXT` ISO 8601)
  - **Swarm Binary Framing Protocol (0x01–0x0C)**:
    - Fixed 5-byte header: `[Opcode: 1 Byte] [Payload Length: 4 Bytes Big-Endian UInt] [Payload: Variable Length]`
    - Maximum payload size: `262,144 bytes` (256 KB); standard piece chunk size: `65,536 bytes` (64 KB).
    - Opcode framing definitions (aligned with `Percolator.Apps.FileTransfer.Features.MultiStreamEngine.StreamFramePayloads` & `ChunkCryptoService`):
      - `0x01` `STREAM_HANDSHAKE` (`HandshakeFrame`): `[AuthToken: 32 B] [SessionId: 16 B GUID BE] [StreamIndex: Int32 BE (4 B)] [SenderPeerId: 16/32 B]`
      - `0x02` `BITFIELD` (`BitfieldFrame`): `[BitfieldBytes: N B]` (1 bit per chunk, bounded to `(TotalChunks + 7) / 8` bytes)
      - `0x03` `HAVE` (`HaveFrame`): `[ChunkIndex: Int32 BE (4 B)]` (validated `< TotalChunks`)
      - `0x04` `REQUEST_CHUNK` (`RequestChunkFrame`): `[ChunkIndex: Int32 BE (4 B)]`
      - `0x05` `CHUNK_DATA` (`ChunkDataFrame`): `[ChunkIndex: Int32 BE (4 B)] [CiphertextWithAuthTag: N + 16 B]`
        - **Per-Chunk AEAD & Merkle Verification**: Each chunk is AES-256-GCM encrypted via `ChunkCryptoService` using `TransferKey` and a deterministic 12-byte chunk nonce (`BaseNonce[0..8] || (BaseNonce[8..12] ^ ChunkIndex_BE32)`). `TransferSessionCoordinator` drops unsolicited chunks (`!ChunkPipelineScheduler.IsChunkInFlight(chunkIndex)`), verifies and decrypts the 16-byte AES-GCM authentication tag first, and then verifies the 32-byte SHA-256 Merkle leaf hash via `DiskChunkAssembler` before writing to disk.
      - `0x06` `CANCEL_CHUNK` (`CancelChunkFrame`): `[ChunkIndex: Int32 BE (4 B)]`
      - `0x07` `CHOKE`: (Zero payload)
      - `0x08` `UNCHOKE`: (Zero payload)
      - `0x09` `INTERESTED`: (Zero payload)
      - `0x0A` `NOT_INTERESTED`: (Zero payload)
      - `0x0B` `KEEP_ALIVE`: (Zero payload)
      - `0x0C` `PEX_PEERS` (`PexPeersFrame`): `[CandidateCount: 1 B] repeated: [PeerIdLength: 1 B] [PeerIdBytes: 16/32 B] [EndpointLength: UInt16 BE] [EndpointUtf8Bytes: M B]` (clamped by `PexRateLimiter` to `MaxCandidatesPerFrame = 16` and `MinPexInterval = 10s`, dropping WAN candidates when `DirectWanPolicy.DisabledByDefault` and validating endpoints via `CallbackEndpointValidator`).
  - **Wire Contracts (`Percolator.Infrastructure/Protos/file_transfer.proto`)**:
    - `FileEntryProto`: `file_index`, `relative_path`, `file_size_bytes`, `start_offset`, `last_modified_unix_seconds`.
    - `TransferManifestProto`: `root_name`, `total_size_bytes`, `chunk_size_bytes`, `merkle_root`, `concatenated_leaves` (concatenated 32-byte leaf array without per-leaf tags), `repeated FileEntryProto files`.
    - `TransferBundleSummaryProto`: `root_name`, `total_size_bytes`, `file_count`, `chunk_size_bytes`, `merkle_root`.
    - `ManifestBlobDescriptorProto` & `ManifestDistributionProto`: `is_inline`, `inline_manifest`, or `external_blob` (`blob_id`, `manifest_key`, `manifest_nonce`, `ciphertext_size_bytes`, `relay_endpoint_uri`).
    - `RelayRendezvousDescriptorProto` & `DirectEndpointCandidateProto`: relay pipe endpoint/token and candidate endpoint/priority descriptors.
    - `FileTransferOfferProto`: `session_id`, `channel_id`, `sender_id`, `topology_type`, `repeated bytes recipient_ids`, `summary`, `manifest_distribution`, `transfer_key`, `base_nonce`, `stream_auth_token`, `relay_rendezvous`, `repeated DirectEndpointCandidateProto direct_candidates`, `expires_at_unix_seconds`.
    - `FileTransferAcceptProto`: `session_id`, `responder_id`, `repeated int32 selected_file_indices`, `repeated DirectEndpointCandidateProto responder_candidates`, `accepted_at_unix_seconds`.
    - `FileTransferCancelProto`: `session_id`, `sender_id`, `reason` (`UserCancelled`, `RejectedByPeer`, `OfferExpired`, `IntegrityFailure`, `StorageExhausted`, `TransportFailure`), `cancelled_at_unix_seconds`.
  - **Portable Desktop File Store Guarantees (`PortableDesktopFileStore : IDesktopFileStorage`)**:
    - Implements all 10 port methods on `Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage` (`PreAllocateFilesAsync`, `WriteChunkAsync`, `ReadChunkAsync`, `WriteBoundarySliceAsync`, `ReadBoundarySliceAsync`, `DeleteBoundaryPartsAsync`, `ScanAndComputeBitfieldAsync`, `FinalizeFileAsync`, `CheckDiskSpaceAsync`, `GetFileMetadataAsync`).
    - Pre-allocation via portable `FileStream.SetLength` for cross-platform file sizing (whereas Windows-specific NTFS `FSCTL_SET_SPARSE` kernel pre-allocation is implemented in `Percolator.Infrastructure.Windows`).
    - Zero-byte files materialize immediately without temporary staging.
    - Active piece staging via `.percolator-part` extension with atomic rename upon full verification.
    - Portable canonical root-containment and path traversal defense: verifies `Path.GetFullPath(resolvedPath)` stays strictly under `Path.GetFullPath(rootDirectory)` and rejects relative traversal segments (`..`), null bytes, and invalid path characters (while Windows-specific Win32 reserved device names and NTFS ADS stream checks are enforced in `Percolator.Infrastructure.Windows`).
  - **Swarm Multiplexing & Rate Limiting**:
    - `TcpMultiStreamPool`: manages 4–8 multiplexed TCP connections with `TCP_NODELAY = true`.
    - `TokenBucketRateLimiter`: smooth bandwidth allocation across active streams.
    - `RelayTransferPipeClient`: 4–8 parallel streams over relay transport with `X-Pipe-Auth` authentication.
    - `RelaySwarmTrackerClient`: tracker client with sliding lease expirations.
    - **Transport Frame Hardening & Swarm Peer Authentication**:
      - `TcpMultiStreamPool` enforces transport-level DoS defenses: (1) validates the 5-byte frame header length (`<= 262,144 B`) before allocating receive buffers to prevent OOM attacks, (2) enforces a 5-second handshake read timeout on newly accepted sockets to prevent Slowloris socket exhaustion, and (3) invokes `CallbackEndpointValidator` inside `ConnectDirectAsync` to block SSRF/loopback/link-local connections on WAN candidates.
      - Application-layer `TransferSessionCoordinator.AuthenticateHandshakeAsync` performs constant-time `CryptographicOperations.FixedTimeEquals` validation on the `0x01 HandshakeFrame` `StreamAuthToken` and verifies `SenderPeerId` against the session's `authorizedPeers` set (`InvalidStreamAuthToken` / `UnauthorizedSwarmPeer`), immediately closing unauthenticated streams before bitfield or chunk exchange.
- **Scope & Deliverables**:
  - [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/file_transfer.proto`.
  - [ ] Configure `<ProjectReference>` to `Percolator.Apps.FileTransfer` and `Percolator.Apps.FileTransfer.Domain` in `Percolator.Infrastructure.csproj`.
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
    - `TransferOfferReaperWorker` (`Microsoft.Extensions.Hosting.BackgroundService` scheduled worker invoking `ITransferOfferReaperService`)
    - `SwarmEndpointRetentionPruner` (`IHostedService`)
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Plugins/FileTransfer/`)**:
  - `TcpMultiStreamPoolTests.FrameSerialization_All12Frames_RoundtripsSuccessfully`: serializes and deserializes all 12 frames (0x01–0x0C).
  - `PortableDesktopFileStoreTests.PathValidation_RejectsDirectoryTraversalAndRootEscape`: rejects `..` traversal and paths escaping the target root directory without touching disk.
  - `PortableDesktopFileStoreTests.ZeroByteFile_MaterializesImmediately`: creates zero-byte file immediately without `.percolator-part`.
  - `PortableDesktopFileStoreTests.BitfieldScanner_MatchesMerkleLeaves`: validates existing chunks against Merkle leaf hashes.
  - `TransferOfferReaperWorkerTests.StaleOffers_OlderThan24h_Reaped`: advances virtual clock by 25h with `FakeTimeProvider`; asserts stale offers marked cancelled.
  - `TcpMultiStreamPoolTests.StreamHandshake_InvalidAuthToken_TerminatesConnection`: asserts unauthenticated swarm peer connections are closed before bitfield exchange.
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

          await context.OutboxJobs
              .Where(j => expiredIds.Contains(j.Id))
              .ExecuteDeleteAsync(ct);
          
          if (expiredIds.Count < 500) break;
      }
      ```
  - **Pruning Worker Implementations**:
    - `OutboxRetentionPruner` (`IHostedService`):
      - Sweeps delivered outbox jobs (`ops_outbox_jobs`, whose `payload` bytes were already scrubbed upon delivery) older than 7 days (`TimeSpan.FromDays(7)`). Preserves pending, in-flight, or failed jobs for inspection/retry via `IOutboxQueryService`.
      - Scheduled interval: 24 hours (or on virtual clock tick via `TimeProvider`).
    - `RelayPreKeyDirectoryPruner` (`IHostedService`):
      - Sweeps unrefreshed relay prekey bundle records from `relay_prekeys` where `updated_at_utc <= cutoffTime` (older than 30 days, utilizing `ix_relay_prekeys_updated`), while consumed One-Time PreKeys are removed immediately upon `RelayHostedPreKeyBundle.ConsumeBundle()` persistence.
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
    - *Stage 2 (Authenticated Host - `IHost`)*: Built post-unlock via `Stage2HostFactory`. Hosts `IMasterKeyHandle` (scoped to session in pinned unmanaged memory), `SqlCipherPragmaInterceptor`, `PercolatorDbContext` (scoped/pooled), all 61 infrastructure ports, and `IHostedService` background workers.
    - *Deterministic Session Teardown Ordering*: `AuthenticatedSession.DisposeAsync()` and `SqlCipherDatabaseUnlockCoordinator.LockAsync()` enforce the strict teardown sequence: (1) `await host.StopAsync(ct)`, (2) `await host.DisposeAsync()`, (3) `SqliteConnection.ClearAllPools()`, and (4) `masterKeyHandle.Dispose()`.
  - **Service Lifetime Registration Topology**:
    - **Singletons**:
      - `IMasterKeyHandle` (instance passed from unlock coordinator)
      - `IDbContextFactory<PercolatorDbContext>` & `PercolatorDbContextOptions`
      - Core Cryptography & Time: `ICryptoEngine`, `IZkProofEngine`, `IDateTimeProvider`
      - Networking & Stream Management: `StreamRegistry`, `ITransferStreamPoolFactory` (`TcpMultiStreamPoolFactory`), `TokenBucketRateLimiter`, `LocalFileChunkStore`, `ITransportCertificateProvider` (`InMemoryTransportCertificateProvider`)
      - Protocol Serializers: `ProtobufSessionWirePacker`, `ProtobufPayloadSerializer`, `ProtobufProfileSerializer`
    - **Scoped**:
      - `PercolatorDbContext` (pooled context per scope)
      - All Repositories & CQRS Query Services (Outbox, Contacts, Ratchet, Channels, Relay prekeys/profiles/ledger, Chat, File Transfer)
      - Workflow & Dispatching: `SqlitePortReservationService`, `TransportDispatcher`, `IngressFilterService`, `SealedEnvelopeUnwrapper`, `DiscoveryQueryClient`, `StreamingAesGcmCryptoService`, `RelayBlobClient`, `PeerStreamBlobClient`, `ChannelRelayResolver`, `RelayTransferPipeClient`, `RelaySwarmTrackerClient`
    - **Hosted Services (`services.AddHostedService<T>()`)**:
      - All 12 background streaming, pruning, syncing, and lifecycle workers across the application:
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
        11. `RelayMailboxStreamWorker` (Chunk 3.6)
        12. `RelayGroupStreamWorker` (Chunk 3.6)
  - **Platform Separation Boundary & Windows Extensibility Hook**:
    - `Percolator.Infrastructure` (`net10.0`) remains 100% OS-agnostic and registers portable baseline implementations: `PortableDesktopFileStore` (`IDesktopFileStorage`) and `InMemoryTransportCertificateProvider` (`ITransportCertificateProvider`).
    - Windows-specific adapter specifications, P/Invoke / SChannel mechanics, and tests are defined exclusively in [`Percolator.Infrastructure.Windows/implementation-plan.md`](../Percolator.Infrastructure.Windows/implementation-plan.md), which overrides those two registrations via `AddWindowsInfrastructureOverrides(this IServiceCollection services)`.
  - **Full Container Verification Guarantee**:
    - `Stage2HostFactory` builds `IHost` with service provider validation (`ValidateScopes = true`, `ValidateOnBuild = true`), guaranteeing zero captive dependencies, zero missing registrations, and zero circular dependencies.
- **Scope & Deliverables**:
  - [ ] Implement `Stage2HostFactory`:
    - Extends `BaselineStage2HostFactory` to wire the full suite of operational adapters, repositories, query services, gRPC channels, and background pruners across all feature slices.
  - [ ] Implement `InfrastructureServiceCollectionExtensions.AddAuthenticatedInfrastructure(IServiceCollection, IConfiguration, IMasterKeyHandle)`:
    - Registers all 61 infrastructure ports into the Stage 2 container with correct lifetime scopes.
    - Registers all 12 background `IHostedService` workers.
- **Embedded Unit Tests (`Percolator.Infrastructure.Tests/Composition/`)**:
  - `DependencyInjectionTests.All61Ports_ResolveFromContainerWithoutCycles`: resolves every planned infrastructure port from DI container; asserts non-null without circular dependency or missing registration.
  - `DependencyInjectionTests.NoCaptiveDependencies_ScopedServicesNeverInjectedIntoSingletons`: enforces container scope validation (`ValidateScopes = true`); verifies no scoped DbContext or repository is injected into singleton services or pruners.
  - `DependencyInjectionTests.All12HostedServices_RegisteredAndStartable`: resolves `IEnumerable<IHostedService>` and asserts exact count of 12 registered background workers.
- **Verification Gate**:
  - `dotnet test Percolator.Infrastructure.Tests --filter FullyQualifiedName~Composition`

### Deferred Scope & Future Hardening (Post-Phase 5 Backlog)

> [!NOTE] Deferred Operational Items to Revisit Post-Phase 5
> - **`ops_inbound_queue` Payload & Retention Lifecycle**: Revisit indexing on `processed_at_utc` and scheduled background sweeping for processed inbound envelopes to prevent unbounded table growth.
> - **`vault_pending_handshakes` Eviction & Retention**: Revisit explicit `expires_at_utc` TTL timestamps and automated pruning for stale unapproved contact invitations.

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
| 44 | `IPreKeyAvailabilityPort` | `Percolator.Apps.Discovery` | `PreKeyAvailabilityAdapter` | Chunk 4.2 |
| 45 | `IPreKeyBundleProvider` | `Percolator.Application` | `PreKeyBundleProviderAdapter` | Chunk 4.2 |
| 46 | `IProfileDeliveryClient` | `Percolator.PluginSdk` | `GrpcProfileDeliveryClient` | Chunk 4.2 |
| 47 | `IBlobCryptoService` | `Percolator.Application` | `StreamingAesGcmCryptoService` | Chunk 4.3 |
| 48 | `IRelayBlobClient` | `Percolator.Application` | `RelayBlobClient` | Chunk 4.3 |
| 49 | `IPeerStreamBlobClient` | `Percolator.Application` | `PeerStreamBlobClient` | Chunk 4.3 |
| 50 | `ILocalChunkStore` | `Percolator.Application` | `LocalFileChunkStore` | Chunk 4.3 |
| 51 | `IChannelRelayResolver` | `Percolator.Application` | `ChannelRelayResolver` | Chunk 4.3 |
| 52 | `ITransferStreamPoolFactory` | `Percolator.Apps.FileTransfer` | `TcpMultiStreamPoolFactory` | Chunk 4.4 |
| 53 | `INetworkEndpointDiscoveryPort` | `Percolator.Apps.FileTransfer` | `NetworkInterfaceEndpointDiscoveryPort` | Chunk 4.4 |
| 54 | `IDesktopFileStorage` | `Percolator.Apps.FileTransfer` | `PortableDesktopFileStore` | Chunk 4.4 |
| 55 | `ITransferRateLimiter` | `Percolator.Apps.FileTransfer` | `TokenBucketRateLimiter` | Chunk 4.4 |
| 56 | `IDistributedTrackerPort` | `Percolator.Apps.FileTransfer` | `RelaySwarmTrackerClient` | Chunk 4.4 |
| 57 | `ITransportCertificateProvider` | `Percolator.Infrastructure.Transport` | `InMemoryTransportCertificateProvider` | Chunk 3.4 |
| 58 | `IIdentityProfileRepository` | `Percolator.Domain` | `SqliteIdentityProfileRepository` | Chunk 2.2 |
| 59 | `IPortReservationService` | `Percolator.Application` | `SqlitePortReservationService` | Chunk 3.4 |
| 60 | `IPrivateRendezvousService` | `Percolator.Apps.Discovery` | `SqlitePrivateRendezvousService` | Chunk 4.2 |
| 61 | `IProfileMetadataSerializer` | `Percolator.PluginSdk` | `ProtobufProfileSerializer` | Chunk 3.3 |
