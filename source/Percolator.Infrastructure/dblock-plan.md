# Percolator Database Encryption & Authenticated Session Lifecycle Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: In Review)
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
- **Optional Duress Passphrase & Instant Decoy Transition**:
  - Users can optionally establish a duress passphrase (cannot be empty or whitespace) during setup or update it later in authenticated Settings.
  - When the duress passphrase is typed at `UnlockWindow`, the system triggers an immediate **Crypto-Shredding Pipeline** destroying the header in $< 2\text{ ms}$ and smoothly transitions the UI to a **Decoy State (Fresh App)** without disk thrashing, error popups, or crash signatures.
- **$O(1)$ Master Password Envelope Re-Wrapping**:
  - Because the database encryption key $K_{\text{db}}$ is stored wrapped in the Master Slot using $K_{\text{master}} = \text{Argon2id}(\text{passphrase}, \text{salt})$, changing the master password in an authenticated session does **not** require re-encrypting the underlying SQLite database file (`PRAGMA rekey`).
  - Changing the master password is an instantaneous $O(1)$ operation: derive new $K_{\text{master}}$, re-encrypt the 32-byte $K_{\text{db}}$, and atomically rewrite `percolator.header`.
- **Argon2id Key Derivation**: Derive 256-bit keys from passphrases using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$) salted with the 16-byte random salt from `percolator.header`.
- **Canary Validation**: Verify the derived master key on an isolated probe connection using `SELECT count(*) FROM sqlite_master;` before initializing EF Core or connection pools, explicitly trapping native error code 26 (`SQLITE_NOTADB`).
- **Dynamic Post-Canary Schema Migrations**: Execute EF Core / SQLite migrations (`Database.MigrateAsync()`) immediately post-canary prior to resolving application repositories.
- **Native SQLCipher Key Application**: Apply the 32-byte database encryption key directly via `SQLitePCL.raw.sqlite3_key` to unmanaged handles on connection open, eliminating managed string allocations of sensitive key material.
- **Explicit Unlock for Blank Passwords**: Support empty master password (`""`) with mandatory first-launch UI warning confirmation, and require explicit interaction ("Unlock" click) on every subsequent launch.
- **Orderly In-Session Relock & Memory Scrubbing**: Provide graceful teardown via `LockAsync`: stop all `IHostedService` instances, flush SQLite pools (`SqliteConnection.ClearAllPools()`), zeroize raw key bytes via `CryptographicOperations.ZeroMemory`, dispose `AuthenticatedHost`, and return UI to `UnlockWindow`.

### 1.2 Explicit Non-Goals (Negative Scope)
- **No Progressive Backoff Throttling**: Artificial login delay throttling is excluded; Argon2id memory-hardness ($64\text{ MB}, t=3, p=4$) provides consistent, deterministic computational cost without UI delay loops.
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
│   ├── DuressWipeHandler.cs          # Implements IDuressWipeHandler (instant crypto-shredder)
│   ├── MasterKeyHandle.cs            # Implements IMasterKeyHandle (pinned memory, sqlite3_key provider)
│   └── SqlCipherDatabaseUnlockCoordinator.cs # Implements IDatabaseUnlockCoordinator
└── Persistence/
    ├── PercolatorDbContext.cs        # Stage 2 post-unlock EF Core DbContext
    ├── SqlCipherPragmaInterceptor.cs # Pooled connection interceptor (calls SQLitePCL.raw.sqlite3_key)
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

An in-memory key protector that applies the raw SQLite encryption key directly to database connections using `SQLitePCL.raw.sqlite3_key`, avoiding string allocations of sensitive key material:

```csharp
namespace Percolator.Domain.Security.Ports;

using System.Data.Common;
using Percolator.Domain.Common;

public interface IMasterKeyHandle : ISensitiveSecret
{
    /// <summary>
    /// Passes the raw 32-byte key span directly to SQLitePCL.raw.sqlite3_key.
    /// Never creates an immutable hex string in the managed GC heap.
    /// </summary>
    void ApplyKeyToConnection(DbConnection connection);

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
    /// Executes instant cryptographic shredding:
    /// Overwrites percolator.header with random bytes and forces hardware disk flush (< 2 ms).
    /// Deletes percolator.header, percolator.db, percolator.db-wal, percolator.db-shm.
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

Because physical disk layout, Protobuf serialization, and SQLite canary mechanics are purely concrete infrastructure details, their helper contracts remain internal to `Percolator.Infrastructure`:

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

## 3. Workflow Decomposition & Concrete Implementation Mechanics

### 3.1 ChaCha20-Poly1305 Slot Trials & Exception Control Flow
`SqlCipherDatabaseUnlockCoordinator` derives $K_{\text{cand}} = \text{Argon2id}(\text{passphrase}, \text{salt})$ exactly once, then evaluates the two 48-byte AEAD slots using BCL `System.Security.Cryptography.ChaCha20Poly1305`:

```csharp
// Each slot payload is 48 bytes: [0..32] ciphertext + [32..48] 16-byte Poly1305 tag
using var cipher = new ChaCha20Poly1305(derivedCandidateKey);
Span<byte> candidatePlaintext = stackalloc byte[32];

