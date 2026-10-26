# Percolator Database Encryption & Authenticated Session Lifecycle Plan

## Refinement Status Gate
- [ ] Lens 1: Scope & Feature Slice Framing (Status: In Review)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: Pending)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Pending)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Pending)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Pending)
- [ ] Implementation Gate: LOCKED (Requires Approval of Lenses 1-5)

---

## 1. Scope & Architectural Boundaries

### 1.1 In-Scope Capabilities
- **Single Unified Database (`percolator.db`)**: AES-256-CBC encryption via SQLCipher in Write-Ahead Logging (WAL) mode for all application data (identities, prekeys, ratchets, channels, messages, transfers, outbox).
- **Two-Stage Container Architecture (Option 2)**:
  - *Stage 1 (Bootstrap Container)*: Pre-unlock environment containing zero SQLite DbContexts, zero repositories, and zero background worker instances. Manages presentation bootstrap, logging, crypto primitives, time provider, and database unlock coordination.
  - *Stage 2 (Authenticated Host - `IHost`)*: Post-unlock environment hosting `PercolatorDbContext`, connection pools, all 57 domain repositories/query services, and `IHostedService` background workers.
- **Protobuf-Serialized Plausibly Deniable Header (`percolator.header`)**:
  - Binary header format using Protocol Buffers (`DatabaseHeader.proto`).
  - Contains an Argon2id salt and two fixed-size cryptographic slots: **Master Slot** and **Duress Slot**.
  - **Plausible Deniability Invariant**: When no duress password is set, the Duress Slot is filled with cryptographically secure random bytes of identical length. To forensic disk inspection, the header format and byte distribution are 100% indistinguishable regardless of whether a duress password is configured.
- **Optional Duress Passphrase & Decoy State Transition**:
  - Users can optionally establish a duress passphrase (cannot be empty or whitespace) during setup or update it later in authenticated Settings.
  - When the duress passphrase is typed at `UnlockWindow`, the system triggers an immediate **Crypto-Shredding Pipeline** and smoothly transitions the UI to a **Decoy State (Fresh App)**, appearing as a clean, newly installed application without error popups or crash signatures.
- **Argon2id Key Derivation**: Derive 256-bit keys from passphrases using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$) salted with the 16-byte random salt from `percolator.header`.
- **Canary Validation**: Verify the derived master key on an isolated probe connection using `SELECT count(*) FROM sqlite_master;` before initializing EF Core or connection pools.
- **Dynamic Post-Canary Schema Migrations**: Execute EF Core / SQLite migrations (`Database.MigrateAsync()`) immediately post-canary prior to resolving application repositories.
- **Pooled Connection Key Interception**: Apply `PRAGMA key = "x'<hex_key>'";` and WAL pragmas transparently to every pooled SQLite connection via a `DbConnectionInterceptor`.
- **Explicit Unlock for Blank Passwords**: Support empty master password (`""`) with mandatory first-launch UI warning confirmation, and require explicit interaction ("Unlock" click) on every subsequent launch.
- **Orderly In-Session Relock & Memory Scrubbing**: Provide graceful teardown via `LockAsync`: stop all `IHostedService` instances, flush SQLite pools (`SqliteConnection.ClearAllPools()`), zeroize raw key bytes via `CryptographicOperations.ZeroMemory`, dispose `AuthenticatedHost`, and return UI to `UnlockWindow`.

### 1.2 Explicit Non-Goals
- **No Disk Indicators of Duress Configuration**: No separate `duress.hash` or marker files on disk. The header schema and file size remain completely invariant.
- **No Multi-Database Partitioning**: All domain aggregates share `percolator.db` to eliminate redundant KDF overhead and guarantee atomic schema migrations.
- **No Master Password Persistence**: The master passphrase is never cached on disk, DPAPI, or OS keychain without explicit user unlock.
- **No Repository Resolution in Bootstrap Container**: No repository or DbContext may be registered or resolved in the root DI container.

---

## 2. Vertical Feature Slice Architecture

The database encryption and session lifecycle subsystem is organized into cohesive vertical slices:

```
Percolator.Infrastructure/
└── Features/
    └── DatabaseLock/
        ├── Contracts/                     # Subsystem ports
        │   ├── IDatabaseUnlockCoordinator.cs
        │   ├── IAuthenticatedSession.cs
        │   ├── IMasterKeyProtector.cs
        │   └── IDuressWipeHandler.cs
        ├── ValueObjects/                  # Strongly typed domain concepts
        │   ├── MasterPassword.cs
        │   ├── DuressPassword.cs
        │   ├── MasterKeyPragma.cs
        │   ├── UnlockOutcome.cs
        │   └── DatabaseSetupStatus.cs
        ├── KeyDerivation/                 # Argon2id derivation & slot envelopes
        │   ├── IHeaderStorage.cs
        │   ├── ProtobufHeaderStorage.cs
        │   └── Argon2idKeyDeriver.cs
        ├── Duress/                        # Shredding & decoy orchestrator
        │   ├── DuressWipeHandler.cs
        │   └── DecoyStateFactory.cs
        ├── Persistence/                   # SQLCipher EF Core integration
        │   ├── PercolatorDbContext.cs
        │   ├── SqlCipherPragmaInterceptor.cs
        │   └── SqliteConnectionFactory.cs
        ├── Coordination/                  # Workflow orchestrator & session lifecycle
        │   ├── SqlCipherDatabaseUnlockCoordinator.cs
        │   └── AuthenticatedSession.cs
        └── Protos/                        # Protobuf header contract
            └── DatabaseHeader.proto
```

### 2.1 Protobuf Header Contract (`Protos/DatabaseHeader.proto`)

```protobuf
syntax = "proto3";
package percolator.infrastructure;

message DatabaseHeader {
  int32 version = 1;
  bytes salt = 2;               // 16-byte random salt for Argon2id
  bytes master_slot_nonce = 3;  // 12-byte ChaCha20-Poly1305 nonce
  bytes master_slot_payload = 4;// Encrypted 32-byte master key + 16-byte Poly1305 tag
  bytes duress_slot_nonce = 5;  // 12-byte nonce (or random noise)
  bytes duress_slot_payload = 6;// Encrypted 32-byte sentinel + tag (or dummy random noise)
}
```

* **Slot Payload Sizing**: Both `master_slot_payload` and `duress_slot_payload` are exactly 48 bytes (32-byte plaintext + 16-byte Poly1305 auth tag).
* **Dummy Invariant**: If no duress password is configured, `duress_slot_nonce` (12 bytes) and `duress_slot_payload` (48 bytes) are filled with `RandomNumberGenerator.GetBytes(60)`. An adversary reading the Protobuf stream cannot distinguish active duress protection from dummy noise.

### 2.2 Domain Value Objects (`Features/DatabaseLock/ValueObjects/`)

* **`MasterPassword`**: Encapsulates user passphrase (empty string permitted). Provides zeroized buffer extraction.
* **`DuressPassword`**: Encapsulates optional duress passphrase. Requires non-empty, non-whitespace string upon construction:
  ```csharp
  public sealed class DuressPassword
  {
      public string Value { get; }
      public static Result<DuressPassword> Create(string? input);
  }
  ```
* **`UnlockOutcome`**: Explicit discriminated outcome of an unlock attempt:
  ```csharp
  public enum UnlockOutcome
  {
      Success = 0,
      DuressDecoyActivated = 1,
      InvalidPassphrase = 2
  }
  ```
* **`DatabaseSetupStatus`**:
  ```csharp
  public enum DatabaseSetupStatus
  {
      Uninitialized = 0,
      Locked = 1,
      Unlocked = 2
  }
  ```

### 2.3 Subsystem Ports (`Features/DatabaseLock/Contracts/`)

#### `IDatabaseUnlockCoordinator`
```csharp
namespace Percolator.Application.Ports;

public interface IDatabaseUnlockCoordinator
{
    DatabaseSetupStatus Status { get; }

    /// <summary>
    /// Checks disk for existing header and database files to determine initial setup state.
    /// </summary>
    Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Initializes new database with master password and optional duress password.
    /// Generates salt, derives master key, encrypts slots, verifies canary, runs initial migrations.
    /// </summary>
    Task<Result<IAuthenticatedSession>> InitializeMasterPasswordAsync(
        string masterPassword, 
        string? duressPassword = null, 
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates candidate password against master slot and duress slot.
    /// If master matches: runs migrations and returns active session.
    /// If duress matches: executes crypto-shredding and returns duress decoy signal.
    /// </summary>
    Task<Result<UnlockResult>> UnlockAsync(string candidatePassword, CancellationToken ct = default);

    /// <summary>
    /// Updates or clears duress password within an active authenticated session.
    /// </summary>
    Task<Result> ConfigureDuressPasswordAsync(string? duressPassword, CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops workers, flushes connection pools, zeroizes master key, disposes AuthenticatedHost.
    /// </summary>
    Task LockAsync();
}
```

