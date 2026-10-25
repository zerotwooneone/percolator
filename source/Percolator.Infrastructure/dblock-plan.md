# Percolator Database Encryption & Authenticated Session Lifecycle Plan

## 1. Executive Summary & Requirements

This document specifies the architectural blueprint for encrypting local SQLite databases with SQLCipher, enforcing a zero-repository pre-unlock state, and managing application session lifecycles using a **Two-Stage Dependency Injection Architecture (Option 2)**.

### Core Invariants:
1. **Mandatory Passphrase Gate**: Every database connection requires a user-specified passphrase (an empty string `""` is supported with explicit UI confirmation).
2. **Pre-Unlock Isolation**: Zero SQLite repositories, DbContexts, or background outbox/sync workers exist in memory prior to database unlocking.
3. **Explicit Unlock**: Every launch (even when configured with a blank password) requires the user to interact with the unlock screen and press "Unlock" to enter.
4. **Schema Migrations**: Schema migrations run immediately following successful key derivation and verification, prior to resolving any repositories.
5. **In-Session Locking & Relocking**: Users can lock the application at any time. Locking stops all background services, drops SQLite connection pools, zeroizes the master key in RAM, destroys the authenticated session, and returns the UI to the bootstrap unlock screen.

---

## 2. Database File Layout: Single Unified DB vs. 4 Partitioned DBs

We evaluate whether Percolator should use a **Single Unified Database (`percolator.db`)** or retain **4 Partitioned Databases (`ops.db`, `vault.db`, `chat.db`, `transfers.db`)**.

### Comparison Matrix

| Evaluation Dimension | Option A: Single Unified Database (`percolator.db`) | Option B: 4 Partitioned Databases (`ops.db`, `vault.db`, `chat.db`, `transfers.db`) |
| :--- | :--- | :--- |
| **SQLCipher PRAGMA & KDF Overhead** | **1x derivation and 1x PRAGMA execution per connection**. Fast connection startup. | **4x derivation and 4x PRAGMA setup** for every connection opened across subsystems. |
| **Connection Pooling & RAM Footprint** | **Minimal**: Single connection pool, 1 unified page cache, fewer open OS file handles. | **High**: 4 separate pools, 4 page caches, 4x file descriptors maintained concurrently. |
| **Database Migrations** | **Atomic & Simple**: Single schema version table (`__EFMigrationsHistory`), atomic migrations across all tables. | **Complex**: Must orchestrate 4 independent migration pipelines. Partial schema update failures cause cross-DB inconsistency. |
| **Foreign Keys & Cross-Domain Integrity** | **Supported**: Can define SQL foreign keys between identities, contacts, channels, and outbox envelopes. | **Impossible across files**: Cannot use SQLite foreign keys or cross-database transactions; requires application-level manual consistency. |
| **Concurrency & WAL Contention** | **Excellent in WAL mode**: SQLite WAL mode supports multiple concurrent readers and 1 concurrent writer. Desktop client write load (tens of ops/sec) is well within WAL capacity. | **Isolated**: High-throughput file transfer piece updates don't lock chat writes. However, chunk metadata is mostly written in memory before disk flush anyway. |
| **Backup, Restore & Reset** | **Simple**: Copy/wipe a single `.db` file (+ WAL/SHM). | **Fragile**: Must synchronize backups across 4 files simultaneously to avoid state mismatch (e.g. chat referencing deleted ratchet key). |
| **Selective Data Purging** | Handled via `DELETE FROM ...` or `VACUUM`. | Can wipe `chat.db` or `transfers.db` directly from disk without touching `vault.db`. |

### Architectural Decision
**We adopt Option A: Single Unified Encrypted Database (`percolator.db`).**
- Because **all 4 subsystems are encrypted with the user's master key and unlocked simultaneously**, splitting into 4 separate database files adds massive migration and connection overhead with zero cryptographic benefit.
- With SQLite **Write-Ahead Logging (WAL)** enabled (`PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;`), concurrent reads from chat timelines and writes from outbox/file transfers operate without thread lock contention.
- Relational integrity across identities, ratchet sessions, outbox jobs, and channels is strictly preserved via native SQLite foreign keys.

