# Percolator Database Encryption & Authenticated Session Lifecycle Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: In Review)
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
  - Binary header format using Protocol Buffers (`DatabaseHeader.proto`) compiled exclusively in `Percolator.Infrastructure`.
  - Contains an Argon2id salt and two fixed-size cryptographic slots: **Master Slot** and **Duress Slot**.
  - **Plausible Deniability Invariant**: When no duress password is set, the Duress Slot is filled with cryptographically secure random bytes of identical length. To forensic disk inspection, the header format and byte distribution are 100% indistinguishable regardless of whether a duress password is configured.
- **Optional Duress Passphrase & Decoy State Transition**:
  - Users can optionally establish a duress passphrase (cannot be empty or whitespace) during setup or update it later in authenticated Settings.
  - When the duress passphrase is typed at `UnlockWindow`, the system triggers an immediate **Crypto-Shredding Pipeline** and smoothly transitions the UI to a **Decoy State (Fresh App)**, appearing as a clean, newly installed application without error popups or crash signatures.
- **$O(1)$ Master Password Envelope Re-Wrapping**:
  - Because the database encryption key $K_{\text{db}}$ is stored wrapped in the Master Slot using $K_{\text{master}} = \text{Argon2id}(\text{passphrase}, \text{salt})$, changing the master password in an authenticated session does **not** require re-encrypting the underlying SQLite database file (`PRAGMA rekey`).
  - Changing the master password is an instantaneous $O(1)$ operation: derive new $K_{\text{master}}$, re-encrypt the 32-byte $K_{\text{db}}$, and atomically rewrite `percolator.header`.
- **Argon2id Key Derivation**: Derive 256-bit keys from passphrases using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$) salted with the 16-byte random salt from `percolator.header`.
- **Canary Validation**: Verify the derived master key on an isolated probe connection using `SELECT count(*) FROM sqlite_master;` before initializing EF Core or connection pools.
- **Dynamic Post-Canary Schema Migrations**: Execute EF Core / SQLite migrations (`Database.MigrateAsync()`) immediately post-canary prior to resolving application repositories.
- **Pooled Connection Key Interception**: Apply `PRAGMA key = "x'<hex_key>'\";` and WAL pragmas transparently to every pooled SQLite connection via a `DbConnectionInterceptor`.
- **Explicit Unlock for Blank Passwords**: Support empty master password (`""`) with mandatory first-launch UI warning confirmation, and require explicit interaction ("Unlock" click) on every subsequent launch.
- **Orderly In-Session Relock & Memory Scrubbing**: Provide graceful teardown via `LockAsync`: stop all `IHostedService` instances, flush SQLite pools (`SqliteConnection.ClearAllPools()`), zeroize raw key bytes via `CryptographicOperations.ZeroMemory`, dispose `AuthenticatedHost`, and return UI to `UnlockWindow`.

### 1.2 Explicit Non-Goals (Negative Scope)
- **No Presentation Layer Business Logic**: All database unlocking, envelope decryption, key derivation, and wipe orchestration live in `Percolator.Application` and `Percolator.Infrastructure`. The `Percolator.Desktop` UI layer is strictly a thin presentation client.
- **No Disk Indicators of Duress Configuration**: No separate `duress.hash` or marker files on disk. The header schema and file size remain completely invariant.
- **No Multi-Database or Multi-Tenant Partitioning**: All domain aggregates share `percolator.db`. Multi-user tenancy or partitioned per-aggregate SQLite databases are excluded to avoid redundant KDF latency and complex distributed transactions.
- **No Cloud Escrow or Passphrase Recovery**: Zero server-side escrow or remote recovery mechanisms exist. If the user forgets their master passphrase, data cannot be recovered.
- **No Configurable KDF Algorithms**: Argon2id ($m=64\text{ MB}$, $t=3$, $p=4$) is strictly fixed. No runtime selection of legacy algorithms (PBKDF2, scrypt, bcrypt) is permitted.
- **No In-Memory SQLite Modes**: SQLCipher must operate against persistent disk files; in-memory database modes are excluded from production vault operation.
- **No Master Password Persistence**: The master passphrase is never cached on disk, DPAPI, or OS keychain without explicit user unlock.
- **No Repository Resolution in Bootstrap Container**: No repository or DbContext may be registered or resolved in the root DI container.

---

## 2. Cross-Layer Architecture & Strict Onion Boundaries

All dependencies point strictly inward toward `Percolator.Domain`. `Percolator.Application` has zero references to `Percolator.Infrastructure` or `Google.Protobuf`.

