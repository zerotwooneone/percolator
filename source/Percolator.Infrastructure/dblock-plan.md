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
- **Single Unified Database (`percolator.db`)**: Enforce AES-256-CBC encryption via SQLCipher in Write-Ahead Logging (WAL) mode for all application data (identities, prekeys, ratchets, channels, messages, transfers, outbox envelopes).
- **Two-Stage Container Architecture (Option 2)**:
  - *Stage 1 (Bootstrap Container)*: Pre-unlock environment containing zero SQLite DbContexts, zero repositories, and zero background worker instances. Manages presentation bootstrap, logging, crypto primitives, time provider, and database unlock coordination.
  - *Stage 2 (Authenticated Host - `IHost`)*: Post-unlock environment hosting `PercolatorDbContext`, connection pools, all 57 domain repositories/query services, and `IHostedService` background workers.
- **Argon2id Key Derivation**: Derive a 256-bit raw encryption key from user input using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$) salted with a 16-byte random binary salt stored in `percolator.salt`.
- **Canary Validation**: Verify the derived key on a dedicated probe connection using `SELECT count(*) FROM sqlite_master;` before initializing EF Core or connection pools.
- **Dynamic Post-Canary Schema Migrations**: Execute EF Core / SQLite migrations (`Database.MigrateAsync()`) immediately following canary verification prior to resolving application repositories.
- **Pooled Connection Key Interception**: Apply `PRAGMA key = "x'<hex_key>'";` and WAL pragmas transparently to every pooled SQLite connection via a `DbConnectionInterceptor`.
- **Explicit Unlock for Blank Passwords**: Support empty password (`""`) with mandatory first-launch UI warning confirmation, and require explicit interaction ("Unlock" click) on every subsequent launch.
- **Orderly In-Session Relock & Memory Scrubbing**: Provide graceful teardown via `LockAsync`: stop all `IHostedService` instances, flush SQLite pools (`SqliteConnection.ClearAllPools()`), zeroize raw key bytes in memory via `CryptographicOperations.ZeroMemory`, dispose `AuthenticatedHost`, and return UI to `UnlockWindow`.

### 1.2 Explicit Non-Goals
- **No Multi-Database Partitioning**: Partitioned SQLite files (`ops.db`, `vault.db`, `chat.db`, `transfers.db`) are explicitly rejected. All domain aggregates share `percolator.db` to eliminate redundant KDF overhead and guarantee atomic schema migrations.
- **No Automatic Master Password Caching Across Launches**: The master passphrase is never stored on disk, DPAPI, or OS keychain without explicit user unlock. Every launch requires explicit unlock.
- **No In-Memory Key Persistence Across Locks**: When the session locks, the 256-bit key is scrubbed immediately; no cached key material remains in memory.
- **No Repository Resolution in Bootstrap Container**: No repository or DbContext may be registered or resolved in the root DI container.

---

## 2. Vertical Feature Slice Architecture

The database encryption and session lifecycle subsystem is organized into cohesive vertical slices:

```
Percolator.Infrastructure/
└── Features/
    └── DatabaseLock/
        ├── Contracts/                     # Port interfaces
        │   ├── IDatabaseUnlockCoordinator.cs
        │   ├── IAuthenticatedSession.cs
        │   └── IMasterKeyProtector.cs
        ├── ValueObjects/                  # Strongly typed domain concepts
        │   ├── MasterPassword.cs
        │   ├── MasterKeyPragma.cs
        │   └── DatabaseSetupStatus.cs
        ├── KeyDerivation/                 # Argon2id derivation & salt management
        │   ├── ISaltStorage.cs
        │   ├── FileSaltStorage.cs
        │   └── Argon2idKeyDeriver.cs
        ├── Persistence/                   # SQLCipher EF Core integration
        │   ├── PercolatorDbContext.cs
        │   ├── SqlCipherPragmaInterceptor.cs
        │   └── SqliteConnectionFactory.cs
        ├── Coordination/                  # Workflow orchestrator & session lifecycle
        │   ├── SqlCipherDatabaseUnlockCoordinator.cs
        │   └── AuthenticatedSession.cs
        └── Diagnostics/                   # Memory scrubbing & lifecycle metrics
            └── EphemeralMasterKeyProtector.cs
```