// 1. Evaluate Master Slot:
bool masterSuccess = false;
try
{
    cipher.Decrypt(
        header.MasterSlotNonce.Span, 
        header.MasterSlotPayload.Span[..32], 
        header.MasterSlotPayload.Span[32..], 
        candidatePlaintext);
    masterSuccess = true;
}
catch (AuthenticationTagMismatchException)
{
    // Tag mismatch: candidate key is not the master key
}

if (masterSuccess)
{
    // candidatePlaintext holds raw 32-byte Kdb
    return await CompleteMasterUnlockAsync(candidatePlaintext, ct);
}

// 2. Evaluate Duress Slot:
bool duressSuccess = false;
try
{
    cipher.Decrypt(
        header.DuressSlotNonce.Span, 
        header.DuressSlotPayload.Span[..32], 
        header.DuressSlotPayload.Span[32..], 
        candidatePlaintext);
    duressSuccess = true;
}
catch (AuthenticationTagMismatchException)
{
    // Active duress tag mismatch OR dummy random bytes failed authentication
}

if (duressSuccess && CryptographicOperations.FixedTimeEquals(candidatePlaintext, DuressSentinelBytes))
{
    await _wipeHandler.ExecuteWipeAsync(ct);
    Status = DatabaseSetupStatus.Uninitialized;
    return new UnlockResult.DuressActivated();
}