---

## 3. Two-Stage Container Architecture (Option 2)

The application lifecycle is decoupled into two completely separate DI containers:

```
                      ┌──────────────────────────────────────────────┐
                      │          Bootstrap Host (Root DI)            │
                      │  • Logging, File Paths, Windowing Manager    │
                      │  • ICryptoEngine, IDateTimeProvider          │
                      │  • IDatabaseUnlockCoordinator                │
                      │  • Instantiates UnlockWindow (UnlockViewModel)│
                      └──────────────────────┬───────────────────────┘
                                             │
                        1. User inputs password (or blank)
                        2. Master key derived (Argon2id)
                        3. SQLCipher canary verified
                        4. Migrations executed
                                             ▼
                      ┌──────────────────────────────────────────────┐
                      │       Authenticated Host (Child IHost)       │
                      │  • MasterKeyProtector (Ephemeral Memory)     │
                      │  • PercolatorDbContext & Connection Factory  │
                      │  • All 48 Repositories & CQRS Query Services │
                      │  • Background Workers (IHostedService list)  │
                      │  • Instantiates MainWindow (MainViewModel)   │
                      └──────────────────────┬───────────────────────┘
                                             │
       ┌─────────────────────────────────────┴─────────────────────────────────────┐
       │                                                                           │
       ▼                                                                           ▼
[Session Running]                                                           [Lock Action Triggered]
• AuthenticatedHost.StartAsync()                                            • AuthenticatedHost.StopAsync()
• Background workers active                                                 • Connections closed & pools cleared
• MainWindow visible                                                        • Master key zeroized in RAM
• UnlockWindow closed                                                       • AuthenticatedHost disposed
                                                                            • MainWindow closed
                                                                            • UnlockWindow reopened
```

---

## 4. Application & Domain Ports

### 4.1 `Percolator.Application.Ports.IDatabaseUnlockCoordinator`
```csharp
namespace Percolator.Application.Ports;

public enum DatabaseSetupStatus
{
    Uninitialized, // Database file does not exist yet (First-time user setup required)
    Locked,        // Database file exists and is locked (User passphrase required)
    Unlocked       // Database is actively decrypted and session is running
}

public interface IDatabaseUnlockCoordinator
{
    DatabaseSetupStatus Status { get; }
    bool IsUnlocked => Status == DatabaseSetupStatus.Unlocked;

    /// <summary>
    /// Checks disk for existing database and determines whether setup or unlock is required.
    /// </summary>
    Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Initializes database on first launch with user password (can be empty string).
    /// Derives master key, creates percolator.db, runs initial migrations, and returns active session.
    /// </summary>
    Task<Result<IAuthenticatedSession>> InitializeMasterPasswordAsync(string password, CancellationToken ct = default);

    /// <summary>
    /// Verifies password against SQLCipher, runs pending migrations, builds AuthenticatedHost, and returns active session.
    /// </summary>
    Task<Result<IAuthenticatedSession>> UnlockAsync(string password, CancellationToken ct = default);

    /// <summary>
    /// Stops all background workers, clears connection pools, zeroizes master key, disposes AuthenticatedHost, and re-locks.
    /// </summary>
    Task LockAsync();
}
```

### 4.2 `Percolator.Application.Ports.IAuthenticatedSession`
```csharp
namespace Percolator.Application.Ports;

public interface IAuthenticatedSession : IAsyncDisposable
{
    /// <summary>
    /// The dedicated DI service provider holding repositories, DbContexts, and feature services.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Starts all IHostedService implementations registered in the session.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops all IHostedService implementations registered in the session.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
```

