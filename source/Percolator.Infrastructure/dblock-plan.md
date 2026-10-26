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
  - Binary header format using Protocol Buffers (`DatabaseHeader.proto`).
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

## 2. Cross-Layer Architecture & Solution Placement

The vault and encryption subsystem is structured across existing solution projects according to Onion Architecture principles:

```
01 - Domain (Percolator.Domain):
└── Security/
    ├── ValueObjects/
    │   ├── MasterPassword.cs          # ISensitiveSecret: validated passphrase buffer with zeroization
    │   ├── DuressPassword.cs          # ISensitiveSecret: non-empty, non-whitespace invariant
    │   ├── Argon2Salt.cs              # [ByteArray(16)] cryptographic salt
    │   ├── EncryptedSlotPayload.cs    # [ByteArray(48)] 32-byte ciphertext + 16-byte Poly1305 auth tag
    │   ├── DatabaseEncryptionKey.cs   # [ByteArray(32)] ISensitiveSecret raw database key
    │   ├── MasterKeyPragma.cs         # Sanitized hex pragma string: PRAGMA key = "x'<hex>'"
    │   ├── UnlockOutcome.cs           # Discriminated outcome: Success, DuressDecoyActivated, InvalidPassphrase
    │   ├── DatabaseSetupStatus.cs     # State enum: Uninitialized, Locked, Unlocked
    │   ├── HeaderPresenceStatus.cs    # State enum: Present, Absent
    │   └── HeaderVersion.cs           # Enum: V1 = 1
    └── Ports/
        └── IMasterKeyProtector.cs     # Pure in-memory key protector port (visitor pattern, ISensitiveSecret)

02 - Application (Percolator.Application):
├── Ports/                             # Driving and driven vault ports
│   ├── IDatabaseUnlockCoordinator.cs # Primary driving port for vault lifecycle & unlock coordination
│   ├── IAuthenticatedSession.cs      # Token/handle to Stage 2 container
│   ├── IDuressWipeHandler.cs         # Driven port: multi-phase crypto-shredding orchestrator
│   └── IHeaderStorage.cs             # Driven port: reads/writes binary header payload
└── Vault/
    ├── DatabaseHeaderPayload.cs      # Header DTO (strongly typed value objects, zero naked primitives)
    └── DecoyTransitionSignal.cs      # Signal sent to UI upon duress wipe completion

03 - Infrastructure (Percolator.Infrastructure):
├── Protos/
│   └── DatabaseHeader.proto          # Protobuf schema for twin-slot header
├── Vault/
│   ├── ProtobufHeaderStorage.cs      # Implements IHeaderStorage using DatabaseHeader.proto
│   ├── DuressWipeHandler.cs          # Implements IDuressWipeHandler (overwrites header & DB files)
│   ├── EphemeralMasterKeyProtector.cs# Implements IMasterKeyProtector (in-memory protected key span)
│   └── SqlCipherDatabaseUnlockCoordinator.cs # Implements IDatabaseUnlockCoordinator
└── Persistence/
    ├── PercolatorDbContext.cs        # Stage 2 post-unlock EF Core DbContext
    ├── SqlCipherPragmaInterceptor.cs # Pooled connection PRAGMA key interceptor
    ├── SqlCipherCanaryProbe.cs       # Isolated probe connection PRAGMA validator
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

---

## 2.2 Domain Value Objects & Ports (`Percolator.Domain.Security`)

### Value Objects (`Percolator.Domain.Security.ValueObjects`)

All value objects enforce structural immutability, explicit factory construction, and **zero implicit or explicit cast operators**. Sensitive memory buffers implement `ISensitiveSecret` to guarantee deterministic memory zeroization.

#### `MasterPassword`
```csharp
namespace Percolator.Domain.Security.ValueObjects;

using Percolator.Domain.Common;

public sealed class MasterPassword : ISensitiveSecret
{
    private char[] _chars;
    public bool IsZeroized { get; private set; }
    public bool IsEmpty => _chars.Length == 0;

    private MasterPassword(char[] chars) => _chars = chars;

    public static MasterPassword Create(string? input) => 
        new(input?.ToCharArray() ?? Array.Empty<char>());

    public static MasterPassword Empty => new(Array.Empty<char>());

    public void WithCharSpan(Action<ReadOnlySpan<char>> action)
    {
        ObjectDisposedException.ThrowIf(IsZeroized, this);
        action(_chars.AsSpan());
    }