```
01 - Domain (Percolator.Domain):
└── Security/
    ├── ValueObjects/
    │   ├── VaultPassphrase.cs        # ISensitiveSecret: validated passphrase buffer with zeroization
    │   ├── DatabaseSetupStatus.cs    # State enum: Uninitialized, Locked, Unlocked
    │   └── UnlockResult.cs           # Polymorphic union: Success(session), DuressActivated, InvalidCredentials
    └── Ports/
        └── IMasterKeyHandle.cs       # Pure key wrapper: zero-allocation pragma application, ISensitiveSecret

02 - Application (Percolator.Application):
└── Ports/
    ├── IDatabaseUnlockCoordinator.cs # Primary driving port for vault lifecycle & session coordination
    ├── IAuthenticatedSession.cs      # Token/handle to Stage 2 container
    └── IDuressWipeHandler.cs         # Driven port: multi-phase crypto-shredding orchestrator

03 - Infrastructure (Percolator.Infrastructure):
├── Protos/
│   └── DatabaseHeader.proto          # Protobuf schema for twin-slot header (Infrastructure exclusive)
├── Vault/
│   ├── IHeaderStorage.cs             # Internal infrastructure port: reads/writes DatabaseHeader protobuf
│   ├── ProtobufHeaderStorage.cs      # Implements IHeaderStorage using DatabaseHeader.proto
│   ├── DuressWipeHandler.cs          # Implements IDuressWipeHandler (overwrites header & DB files)
│   ├── MasterKeyHandle.cs            # Implements IMasterKeyHandle (pinned memory, formats pragma on stack)
│   └── SqlCipherDatabaseUnlockCoordinator.cs # Implements IDatabaseUnlockCoordinator
└── Persistence/
    ├── PercolatorDbContext.cs        # Stage 2 post-unlock EF Core DbContext
    ├── SqlCipherPragmaInterceptor.cs # Pooled connection PRAGMA key interceptor
    └── Stage2HostFactory.cs          # Constructs Stage 2 IHost & executes Database.MigrateAsync

05 - Desktop (Percolator.Desktop):
├── Features/
│   ├── Unlock/
│   │   ├── UnlockWindow.axaml
│   │   ├── UnlockWindow.axaml.cs
│   │   └── UnlockViewModel.cs        # Thin presentation: delegates to IDatabaseUnlockCoordinator
│   ├── Setup/
│   │   ├── MasterPasswordSetupWindow.axaml
│   │   ├── MasterPasswordSetupWindow.axaml.cs
│   │   └── MasterPasswordSetupViewModel.cs
│   └── Settings/
│       ├── SettingsView.axaml
│       ├── SettingsView.axaml.cs
│       └── SettingsViewModel.cs      # Change master password & emergency duress configuration
└── Common/
    └── Windowing/
        └── WindowHandoffCoordinator.cs # Manages UnlockWindow -> MainWindow -> Decoy transitions
```

### 2.1 Protobuf Header Contract (`Percolator.Infrastructure/Protos/DatabaseHeader.proto`)

Compiled strictly inside `Percolator.Infrastructure` where `Google.Protobuf` is referenced:

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

---

## 2.2 Domain Contracts & Value Objects (`Percolator.Domain.Security`)

### `VaultPassphrase` (`Percolator.Domain.Security.ValueObjects`)

A unified, memory-scrubbing passphrase wrapper that extracts UTF-8 spans directly into temporary buffers without leaving string or byte artifacts on the managed GC heap:

```csharp
namespace Percolator.Domain.Security.ValueObjects;

using System.Security.Cryptography;
using System.Text;
using Percolator.Domain.Common;

public sealed class VaultPassphrase : ISensitiveSecret
{
    private char[] _chars;
    public bool IsZeroized { get; private set; }
    public bool IsEmpty => _chars.Length == 0;

    private VaultPassphrase(char[] chars) => _chars = chars;

    public static VaultPassphrase Create(string? input) => 
        new(input?.ToCharArray() ?? Array.Empty<char>());

    public static VaultPassphrase Empty => new(Array.Empty<char>());

    /// <summary>
    /// Executes an action with the UTF-8 encoded bytes of the passphrase in a temporary stack/rented buffer,
    /// deterministically zeroizing the memory immediately upon completion.
    /// </summary>
    public void WithUtf8Span(Action<ReadOnlySpan<byte>> action)
    {
        ObjectDisposedException.ThrowIf(IsZeroized, this);
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(_chars.Length);
        Span<byte> buffer = maxByteCount <= 256 ? stackalloc byte[maxByteCount] : new byte[maxByteCount];
        try
        {
            int written = Encoding.UTF8.GetBytes(_chars.AsSpan(), buffer);
            action(buffer[..written]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// Evaluates if two passphrases are identical in constant time.
    /// </summary>
    public static bool ConstantTimeEquals(VaultPassphrase a, VaultPassphrase b)
    {
        return a.WithUtf8Span(spanA =>
            b.WithUtf8Span(spanB =>
                CryptographicOperations.FixedTimeEquals(spanA, spanB)));
    }

    public void Zeroize()
    {
        if (IsZeroized) return;
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_chars.AsSpan()));
        _chars = Array.Empty<char>();
        IsZeroized = true;
    }

    public void Dispose() => Zeroize();
}
```