#### `IDuressWipeHandler`
```csharp
namespace Percolator.Application.Ports;

public interface IDuressWipeHandler
{
    /// <summary>
    /// Executes multi-phase cryptographic shredding:
    /// Overwrites percolator.header with random bytes and flushes.
    /// Overwrites and deletes percolator.db, percolator.db-wal, percolator.db-shm.
    /// Purges cached avatars, local attachments, and temporary staging stores.
    /// </summary>
    Task ExecuteWipeAsync(CancellationToken ct = default);
}
```

#### `IMasterKeyProtector`
```csharp
namespace Percolator.Application.Ports;

public interface IMasterKeyProtector : IDisposable
{
    MasterKeyPragma GetPragmaLiteral();
    void WithKeySpan(Action<ReadOnlySpan<byte>> action);
    T WithKeySpan<T>(Func<ReadOnlySpan<byte>, T> func);
}
```

---

## 3. Workflow Decomposition & Orchestration

```
[UnlockAsync Workflow]
  │
  ├─ 1. Read DatabaseHeader from percolator.header
  ├─ 2. Derive Candidate Key Kcand via Argon2id(candidatePassword, header.Salt)
  │
  ├─ 3. Attempt Decryption of Master Slot:
  │     ChaCha20Poly1305.Decrypt(MasterSlotNonce, MasterSlotPayload, Kcand)
  │     ├─ Success -> Master Key recovered!
  │     │             ├─ Probe Canary: PRAGMA key = "x'<MasterKey>'"; SELECT count(*) FROM sqlite_master;
  │     │             ├─ Build AuthenticatedHost & register EphemeralMasterKeyProtector
  │     │             ├─ Run EF Core Migrations (Database.MigrateAsync)
  │     │             └─ Return UnlockResult.AsSuccess(session)
  │     └─ Auth Failed -> Proceed to Duress Check
  │
  ├─ 4. Attempt Decryption of Duress Slot:
  │     ChaCha20Poly1305.Decrypt(DuressSlotNonce, DuressSlotPayload, Kcand)
  │     ├─ Success & Payload == "PERCOLATOR_DURESS_SENTINEL_OK" ->
  │     │     ├─ Trigger IDuressWipeHandler.ExecuteWipeAsync()
  │     │     │    ├─ Overwrite percolator.header with 512 random bytes & flush
  │     │     │    ├─ Overwrite percolator.db/wal/shm with random bytes & delete
  │     │     │    └─ Wipe local storage directories
  │     │     ├─ Status = DatabaseSetupStatus.Uninitialized
  │     │     └─ Return UnlockResult.AsDuressDecoy()
  │     └─ Auth Failed -> Return Result.Failure("Invalid passphrase")
```

### 3.1 Post-Duress Decoy State UI Handoff
When `UnlockResult.AsDuressDecoy()` is returned:
1. `UnlockViewModel` does **not** display any alert or error message.
2. The coordinator informs `IWindowHandoffCoordinator`.
3. The UI smoothly transitions into `IdentityGateWindow` / First-Time Setup mode as a clean, blank slate.
4. The adversary observes an empty, unconfigured installation.

### 3.2 Duress Password Configuration within Authenticated Session
1. User navigates to Settings $\rightarrow$ Security $\rightarrow$ *Emergency Duress Passphrase*.
2. User provides new duress password (or leaves blank to clear).
3. `ConfigureDuressPasswordAsync`:
   - Reads existing `DatabaseHeader`.
   - If setting password: derives duress key via Argon2id, encrypts sentinel, and writes updated `DatabaseHeader`.
   - If clearing password: generates 60 random bytes into `duress_slot_nonce` + `duress_slot_payload` and writes header.
   - Preserves plausible deniability invariant at all times.

---