### 2.1 Domain Value Objects (`Features/DatabaseLock/ValueObjects/`)

* **`MasterPassword`**: Encapsulates the user-entered passphrase string. Provides methods to extract UTF-8 bytes into a caller-provided buffer and zeroizes the temporary buffer immediately after Argon2id processing.
* **`MasterKeyPragma`**: Encapsulates the formatted 64-character hexadecimal SQLCipher PRAGMA string (`x'...'`). Exposes zero naked string or byte conversion operators.
* **`DatabaseSetupStatus`**: Explicit enum representing the database initialization state:
  ```csharp
  public enum DatabaseSetupStatus
  {
      Uninitialized = 0, // percolator.db does not exist (First-time setup required)
      Locked = 1,        // percolator.db exists and is locked (User passphrase required)
      Unlocked = 2       // Session is active and database is decrypted
  }
  ```

### 2.2 Subsystem Ports (`Features/DatabaseLock/Contracts/`)

#### `IDatabaseUnlockCoordinator`
```csharp
namespace Percolator.Application.Ports;

public interface IDatabaseUnlockCoordinator
{
    DatabaseSetupStatus Status { get; }

    /// <summary>
    /// Checks disk for existing database and salt files to determine setup mode.
    /// </summary>
    Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Initializes a new encrypted database on first launch with user password (can be empty).
    /// Generates salt, derives key, verifies canary, runs migrations, builds AuthenticatedHost, and returns session.
    /// </summary>
    Task<Result<IAuthenticatedSession>> InitializeMasterPasswordAsync(string password, CancellationToken ct = default);

    /// <summary>
    /// Unlocks existing database: reads salt, derives key, verifies canary, runs pending migrations, builds AuthenticatedHost.
    /// </summary>
    Task<Result<IAuthenticatedSession>> UnlockAsync(string password, CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops workers, flushes connection pools, zeroizes master key, disposes AuthenticatedHost, and resets status.
    /// </summary>
    Task LockAsync();
}
```

#### `IAuthenticatedSession`
```csharp
namespace Percolator.Application.Ports;

public interface IAuthenticatedSession : IAsyncDisposable
{
    /// <summary>
    /// The dedicated DI service provider holding repositories, DbContexts, and feature services.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Starts all registered IHostedService background workers in the authenticated container.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops all registered IHostedService background workers in the authenticated container.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
```

#### `IMasterKeyProtector`
```csharp
namespace Percolator.Application.Ports;

public interface IMasterKeyProtector : IDisposable
{
    /// <summary>
    /// Gets the formatted 64-hex-character string literal for SQLCipher PRAGMA key.
    /// </summary>
    MasterKeyPragma GetPragmaLiteral();

    /// <summary>
    /// Executes a delegate with access to the raw 32-byte key span in pinned memory.
    /// </summary>
    void WithKeySpan(Action<ReadOnlySpan<byte>> action);

    /// <summary>
    /// Executes a functional mapping with access to the raw 32-byte key span in pinned memory.
    /// </summary>
    T WithKeySpan<T>(Func<ReadOnlySpan<byte>, T> func);
}
```

---

## 3. Workflow Decomposition & Orchestration

The subsystem breaks down complex lifecycle operations into distinct, composable workflow steps:

```
[DetermineStatusAsync]
  ├─ Check percolator.db exists
  └─ Check percolator.salt exists ──> Returns Uninitialized | Locked | Unlocked

[InitializeMasterPasswordAsync (First Launch)]
  ├─ 1. Generate 16-byte random salt via RandomNumberGenerator
  ├─ 2. Write salt to percolator.salt atomically
  ├─ 3. Derive 32-byte key via Argon2id (64MB, 3 iter, 4 lanes)
  ├─ 4. Allocate EphemeralMasterKeyProtector
  ├─ 5. Probe canary connection & execute PRAGMA key
  ├─ 6. Build AuthenticatedHost with SQLite & Repositories
  ├─ 7. Execute EF Core Migrations (Database.MigrateAsync)
  └─ 8. Return AuthenticatedSession wrapping AuthenticatedHost

[UnlockAsync (Subsequent Launch)]
  ├─ 1. Read 16-byte salt from percolator.salt
  ├─ 2. Derive 32-byte key via Argon2id (64MB, 3 iter, 4 lanes)
  ├─ 3. Probe canary connection: PRAGMA key + SELECT count(*) FROM sqlite_master;
  │     └─ Catch SqliteException -> If invalid key, return Result.Failure("Invalid master passphrase")
  ├─ 4. Allocate EphemeralMasterKeyProtector
  ├─ 5. Build AuthenticatedHost with SQLite & Repositories
  ├─ 6. Execute EF Core Migrations (Database.MigrateAsync)
  │     └─ Catch Exception -> Abort: clear pools, zeroize key, dispose host, return Result.Failure(...)
  └─ 7. Return AuthenticatedSession wrapping AuthenticatedHost

[LockAsync (In-Session Lock)]
  ├─ 1. Invoke AuthenticatedSession.StopAsync (gracefully stops all IHostedService workers)
  ├─ 2. Invoke SqliteConnection.ClearAllPools() (closes all file handles)
  ├─ 3. Invoke IMasterKeyProtector.Dispose() (CryptographicOperations.ZeroMemory on key)
  ├─ 4. Dispose AuthenticatedHost
  └─ 5. Reset Status to Locked
```

### 3.1 Key Derivation Pipeline (`Argon2idKeyDeriver`)
- **Parameters**: Argon2id with:
  - Iterations: $3$
  - Memory: $65,536\text{ KB}$ ($64\text{ MB}$)
  - Degree of Parallelism: $4\text{ lanes}$
  - Output: $32\text{ bytes}$ ($256\text{ bits}$)
- **Memory Security**: Passphrase byte arrays are pinned and scrubbed via `CryptographicOperations.ZeroMemory` immediately following derivation.

### 3.2 Pooled Connection Pipeline (`SqlCipherPragmaInterceptor`)
- Registered as a singleton in the Authenticated Container as a `DbConnectionInterceptor`.
- When EF Core or Dapper opens any `DbConnection` from the pool:
  ```csharp
  public override async ValueTask<InterceptionResult> ConnectionOpenedAsync(
      DbConnection connection,
      ConnectionEndEventData eventData,
      InterceptionResult result,
      CancellationToken ct = default)
  {
      using var cmd = connection.CreateCommand();
      var pragma = _keyProtector.GetPragmaLiteral();
      cmd.CommandText = $"PRAGMA key = \"{pragma.Value}\"; " +
                        "PRAGMA journal_mode = WAL; " +
                        "PRAGMA synchronous = NORMAL; " +
                        "PRAGMA foreign_keys = ON;";
      await cmd.ExecuteNonQueryAsync(ct);
      return result;
  }
  ```

### 3.3 Bootstrap to Authenticated Host Dependency Forwarding
When `SqlCipherDatabaseUnlockCoordinator` builds `AuthenticatedHost`:
- Core singletons from `BootstrapHost` are forwarded into the child `ServiceCollection`:
  - `ILoggerFactory` & `ILogger<T>`
  - `IConfiguration`
  - `System.TimeProvider`
  - `ICryptoEngine`
- New services registered exclusively in `AuthenticatedHost`:
  - `IMasterKeyProtector` (holding the derived key in memory)
  - `SqlCipherPragmaInterceptor`
  - `PercolatorDbContext` (configured with SQLite connection string and interceptor)
  - All 57 application and domain repository implementations
  - Background outbox and synchronization `IHostedService` implementations
  - Feature ViewModels (`MainWindowViewModel`, `ChatViewModel`, etc.)

---

## 4. Threat Model & Privacy Invariants

- **Cryptographic Parity & Strength**:
  - AES-256-CBC database encryption via SQLCipher.
  - Key derivation powered by Argon2id (resistant to GPU/ASIC password cracking attacks).
- **Zero-Knowledge Pre-Unlock Invariant**:
  - Prior to key derivation, zero SQLite file handles are opened.
  - Zero repository instances exist in memory.
  - Zero decrypted database contents exist in RAM.