### `UnlockResult` (`Percolator.Domain.Security.ValueObjects`)

A clean polymorphic discriminated union modeling the outcome of an unlock attempt, eliminating temporal coupling and dangling nullable session properties:

```csharp
namespace Percolator.Domain.Security.ValueObjects;

using Percolator.Application.Ports;

public abstract record UnlockResult
{
    public sealed record Success(IAuthenticatedSession Session) : UnlockResult;
    public sealed record DuressActivated : UnlockResult;
    public sealed record InvalidCredentials : UnlockResult;
}
```

### `DatabaseSetupStatus` (`Percolator.Domain.Security.ValueObjects`)

```csharp
namespace Percolator.Domain.Security.ValueObjects;

public enum DatabaseSetupStatus
{
    Uninitialized = 0,
    Locked = 1,
    Unlocked = 2
}
```

### `IMasterKeyHandle` (`Percolator.Domain.Security.Ports`)

An in-memory key protector that applies the SQLite encryption key to database connections on the stack without materializing a managed hex string in the GC heap:

```csharp
namespace Percolator.Domain.Security.Ports;

using System.Data.Common;
using Percolator.Domain.Common;

public interface IMasterKeyHandle : ISensitiveSecret
{
    /// <summary>
    /// Executes PRAGMA key directly on the connection using a stack-allocated buffer.
    /// Never creates an immutable string containing the hex key in the managed GC heap.
    /// </summary>
    void ApplyPragmaKey(DbConnection connection);

    /// <summary>
    /// Executes a callback with access to the raw 32-byte key span for O(1) envelope re-wrapping.
    /// </summary>
    void WithKeySpan(Action<ReadOnlySpan<byte>> action);
}
```

---

## 2.3 Application Ports (`Percolator.Application.Ports`)

### `IDatabaseUnlockCoordinator`

The primary application port exposed to presentation clients and daemon hosts. Notice that **zero infrastructure types** (Protobuf, SQLite, file streams) are present in this contract:

```csharp
namespace Percolator.Application.Ports;

using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

public interface IDatabaseUnlockCoordinator
{
    DatabaseSetupStatus Status { get; }

    /// <summary>
    /// Checks disk for existing header and database files to determine initial setup state.
    /// </summary>
    Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Initializes new database with master passphrase and optional duress passphrase.
    /// Generates salt, derives master key, encrypts slots, verifies canary, runs initial migrations.
    /// </summary>
    Task<Result<IAuthenticatedSession>> InitializeAsync(
        VaultPassphrase masterPassword, 
        VaultPassphrase? duressPassword = null, 
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates candidate password against master slot and duress slot.
    /// Returns UnlockResult.Success(session), DuressActivated, or InvalidCredentials.
    /// </summary>
    Task<UnlockResult> UnlockAsync(
        VaultPassphrase candidatePassword, 
        CancellationToken ct = default);

    /// <summary>
    /// Updates the master password within an active authenticated session by re-wrapping the database key in the master slot.
    /// Does not require re-encrypting the underlying SQLite database file (O(1) operation).
    /// </summary>
    Task<Result> ChangeMasterPasswordAsync(
        VaultPassphrase currentPassword, 
        VaultPassphrase newPassword, 
        CancellationToken ct = default);

    /// <summary>
    /// Sets, updates, or clears (if null or empty) the emergency duress passphrase within an active session.
    /// </summary>
    Task<Result> ConfigureDuressPasswordAsync(
        VaultPassphrase? duressPassword, 
        CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops workers, flushes connection pools, zeroizes master key, disposes AuthenticatedHost.
    /// </summary>
    Task LockAsync();
}
```

### `IDuressWipeHandler`
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