## 4. Threat Model & Privacy Invariants

- **Plausible Deniability**:
  - The existence of a duress password is cryptographically unprovable from forensic disk analysis. Dummy slots and active slots are identical in structure, length, and entropy.
- **Crypto-Shredding Guarantee**:
  - Overwriting `percolator.header` renders all remaining physical NAND flash remnants of `percolator.db` completely unrecoverable, even if raw SSD controller blocks are dumped via chip-off analysis.
- **Coercion Safety**:
  - Entering the duress password produces zero anomalous alerts or crash stack traces; the application appears as an empty first-time run.
- **Memory Security**:
  - Passphrase buffers and intermediate keys are scrubbed with `CryptographicOperations.ZeroMemory` immediately upon completion of derivation/decryption.

---

## 5. High-Value Unit Testing Matrix

| Test Scenario | Input / State | Expected Observable Behavior | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **First-Time Setup (With Duress)** | Master password + Duress password | Creates `DatabaseHeader` with valid master slot and encrypted duress slot; returns session | Asserts result success; verifies header file exists |
| **First-Time Setup (Without Duress)** | Master password only | Creates `DatabaseHeader` with valid master slot and dummy random duress slot; returns session | Asserts dummy slot length == 48 bytes |
| **Normal Unlock** | Valid master password | Unlocks master slot, canary passes, migrations execute, returns `UnlockOutcome.Success` | Asserts successful session creation |
| **Duress Unlock Triggered** | Valid duress password | Decrypts duress slot, executes `IDuressWipeHandler`, deletes database/header files, returns `UnlockOutcome.DuressDecoyActivated` | Verifies disk files are deleted and status is `Uninitialized` |
| **Invalid Passphrase Attempt** | Incorrect password | Fails both master and duress slots, returns `Result.Failure("Invalid passphrase")`, zero wipe occurs | Asserts failure result; verifies database files remain intact |
| **Duress Update in Session** | Authenticated session sets new duress key | Re-encrypts duress slot in `DatabaseHeader`; subsequent duress unlock succeeds with new key | Tests end-to-end configuration transition |
| **Duress Clear in Session** | Authenticated session clears duress key | Overwrites duress slot with random noise; prior duress password no longer triggers wipe | Verifies former duress password returns invalid passphrase |
| **Crypto-Shredding Resilience** | Invoking `ExecuteWipeAsync` | Overwrites files with random bytes, deletes files, purges cache directory | Verifies target files are absent post-execution |

---

## 6. Cross-Layer Infrastructure Alignment

- **Required NuGet Packages in `Percolator.Infrastructure.csproj`**:
  - `Microsoft.EntityFrameworkCore.Sqlite.Core` (EF Core SQLite)
  - `SQLitePCLRaw.bundle_e_sqlcipher` (Native SQLCipher bundle)
  - `Konscious.Security.Cryptography.Argon2` (Argon2id key derivation)
  - `Google.Protobuf` (Existing - serializes `DatabaseHeader`)
- **Bootstrap Initialization Call**:
  - `SQLitePCL.Batteries_V2.Init()` executed in `AddBootstrapInfrastructure()`.
- **Database File Layout**:
  - `%LocalAppData%/Percolator/percolator.header` (Protobuf header containing salt + slots)
  - `%LocalAppData%/Percolator/percolator.db` (Encrypted database)
  - `%LocalAppData%/Percolator/percolator.db-wal` (WAL journal)
  - `%LocalAppData%/Percolator/percolator.db-shm` (Shared memory index)
- **Adapters Registered**:
  - `IDatabaseUnlockCoordinator` $\rightarrow$ `SqlCipherDatabaseUnlockCoordinator` (Bootstrap)
  - `IDuressWipeHandler` $\rightarrow$ `DuressWipeHandler` (Bootstrap)
  - `IHeaderStorage` $\rightarrow$ `ProtobufHeaderStorage` (Bootstrap)
  - `IMasterKeyProtector` $\rightarrow$ `EphemeralMasterKeyProtector` (Authenticated)
  - `DbConnectionInterceptor` $\rightarrow$ `SqlCipherPragmaInterceptor` (Authenticated)
  - `IAuthenticatedSession` $\rightarrow$ `AuthenticatedSession` (Bootstrap & Authenticated)