- **Immediate Memory Zeroization**:
  - Master key is held exclusively within `EphemeralMasterKeyProtector`.
  - On `LockAsync()` or application shutdown, `Dispose()` immediately zeroes the memory span.
  - Garbage collector heap sweeps cannot leave lingering plain text key strings because the key is held in unmanaged or pinned memory.
- **Canary Isolation**:
  - Canary verification runs on an isolated non-pooled connection that is closed immediately after the probe.
  - Invalid passphrase attempts never initialize EF Core or populate the connection pool.
- **Migration Atomicity & Failure Rollback**:
  - If schema migrations fail, the session creation aborts immediately, wiping all connection pools and zeroing the key, leaving zero partially migrated or accessible database states.

---

## 5. High-Value Unit Testing Matrix

| Test Scenario | Input / State | Expected Observable Behavior | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **Status Determination: Missing DB** | No `percolator.db` or salt file on disk | `DetermineStatusAsync` returns `DatabaseSetupStatus.Uninitialized` | Checks contract enum, does not mock file internals |
| **Status Determination: Existing DB** | `percolator.db` and `percolator.salt` exist | `DetermineStatusAsync` returns `DatabaseSetupStatus.Locked` | Asserts contract state |
| **First-Time Setup (Strong Password)** | Password provided, no prior files | Creates 16-byte salt, derives key, canary succeeds, migrations run, returns active session | Asserts result success and valid session |
| **First-Time Setup (Blank Password)** | Empty string `""` password provided | Creates salt, derives key from empty string, initializes encrypted database, returns active session | Asserts blank password compatibility |
| **Subsequent Unlock: Correct Password** | Valid master password | Derives key using disk salt, canary succeeds, pending migrations run, returns active session | Asserts result success and running session |
| **Subsequent Unlock: Incorrect Password** | Invalid master password | Canary query fails with SQLCipher error, returns `Result.Failure`, zero host created | Asserts failure contract, no leaked host |
| **Subsequent Unlock: Corrupted Salt** | Salt file < 16 bytes | Returns `Result.Failure("Corrupted database salt")`, derives no keys | Asserts boundary validation |
| **Migration Failure Handling** | DbContext migration throws exception | Clears connection pools, zeroizes key, returns `Result.Failure`, session is null | Asserts cleanup invariants on failure |
| **In-Session Locking** | Active session invokes `LockAsync` | All hosted services stop, connection pools cleared, key span zeroized, status transitions to `Locked` | Asserts post-condition state and zeroization |
| **Interceptor PRAGMA Execution** | DbConnection opened from pool | Connection executes PRAGMA key and WAL mode before returning | Verifies connection interceptor contract |

---

## 6. Cross-Layer Infrastructure Alignment

- **Required NuGet Packages in `Percolator.Infrastructure.csproj`**:
  - `Microsoft.EntityFrameworkCore.Sqlite.Core` (EF Core SQLite without bundled vanilla binaries)
  - `SQLitePCLRaw.bundle_e_sqlcipher` (Native SQLCipher bundle)
  - `Konscious.Security.Cryptography.Argon2` (Argon2id key derivation)
- **Bootstrap Initialization Call**:
  - `SQLitePCL.Batteries_V2.Init()` executed in `AddBootstrapInfrastructure()`.
- **Database File Layout**:
  - `%LocalAppData%/Percolator/percolator.db` (Encrypted database)
  - `%LocalAppData%/Percolator/percolator.salt` (16-byte binary salt)
  - `%LocalAppData%/Percolator/percolator.db-wal` (WAL journal)
  - `%LocalAppData%/Percolator/percolator.db-shm` (Shared memory index)
- **Adapters Registered**:
  - `IDatabaseUnlockCoordinator` $\rightarrow$ `SqlCipherDatabaseUnlockCoordinator` (Bootstrap)
  - `ISaltStorage` $\rightarrow$ `FileSaltStorage` (Bootstrap)
  - `IMasterKeyProtector` $\rightarrow$ `EphemeralMasterKeyProtector` (Authenticated)
  - `DbConnectionInterceptor` $\rightarrow$ `SqlCipherPragmaInterceptor` (Authenticated)
  - `IAuthenticatedSession` $\rightarrow$ `AuthenticatedSession` (Bootstrap & Authenticated)