### `IAuthenticatedSession`
```csharp
namespace Percolator.Application.Ports;

public interface IAuthenticatedSession : IAsyncDisposable
{
    IServiceProvider Services { get; }
}
```

---

## 2.4 Internal Infrastructure Components (`Percolator.Infrastructure.Vault`)

Because the physical disk layout, Protobuf serialization, and SQLite canary mechanics are purely concrete infrastructure details, their helper contracts remain internal to `Percolator.Infrastructure`:

### `IHeaderStorage` (Internal to `Percolator.Infrastructure.Vault`)
```csharp
namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;

internal interface IHeaderStorage
{
    /// <summary>
    /// Reads and deserializes percolator.header. Returns failure if file is missing or corrupted.
    /// </summary>
    Task<Result<DatabaseHeader>> ReadHeaderAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically writes DatabaseHeader to percolator.header via temporary file flush and replace.
    /// </summary>
    Task<Result> WriteHeaderAsync(DatabaseHeader header, CancellationToken ct = default);

    /// <summary>
    /// Overwrites percolator.header with random bytes before deleting it from disk.
    /// </summary>
    Task<Result> OverwriteAndWipeAsync(CancellationToken ct = default);
}
```

---

## 3. Workflow Decomposition & Orchestration

The unlock and vault orchestration is decomposed into discrete, observable pipeline phases executed by `SqlCipherDatabaseUnlockCoordinator`:

```
[UnlockAsync Workflow]
  │
  ├─ Phase 1: Header Loading (IHeaderStorage.ReadHeaderAsync)
  │     Reads percolator.header and extracts salt + twin slots.
  │
  ├─ Phase 2: Key Derivation & Slot Decryption
  │     Derives Kcand via Argon2id(candidatePassword, header.Salt).
  │     │
  │     ├─ Branch A: Master Slot Evaluation
  │     │     ChaCha20Poly1305.Decrypt(MasterSlotNonce, MasterSlotPayload, Kcand)
  │     │     ├─ Success -> Master Key (Kdb) recovered!
  │     │     │             ├─ Step 3a: Canary Probe: PRAGMA key = "x'<Kdb>'"; SELECT count(*) FROM sqlite_master;
  │     │     │             ├─ Step 3b: Build Authenticated Host (Stage2HostFactory)
  │     │     │             │      Registers MasterKeyHandle and EF Core DbContext.
  │     │     │             ├─ Step 3c: Run Migrations (Database.MigrateAsync)
  │     │     │             │      Executes any pending schema migrations dynamically.
  │     │     │             └─ Step 3d: Return UnlockResult.Success(session)
  │     │     │
  │     │     └─ Auth Failed -> Proceed to Duress Check (Branch B)
  │     │
  │     └─ Branch B: Duress Slot Evaluation
  │           ChaCha20Poly1305.Decrypt(DuressSlotNonce, DuressSlotPayload, Kcand)
  │           ├─ Success & Payload == "PERCOLATOR_DURESS_SENTINEL_OK" ->
  │           │     ├─ Step 4a: Trigger Crypto-Shredding (IDuressWipeHandler.ExecuteWipeAsync)
  │           │     │      ├─ Overwrite percolator.header with random bytes & flush
  │           │     │      ├─ Overwrite percolator.db, wal, shm with random bytes & delete
  │           │     │      └─ Purge avatar/attachment cache directories
  │           │     ├─ Step 4b: Status = DatabaseSetupStatus.Uninitialized
  │           │     └─ Step 4c: Return UnlockResult.DuressActivated()
  │           │
  │           └─ Auth Failed -> Return UnlockResult.InvalidCredentials()
```

### 3.1 Post-Duress Decoy State UI Handoff
When `UnlockResult.DuressActivated` is returned:
1. `UnlockViewModel` receives the decoy signal without displaying an alert or error popup.
2. The coordinator informs `WindowHandoffCoordinator`.
3. The UI smoothly transitions into `IdentityGateWindow` / First-Time Setup mode as a clean, blank slate.
4. An adversary observing the system sees an empty, unconfigured installation.