    public T WithCharSpan<T>(Func<ReadOnlySpan<char>, T> func)
    {
        ObjectDisposedException.ThrowIf(IsZeroized, this);
        return func(_chars.AsSpan());
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

#### `DuressPassword`
```csharp
namespace Percolator.Domain.Security.ValueObjects;

using Percolator.Domain.Common;

public sealed class DuressPassword : ISensitiveSecret
{
    private char[] _chars;
    public bool IsZeroized { get; private set; }

    private DuressPassword(char[] chars) => _chars = chars;

    public static Result<DuressPassword> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result.Failure<DuressPassword>("Duress passphrase cannot be empty or whitespace.");
        }
        return Result.Success(new DuressPassword(input.ToCharArray()));
    }

    public void WithCharSpan(Action<ReadOnlySpan<char>> action)
    {
        ObjectDisposedException.ThrowIf(IsZeroized, this);
        action(_chars.AsSpan());
    }

    public T WithCharSpan<T>(Func<ReadOnlySpan<char>, T> func)
    {
        ObjectDisposedException.ThrowIf(IsZeroized, this);
        return func(_chars.AsSpan());
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

#### Fixed-Length Byte Value Objects (Generated via `[ByteArray]`)
```csharp
namespace Percolator.Domain.Security.ValueObjects;

using Percolator.Domain.Common;
using Percolator.SourceGenerators;

/// <summary>16-byte random salt for Argon2id key derivation.</summary>
[ByteArray(length: 16)]
public sealed partial record Argon2Salt;

/// <summary>48-byte AEAD envelope payload (32-byte payload + 16-byte Poly1305 authentication tag).</summary>
[ByteArray(length: 48)]
public sealed partial record EncryptedSlotPayload;

/// <summary>32-byte raw AES-256-CBC database encryption key.</summary>
[ByteArray(length: 32)]
public sealed partial record DatabaseEncryptionKey : ISensitiveSecret
{
    public void Zeroize()
    {
        if (MemoryMarshal.TryGetArray(_value, out var segment) && segment.Array != null)
        {
            CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
        }
    }

    public void Dispose() => Zeroize();
}
```

#### `MasterKeyPragma`
```csharp
namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Encapsulates a validated, sanitized hex PRAGMA key statement.
/// Prevents SQL injection and raw string manipulation in database connection interceptors.
/// </summary>
public sealed record MasterKeyPragma
{
    public string PragmaStatement { get; }

    private MasterKeyPragma(string statement) => PragmaStatement = statement;

    public static MasterKeyPragma Create(ReadOnlySpan<byte> raw32Bytes)
    {
        if (raw32Bytes.Length != 32)
        {
            throw new ArgumentException("Master key must be exactly 32 bytes.", nameof(raw32Bytes));
        }
        return new MasterKeyPragma($"PRAGMA key = \"x'{Convert.ToHexString(raw32Bytes)}'\";");
    }
}
```

#### Domain State Enums
```csharp
namespace Percolator.Domain.Security.ValueObjects;

public enum UnlockOutcome
{
    Success = 0,
    DuressDecoyActivated = 1,
    InvalidPassphrase = 2
}

public enum DatabaseSetupStatus
{
    Uninitialized = 0,
    Locked = 1,
    Unlocked = 2
}

public enum HeaderPresenceStatus
{
    Present = 0,
    Absent = 1
}

public enum HeaderVersion
{
    V1 = 1
}
```

#### Domain Port (`Percolator.Domain.Security.Ports`)
```csharp
namespace Percolator.Domain.Security.Ports;

using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

public interface IMasterKeyProtector : ISensitiveSecret
{
    MasterKeyPragma GetPragmaLiteral();
    void WithKeySpan(Action<ReadOnlySpan<byte>> action);
    T WithKeySpan<T>(Func<ReadOnlySpan<byte>, T> func);
}
```

---

## 2.3 Application Ports & Models (`Percolator.Application`)

### `IDatabaseUnlockCoordinator` (`Percolator.Application.Ports`)

The coordinator interface eliminates control-coupling boolean flags and nullable doublets by providing explicit, intent-revealing methods:

```csharp
namespace Percolator.Application.Ports;

using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

public interface IDatabaseUnlockCoordinator
{
    DatabaseSetupStatus Status { get; }
    IAuthenticatedSession? ActiveSession { get; }

    /// <summary>
    /// Checks disk for existing header and database files to determine initial setup state.
    /// </summary>
    Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Initializes a new database with only a master password (duress slot padded with dummy random noise).
    /// </summary>
    Task<Result<IAuthenticatedSession>> InitializeAsync(
        MasterPassword masterPassword, 
        CancellationToken ct = default);

    /// <summary>
    /// Initializes a new database with both a master password and an emergency duress password.
    /// </summary>
    Task<Result<IAuthenticatedSession>> InitializeWithDuressAsync(
        MasterPassword masterPassword, 
        DuressPassword duressPassword, 
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates candidate password against master slot and duress slot.
    /// If master matches: runs canary probe, builds Stage 2 container, runs migrations, sets ActiveSession, returns Success.
    /// If duress matches: executes crypto-shredding, purges disk files, returns DuressDecoyActivated.
    /// If neither matches: returns InvalidPassphrase.
    /// </summary>
    Task<Result<UnlockOutcome>> UnlockAsync(MasterPassword candidatePassword, CancellationToken ct = default);

    /// <summary>
    /// Updates the master password within an active authenticated session by re-wrapping the database key in the master slot.
    /// Does not require re-encrypting the underlying SQLite database file (O(1) operation).
    /// </summary>
    Task<Result> ChangeMasterPasswordAsync(
        MasterPassword currentPassword, 
        MasterPassword newPassword, 
        CancellationToken ct = default);

    /// <summary>
    /// Sets or updates the emergency duress password within an active authenticated session.
    /// </summary>
    Task<Result> SetDuressPasswordAsync(DuressPassword duressPassword, CancellationToken ct = default);

    /// <summary>
    /// Clears the duress password and overwrites the duress slot with cryptographically secure dummy noise.
    /// </summary>
    Task<Result> ClearDuressPasswordAsync(CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops workers, flushes connection pools, zeroizes master key, disposes AuthenticatedHost.
    /// </summary>
    Task LockAsync();
}
```

### `IDuressWipeHandler` (`Percolator.Application.Ports`)
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

### `IHeaderStorage` (`Percolator.Application.Ports`)
```csharp
namespace Percolator.Application.Ports;

using Percolator.Application.Vault;
using Percolator.Domain.Security.ValueObjects;

public interface IHeaderStorage
{
    Task<HeaderPresenceStatus> CheckPresenceAsync(CancellationToken ct = default);
    Task<Result<DatabaseHeaderPayload>> ReadHeaderAsync(CancellationToken ct = default);
    Task<Result> WriteHeaderAsync(DatabaseHeaderPayload header, CancellationToken ct = default);
    Task<Result> OverwriteAndWipeAsync(CancellationToken ct = default);
}
```

### `DatabaseHeaderPayload` (`Percolator.Application.Vault`)
```csharp
namespace Percolator.Application.Vault;

using Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Domain-level payload representation of the twin-slot header.
/// Encapsulates all cryptographic slot material in strongly typed value objects with zero naked primitives.
/// </summary>
public sealed record DatabaseHeaderPayload(
    HeaderVersion Version,
    Argon2Salt Salt,
    AeadNonce MasterSlotNonce,
    EncryptedSlotPayload MasterSlotPayload,
    AeadNonce DuressSlotNonce,
    EncryptedSlotPayload DuressSlotPayload);
```

### `IAuthenticatedSession` (`Percolator.Application.Ports`)
```csharp
namespace Percolator.Application.Ports;

public interface IAuthenticatedSession : IAsyncDisposable
{
    IServiceProvider Services { get; }
}
```

---

## 3. Workflow Decomposition & Orchestration

The unlock and vault orchestration is decomposed into discrete, observable pipeline phases:

```
[UnlockAsync Workflow]
  │
  ├─ Phase 1: Header Loading (IHeaderStorage.ReadHeaderAsync)
  │     Reads percolator.header and extracts strongly typed DatabaseHeaderPayload.
  │
  ├─ Phase 2: Key Derivation & Slot Decryption
  │     Derives Kcand via Argon2id(candidatePassword, header.Salt).
  │     │
  │     ├─ Branch A: Master Slot Evaluation
  │     │     ChaCha20Poly1305.Decrypt(MasterSlotNonce, MasterSlotPayload, Kcand)
  │     │     ├─ Success -> Master Key (DatabaseEncryptionKey) recovered!
  │     │     │             ├─ Step 3a: Canary Probe: PRAGMA key = "x'<Kdb>'"; SELECT count(*) FROM sqlite_master;
  │     │     │             ├─ Step 3b: Build Authenticated Host (Stage2HostFactory)
  │     │     │             │      Registers EphemeralMasterKeyProtector and EF Core DbContext.
  │     │     │             ├─ Step 3c: Run Migrations (Database.MigrateAsync)
  │     │     │             │      Executes any pending schema migrations dynamically.
  │     │     │             ├─ Step 3d: Store ActiveSession on coordinator
  │     │     │             └─ Step 3e: Return UnlockOutcome.Success
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
  │           │     └─ Step 4c: Return UnlockOutcome.DuressDecoyActivated
  │           │
  │           └─ Auth Failed -> Return UnlockOutcome.InvalidPassphrase
```

### 3.1 Post-Duress Decoy State UI Handoff
When `UnlockOutcome.DuressDecoyActivated` is returned:
1. `UnlockViewModel` receives the decoy signal without displaying an alert or error popup.
2. The coordinator informs `WindowHandoffCoordinator`.
3. The UI smoothly transitions into `IdentityGateWindow` / First-Time Setup mode as a clean, blank slate.
4. An adversary observing the system sees an empty, unconfigured installation.

### 3.2 Master Password Update ($O(1)$ Re-Wrap Workflow)
1. User navigates to Settings $\rightarrow$ Security $\rightarrow$ *Change Master Password*.
2. User provides `currentPassword` and `newPassword`.
3. `ChangeMasterPasswordAsync`:
   - Validates `currentPassword` against active `IMasterKeyProtector` in the authenticated session.
   - Generates a fresh 16-byte `Argon2Salt`.
   - Derives new $K_{\text{new\_master}} = \text{Argon2id}(\text{newPassword}, \text{newSalt})$.
   - Uses `IMasterKeyProtector.WithKeySpan` to retrieve the active 32-byte $K_{\text{db}}$.
   - Re-encrypts $K_{\text{db}}$ using ChaCha20-Poly1305 under $K_{\text{new\_master}}$ with a fresh 12-byte nonce.
   - Preserves or re-encrypts the duress slot under the new salt.
   - Atomically overwrites `percolator.header` via `IHeaderStorage`.
   - Zeroizes temporary keys via `ISensitiveSecret.Zeroize()`.
   - **Performance Guarantee**: Because $K_{\text{db}}$ remains identical, zero SQLite tables or pages are rewritten. Operation completes in $< 200\text{ ms}$ with zero risk of database corruption.

### 3.3 Duress Password Configuration Workflow
1. User navigates to Settings $\rightarrow$ Security $\rightarrow$ *Emergency Duress Passphrase*.
2. User sets new duress password via `SetDuressPasswordAsync(duressPassword)` or clears via `ClearDuressPasswordAsync()`.
3. If setting: derives duress key via Argon2id, encrypts sentinel into `EncryptedSlotPayload`, and writes updated `DatabaseHeaderPayload`.
4. If clearing: generates 60 random bytes into `duress_slot_nonce` + `duress_slot_payload` and writes header.
5. Plausible deniability invariant is preserved at all times.

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
- **Zero Headless Coupling**:
  - All vault coordination lives in `Percolator.Application` and `Percolator.Infrastructure`. Headless Linux relay hosts, integration tests, and desktop clients use the exact same secure unlock code without any UI dependency.

---

## 5. High-Value Unit Testing Matrix

| Test Scenario | Input / State | Expected Observable Behavior | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **First-Time Setup (With Duress)** | Master password + Duress password | Creates `DatabaseHeader` with valid master slot and encrypted duress slot; returns session | Asserts result success; verifies header file exists |
| **First-Time Setup (Without Duress)** | Master password only | Creates `DatabaseHeader` with valid master slot and dummy random duress slot; returns session | Asserts dummy slot length == 48 bytes |
| **Normal Unlock** | Valid master password | Unlocks master slot, canary passes, migrations execute, returns `UnlockOutcome.Success` | Asserts successful session creation |
| **Duress Unlock Triggered** | Valid duress password | Decrypts duress slot, executes `IDuressWipeHandler`, deletes database/header files, returns `UnlockOutcome.DuressDecoyActivated` | Verifies disk files are deleted and status is `Uninitialized` |
| **Invalid Passphrase Attempt** | Incorrect password | Fails both master and duress slots, returns `UnlockOutcome.InvalidPassphrase`, zero wipe occurs | Asserts outcome enum; verifies database files remain intact |
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
    - `IHeaderStorage` $\rightarrow$ `ProtobufHeaderStorage`
    - `IDuressWipeHandler` $\rightarrow$ `DuressWipeHandler`
  - *Authenticated Container*:
    - `IMasterKeyProtector` $\rightarrow$ `EphemeralMasterKeyProtector`
    - `DbConnectionInterceptor` $\rightarrow$ `SqlCipherPragmaInterceptor`
    - `IAuthenticatedSession` $\rightarrow$ `AuthenticatedSession`