return new UnlockResult.InvalidCredentials();
```

### 3.2 Native Connection Keying & Researched Canary Validation
Instead of executing SQL `PRAGMA key = "x'...'"` with string interpolation, connection keying passes raw byte spans directly to native SQLCipher via `SQLitePCLRaw`:

```csharp
// SqlCipherPragmaInterceptor / CanaryProbe:
public static void KeyConnection(SqliteConnection connection, ReadOnlySpan<byte> key32Bytes)
{
    int rc = SQLitePCL.raw.sqlite3_key(connection.Handle, key32Bytes);
    if (rc != SQLitePCL.raw.SQLITE_OK)
    {
        throw new SecurityException($"SQLitePCL.raw.sqlite3_key failed with return code {rc}");
    }
}
```

#### Researched Canary Error Handling (`SQLITE_NOTADB` = 26)
When an invalid key is supplied, SQLCipher fails to decrypt page 1, failing SQLite's header magic check (`"SQLite format 3\000"`). SQLite does not define a `SQLITE_WRONGKEY` code; it throws `Microsoft.Data.Sqlite.SqliteException` with native error code **`26` (`SQLITE_NOTADB: "file is not a database"` or `"file is encrypted or is not a database"`)**:

```csharp
public async Task<bool> VerifyCanaryAsync(SqliteConnection connection, CancellationToken ct)
{
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
    try
    {
        await cmd.ExecuteScalarAsync(ct);
        return true;
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB
    {
        return false;
    }
}
```

### 3.3 Instant Crypto-Shredding Pipeline (`DuressWipeHandler`)
Under coercion, multi-pass overwrites of large SQLite database files cause seconds of conspicuous disk latency that tips off an adversary. Because `percolator.db` is already AES-256 encrypted, shredding the 120-byte `percolator.header` renders the database permanently unrecoverable in $< 2\text{ ms}$:

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
        if (File.Exists(path)) File.Delete(path);
    }

    // 4. Purge local cache and staging directories
    var cacheDir = GetPath("cache");
    if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
}
```

### 3.4 Master Password Update ($O(1)$ Re-Wrap Workflow)
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

---

## 4. Threat Model & Security Posture (Lens 3)

| Threat / Vulnerability | Concrete Protocol Defense | Verification Mechanism |
| :--- | :--- | :--- |
| **Forensic Plausible Deniability** | **Twin-Slot Layout**: Both Master and Duress slots are fixed at exactly 48 bytes (32-byte payload + 16-byte Poly1305 tag). When duress is inactive, the slot is filled with uniform CSPRNG random bytes. No flags, hashes, or boolean markers exist on disk. | Forensic entropy unit test compares active vs. dummy duress slots, asserting statistical indistinguishability. |
| **SSD Wear-Leveling & Remanence** | **Instant Key Envelope Destruction**: `percolator.db` is encrypted with the 32-byte AES-256 key $K_{\text{db}}$. Overwriting the 120-byte `percolator.header` with hardware flush (`flushToDisk: true`) permanently destroys $K_{\text{db}}$ in $< 2\text{ ms}$, rendering all physical NAND blocks unrecoverable without observable disk latency. | Unit test executes `ExecuteWipeAsync`, asserting header is overwritten with random bytes and all files deleted. |
| **Timing Side-Channel Attacks** | **Single-KDF Pass**: Argon2id is computed exactly once per candidate attempt ($K_{\text{cand}}$). ChaCha20-Poly1305 trials on Master vs. Duress slots take sub-microsecond time. Sentinel verification uses `CryptographicOperations.FixedTimeEquals`. | Unit test verifies candidate key is derived only once; asserts constant-time equality evaluation. |
| **Coercion & Decoy Safety** | **Silent Decoy Transition**: Entering the duress passphrase executes shredding ($< 20\text{ ms}$) and immediately transitions the UI into a clean first-run setup window (`IdentityGateWindow`) without alerts or abnormal crash logs. | UI integration test verifies `UnlockResult.DuressActivated` transitions directly to decoy setup without crash signatures. |
| **Cold Restart Consistency** | **State Parity**: If the app process is terminated post-duress and restarted, the lack of `percolator.header` causes a cold launch to naturally boot into `DatabaseSetupStatus.Uninitialized`. | End-to-end test asserts fresh process boot displays first-time setup following a duress wipe. |
| **Process Memory Inspection** | **Native C-API Keying**: `MasterKeyHandle` passes raw 32-byte key spans directly to `SQLitePCL.raw.sqlite3_key`, preventing the 64-char hex key from materializing as an immutable GC string; scrubs RAM via `ZeroMemory` on lock. | Unit test asserts `MasterKeyHandle.Dispose()` zeroes the key span and marks the instance as disposed. |
| **Cross-Passphrase Collision** | **Domain Invariant**: Duress passphrase cannot match the master passphrase (`VaultPassphrase.ConstantTimeEquals`). | Unit test verifies `ConfigureDuressPasswordAsync` returns failure if duress passphrase matches master. |

---

## 5. High-Value Unit Testing Matrix

| Test Scenario | Input / State | Expected Observable Behavior | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **First-Time Setup (With Duress)** | Master password + Duress password | Creates `DatabaseHeader` with valid master slot and encrypted duress slot; returns session | Asserts result success; verifies header file exists |
| **First-Time Setup (Without Duress)** | Master password only | Creates `DatabaseHeader` with valid master slot and dummy random duress slot; returns session | Asserts dummy slot length == 48 bytes |
| **Normal Unlock** | Valid master password | Unlocks master slot, canary passes, migrations execute, returns `UnlockResult.Success(session)` | Asserts session is non-null and ready |
| **Duress Unlock Triggered** | Valid duress password | Decrypts duress slot, executes `IDuressWipeHandler`, deletes database/header files, returns `UnlockResult.DuressActivated` | Verifies disk files are deleted and status is `Uninitialized` |
| **Invalid Passphrase Attempt** | Incorrect password | Fails both master and duress slots, returns `UnlockResult.InvalidCredentials`, zero wipe occurs | Asserts result type; verifies database files remain intact |
| **Canary Error Verification** | Corrupted database file or bad key | Traps `SqliteException` with `SqliteErrorCode == 26`, returns failure without crashing | Tests explicit `SQLITE_NOTADB` error trap |
| **Master Password Change** | Current password + New password | Re-wraps master slot with new key; database remains fully readable; subsequent unlock requires new password | Tests end-to-end master password re-wrap |
| **Duress Update in Session** | Authenticated session sets new duress key | Re-encrypts duress slot in `DatabaseHeader`; subsequent duress unlock succeeds with new key | Tests end-to-end configuration transition |
| **Duress Clear in Session** | Authenticated session clears duress key | Overwrites duress slot with random noise; prior duress password no longer triggers wipe | Verifies former duress password returns invalid passphrase |
| **Crypto-Shredding Performance & Removal** | Invoking `ExecuteWipeAsync` | Overwrites header with random bytes + hardware flush, deletes files, purges cache directory in < 50ms | Verifies target files are absent post-execution |

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