### 3.2 Master Password Update ($O(1)$ Re-Wrap Workflow)
1. User navigates to Settings $\rightarrow$ Security $\rightarrow$ *Change Master Password*.
2. User provides `currentPassword` and `newPassword`.
3. `ChangeMasterPasswordAsync`:
   - Validates `currentPassword` against active `IMasterKeyHandle` in the authenticated session.
   - Generates a fresh 16-byte random salt.
   - Derives new $K_{\text{new\_master}} = \text{Argon2id}(\text{newPassword}, \text{newSalt})$.
   - Uses `IMasterKeyHandle.WithKeySpan` to retrieve the active 32-byte $K_{\text{db}}$.
   - Re-encrypts $K_{\text{db}}$ using ChaCha20-Poly1305 under $K_{\text{new\_master}}$ with a fresh 12-byte nonce.
   - Preserves or re-encrypts the duress slot under the new salt.
   - Atomically overwrites `percolator.header` via `IHeaderStorage`.
   - Zeroizes temporary keys via `ISensitiveSecret.Zeroize()`.
   - **Performance Guarantee**: Because $K_{\text{db}}$ remains identical, zero SQLite tables or pages are rewritten. Operation completes in $< 200\text{ ms}$ with zero risk of database corruption.

### 3.3 Duress Password Configuration Workflow
1. User navigates to Settings $\rightarrow$ Security $\rightarrow$ *Emergency Duress Passphrase*.
2. User provides new duress password or leaves blank to clear via `ConfigureDuressPasswordAsync(duressPassword)`.
3. Invariant check: if setting password, verifies `!VaultPassphrase.ConstantTimeEquals(masterPassword, duressPassword)`.
4. If setting: derives duress key via Argon2id, encrypts sentinel, and writes updated `DatabaseHeader`.
5. If clearing: generates 60 random bytes into `duress_slot_nonce` + `duress_slot_payload` and writes header.
6. Plausible deniability invariant is preserved at all times.

---

## 4. Threat Model & Privacy Invariants

- **Plausible Deniability**:
  - The existence of a duress password is cryptographically unprovable from forensic disk analysis. Dummy slots and active slots are identical in structure, length, and entropy.
- **Crypto-Shredding Guarantee**:
  - Overwriting `percolator.header` renders all remaining physical NAND flash remnants of `percolator.db` completely unrecoverable, even if raw SSD controller blocks are dumped via chip-off analysis.
- **Coercion Safety**:
  - Entering the duress password produces zero anomalous alerts or crash stack traces; the application appears as an empty first-time run.
- **Memory Security**:
  - Passphrase buffers and intermediate keys implement `ISensitiveSecret` and are scrubbed with `CryptographicOperations.ZeroMemory` immediately upon completion of derivation/decryption.
  - SQLCipher PRAGMA execution formats on the stack to prevent managed string leakage of the master key.
- **Zero Headless Coupling**:
  - All vault coordination lives in `Percolator.Application` and `Percolator.Infrastructure`. Headless Linux relay hosts, integration tests, and desktop clients use the exact same secure unlock code without any UI dependency.

---

## 5. High-Value Unit Testing Matrix

| Test Scenario | Input / State | Expected Observable Behavior | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **First-Time Setup (With Duress)** | Master password + Duress password | Creates `DatabaseHeader` with valid master slot and encrypted duress slot; returns session | Asserts result success; verifies header file exists |
| **First-Time Setup (Without Duress)** | Master password only | Creates `DatabaseHeader` with valid master slot and dummy random duress slot; returns session | Asserts dummy slot length == 48 bytes |
| **Normal Unlock** | Valid master password | Unlocks master slot, canary passes, migrations execute, returns `UnlockResult.Success(session)` | Asserts session is non-null and ready |
| **Duress Unlock Triggered** | Valid duress password | Decrypts duress slot, executes `IDuressWipeHandler`, deletes database/header files, returns `UnlockResult.DuressActivated` | Verifies disk files are deleted and status is `Uninitialized` |
| **Invalid Passphrase Attempt** | Incorrect password | Fails both master and duress slots, returns `UnlockResult.InvalidCredentials`, zero wipe occurs | Asserts result type; verifies database files remain intact |
| **Master Password Change** | Current password + New password | Re-wraps master slot with new key; database remains fully readable; subsequent unlock requires new password | Tests end-to-end master password re-wrap |
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
- **Adapters Registered in DI**:
  - *Bootstrap Container*:
    - `IDatabaseUnlockCoordinator` $\rightarrow$ `SqlCipherDatabaseUnlockCoordinator`
    - `IDuressWipeHandler` $\rightarrow$ `DuressWipeHandler`
  - *Authenticated Container*:
    - `IMasterKeyHandle` $\rightarrow$ `MasterKeyHandle`
    - `DbConnectionInterceptor` $\rightarrow$ `SqlCipherPragmaInterceptor`
    - `IAuthenticatedSession` $\rightarrow$ `AuthenticatedSession`