### 4.3 `Percolator.Application.Ports.IMasterKeyProtector`
```csharp
namespace Percolator.Application.Ports;

/// <summary>
/// Scoped within the Authenticated Container only. Holds the ephemeral 256-bit SQLCipher key.
/// </summary>
public interface IMasterKeyProtector : IDisposable
{
    ReadOnlySpan<byte> KeyBytes { get; }
    string GetPragmaKeyHexString();
}
```

---

## 5. Infrastructure Realization

### 5.1 SQLCipher Key Derivation & Verification
1. **Key Derivation (Argon2id)**:
   - When a password (including `""`) is entered:
   - Salt: 16-byte cryptographically secure salt stored in `percolator.salt` (or SQLite file header salt).
   - Parameters: Argon2id with 3 iterations, 64 MB RAM, 4 parallelism lanes, outputting 32 bytes (256-bit raw key).
2. **Canary Validation**:
   - Connection opened with `PRAGMA key = "x'<32_bytes_hex>'";`
   - Execute query: `SELECT count(*) FROM sqlite_master;`
   - If SQLCipher returns error `file is not a database` or `database disk image is malformed`: password is wrong -> return `Result.Failure("Invalid master passphrase")`.
   - If query returns integer: password is valid.

### 5.2 Schema Migrations Post-Unlock
- Once the canary check passes:
  ```csharp
  var dbContext = sessionServices.GetRequiredService<PercolatorDbContext>();
  await dbContext.Database.MigrateAsync(ct);
  ```
- All tables (`identities`, `prekeys`, `ratchets`, `channels`, `messages`, `transfers`, `outbox`) are created or upgraded before any application repository can execute queries.

### 5.3 Memory Scrubbing on Lock
When `LockAsync()` is called:
```csharp
// 1. Stop all hosted services
await authenticatedHost.StopAsync(ct);

// 2. Clear SQLCipher connection pools to close file handles
SqliteConnection.ClearAllPools();

// 3. Zeroize master key in memory
masterKeyProtector.Dispose(); // Internally executes CryptographicOperations.ZeroMemory

// 4. Dispose AuthenticatedHost
await authenticatedHost.DisposeAsync();
```

---

## 6. Desktop UI Integration & Window Handoff

### 6.1 Bootstrap Launch Flow
1. `App.axaml.cs` builds the `BootstrapContainer`:
   - Configures Logging, `SignalCryptoEngine`, `SystemDateTimeProvider`, `WindowManager`, and `SqlCipherDatabaseUnlockCoordinator`.
   - Instantiates `UnlockWindow` with `UnlockViewModel`.
   - Sets `Desktop.MainWindow = unlockWindow; unlockWindow.Show();`.
2. `UnlockViewModel.InitializeAsync()` queries `coordinator.DetermineStatusAsync()`:
   - If `Uninitialized`: Shows *"Set Master Password"* mode (with confirmation alert if password is left blank).
   - If `Locked`: Shows *"Enter Master Password to Unlock"* mode (empty password field by default).

### 6.2 Unlock & Window Handoff
1. User clicks **"Unlock"** (or **"Set Password"**).
2. `UnlockViewModel` invokes `coordinator.UnlockAsync(password)`.
3. On success:
   - The coordinator builds `AuthenticatedHost` and runs `await session.StartAsync()`.
   - WindowManager resolves `MainWindow` from `session.Services`.
   - `mainWindow.Show()`.
   - `unlockWindow.Close()`.
   - `Desktop.MainWindow = mainWindow;`.

### 6.3 Secure In-Session Relock Flow
1. User clicks **"Lock"** in `MainWindow` (or inactivity timer fires).
2. Application invokes `coordinator.LockAsync()`.
3. Coordinator cleanly tears down `AuthenticatedHost`.
4. WindowManager instantiates a fresh `UnlockWindow` from the root bootstrap container and displays it.
5. `mainWindow.Close()`.
6. Repositories and decrypted data are completely cleared from memory.
