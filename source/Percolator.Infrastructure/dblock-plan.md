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
- **$O(1)$ Master Password Envelope Re-Wrapping**:
  - Because the database encryption key $K_{\text{db}}$ is stored wrapped in the Master Slot using $K_{\text{master}} = \text{Argon2id}(\text{passphrase}, \text{salt})$, changing the master password in an authenticated session does **not** require re-encrypting the underlying SQLite database file (`PRAGMA rekey`).
  - Changing the master password is an instantaneous $O(1)$ operation: derive new $K_{\text{master}}$, re-encrypt the 32-byte $K_{\text{db}}$, and atomically rewrite `percolator.header`.
- **Argon2id Key Derivation**: Derive 256-bit keys from passphrases using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$) salted with the 16-byte random salt from `percolator.header`.
- **Canary Validation**: Verify the derived master key on an isolated probe connection using `SELECT count(*) FROM sqlite_master;` before initializing EF Core or connection pools.
- **Dynamic Post-Canary Schema Migrations**: Execute EF Core / SQLite migrations (`Database.MigrateAsync()`) immediately post-canary prior to resolving application repositories.
- **Pooled Connection Key Interception**: Apply `PRAGMA key = "x'<hex_key>'\";` and WAL pragmas transparently to every pooled SQLite connection via a `DbConnectionInterceptor`.
- **Explicit Unlock for Blank Passwords**: Support empty master password (`""`) with mandatory first-launch UI warning confirmation, and require explicit interaction ("Unlock" click) on every subsequent launch.
- **Orderly In-Session Relock & Memory Scrubbing**: Provide graceful teardown via `LockAsync`: stop all `IHostedService` instances, flush SQLite pools (`SqliteConnection.ClearAllPools()`), zeroize raw key bytes via `CryptographicOperations.ZeroMemory`, dispose `AuthenticatedHost`, and return UI to `UnlockWindow`.

### 1.2 Explicit Non-Goals
- **No Presentation Layer Business Logic**: All database unlocking, envelope decryption, key derivation, and wipe orchestration live in `Percolator.Application` and `Percolator.Infrastructure`. The `Percolator.Desktop` UI layer is strictly a thin presentation client.
- **No Disk Indicators of Duress Configuration**: No separate `duress.hash` or marker files on disk. The header schema and file size remain completely invariant.
- **No Multi-Database Partitioning**: All domain aggregates share `percolator.db` to eliminate redundant KDF overhead and guarantee atomic schema migrations.
- **No Master Password Persistence**: The master passphrase is never cached on disk, DPAPI, or OS keychain without explicit user unlock.
- **No Repository Resolution in Bootstrap Container**: No repository or DbContext may be registered or resolved in the root DI container.

---

## 2. Vertical Feature Slice Architecture Across Onion Layers

Rather than grouping components into horizontal technical folders, the database lock and vault subsystem is structured as a vertical feature slice crossing the architectural layers:

```
01 - Domain (Percolator.Domain):
└── Security/
    └── Vault/
        ├── MasterPassword.cs          # Value object: passphrase validation & memory protection
        ├── DuressPassword.cs          # Value object: non-empty, non-whitespace invariant
        ├── Argon2Salt.cs              # Value object: 16-byte cryptographic salt
        ├── MasterKeyPragma.cs         # Value object: sanitized hex pragma string
        ├── UnlockOutcome.cs           # Discriminated outcome: Success, DuressDecoyActivated, InvalidPassphrase
        ├── DatabaseSetupStatus.cs     # State enum: Uninitialized, Locked, Unlocked
        ├── HeaderPresenceStatus.cs    # State enum: Present, Absent
        └── IMasterKeyProtector.cs     # Pure in-memory key protector port (visitor pattern)

02 - Application (Percolator.Application):
└── Security/
    └── Vault/
        ├── Ports/
        │   ├── IDatabaseUnlockCoordinator.cs # High-level vault lifecycle & session coordination
        │   ├── IAuthenticatedSession.cs      # Token/handle to Stage 2 container
        │   ├── IHeaderStorage.cs             # Reads/writes DatabaseHeader
        │   ├── ISlotCipherService.cs         # Derives keys & encrypts/decrypts slot envelopes
        │   ├── ICanaryProbeService.cs        # Verifies key on isolated probe connection
        │   ├── IDuressWipeHandler.cs         # Multi-phase crypto-shredding orchestrator
        │   └── IAuthenticatedHostFactory.cs  # Creates Stage 2 IHost & executes migrations
        └── UseCases/
            ├── UnlockDatabaseUseCase.cs
            ├── InitializeMasterPasswordUseCase.cs
            ├── ChangeMasterPasswordUseCase.cs
            ├── ConfigureDuressPasswordUseCase.cs
            └── LockDatabaseUseCase.cs

03 - Infrastructure (Percolator.Infrastructure):
└── Security/
    └── Vault/
        ├── Protos/
        │   └── DatabaseHeader.proto          # Protobuf schema for twin-slot header
        ├── Storage/
        │   └── ProtobufHeaderStorage.cs      # Protobuf header read/write adapter
        ├── Cryptography/
        │   ├── Argon2idKeyDeriver.cs         # Argon2id KDF adapter
        │   ├── ChaCha20Poly1305SlotCipher.cs # ChaCha20-Poly1305 slot encryption adapter
        │   └── EphemeralMasterKeyProtector.cs# In-memory DPAPI/protected memory key protector
        ├── Persistence/
        │   ├── SqlCipherCanaryProbe.cs       # Raw probe connection PRAGMA test
        │   ├── SqlCipherPragmaInterceptor.cs # Pooled connection PRAGMA key interceptor
        │   ├── Stage2HostFactory.cs          # Constructs Stage 2 Host & runs migrations
        │   └── PercolatorDbContext.cs        # Post-unlock EF Core DbContext
        ├── Duress/
        │   └── DuressWipeHandler.cs          # Multi-pass crypto-shredding adapter
        └── Coordination/
            └── SqlCipherDatabaseUnlockCoordinator.cs # Application port implementation

05 - Desktop (Percolator.Desktop):
└── Features/
    └── Security/
        ├── Unlock/
        │   ├── UnlockView.axaml
        │   ├── UnlockView.axaml.cs
        │   └── UnlockViewModel.cs            # Thin presentation: delegates to IDatabaseUnlockCoordinator
        ├── Setup/
        │   ├── MasterPasswordSetupView.axaml
        │   └── MasterPasswordSetupViewModel.cs
        ├── Settings/
        │   ├── SecuritySettingsView.axaml
        │   └── SecuritySettingsViewModel.cs  # Invokes ChangeMasterPassword & ConfigureDuressPassword
        └── Navigation/
            └── WindowHandoffCoordinator.cs   # Manages UnlockWindow -> MainWindow -> Decoy transitions
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

### 2.2 Domain Contracts & Value Objects (`Percolator.Domain.Security.Vault`)

* **`MasterPassword`**: Encapsulates user passphrase (empty string permitted). Provides zeroized buffer extraction.
* **`DuressPassword`**: Encapsulates optional duress passphrase. Invariant: must be non-empty, non-whitespace, and cannot match master password.
* **`Argon2Salt`**: 16-byte cryptographically secure salt.
* **`UnlockOutcome`**: Explicit discriminated outcome of an unlock attempt:
  ```csharp
  namespace Percolator.Domain.Security.Vault;

  public enum UnlockOutcome
  {
      Success = 0,
      DuressDecoyActivated = 1,
      InvalidPassphrase = 2
  }
  ```
* **`DatabaseSetupStatus`**:
  ```csharp
  namespace Percolator.Domain.Security.Vault;

  public enum DatabaseSetupStatus
  {
      Uninitialized = 0,
      Locked = 1,
      Unlocked = 2
  }
  ```
* **`HeaderPresenceStatus`**:
  ```csharp
  namespace Percolator.Domain.Security.Vault;

  public enum HeaderPresenceStatus
  {
      Present = 0,
      Absent = 1
  }
  ```
* **`IMasterKeyProtector`**: Pure in-memory key protector port:
  ```csharp
  namespace Percolator.Domain.Security.Vault;

  public interface IMasterKeyProtector : IDisposable
  {
      MasterKeyPragma GetPragmaLiteral();
      void WithKeySpan(Action<ReadOnlySpan<byte>> action);
      T WithKeySpan<T>(Func<ReadOnlySpan<byte>, T> func);
  }
  ```

### 2.3 Application Ports (`Percolator.Application.Security.Vault.Ports`)

#### `IDatabaseUnlockCoordinator`
```csharp
namespace Percolator.Application.Security.Vault.Ports;

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
        MasterPassword masterPassword, 
        DuressPassword? duressPassword = null, 
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates candidate password against master slot and duress slot.
    /// If master matches: runs migrations and returns active session.
    /// If duress matches: executes crypto-shredding and returns duress decoy signal.
    /// </summary>
    Task<Result<UnlockResult>> UnlockAsync(MasterPassword candidatePassword, CancellationToken ct = default);

    /// <summary>
    /// Updates the master password within an active authenticated session by re-wrapping the database key in the master slot.
    /// Does not require re-encrypting the underlying SQLite database file (O(1) operation).
    /// </summary>
    Task<Result> ChangeMasterPasswordAsync(
        MasterPassword currentPassword, 
        MasterPassword newPassword, 
        CancellationToken ct = default);

    /// <summary>
    /// Updates or clears duress password within an active authenticated session.
    /// </summary>
    Task<Result> ConfigureDuressPasswordAsync(DuressPassword? duressPassword, CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops workers, flushes connection pools, zeroizes master key, disposes AuthenticatedHost.
    /// </summary>
    Task LockAsync();
}
```

#### `IDuressWipeHandler`
```csharp
namespace Percolator.Application.Security.Vault.Ports;

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

#### `IHeaderStorage`
```csharp
namespace Percolator.Application.Security.Vault.Ports;

public interface IHeaderStorage
{
    Task<HeaderPresenceStatus> CheckPresenceAsync(CancellationToken ct = default);
    Task<Result<DatabaseHeaderPayload>> ReadHeaderAsync(CancellationToken ct = default);
    Task<Result> WriteHeaderAsync(DatabaseHeaderPayload header, CancellationToken ct = default);
    Task<Result> OverwriteAndWipeAsync(CancellationToken ct = default);
}
```

---

## 3. Workflow Decomposition & Orchestration

The unlock and vault orchestration is decomposed into discrete, composable steps:

```
[UnlockAsync Workflow]
  │
  ├─ 1. Header Retrieval (IHeaderStorage.ReadHeaderAsync)
  │     Reads percolator.header and extracts salt + twin slots.
  │
  ├─ 2. Key Derivation (ISlotCipherService.DeriveCandidateKey)
  │     Derives Kcand via Argon2id(candidatePassword, header.Salt).
  │
  ├─ 3. Master Slot Evaluation (ISlotCipherService.TryDecryptMasterSlot)
  │     ChaCha20Poly1305.Decrypt(MasterSlotNonce, MasterSlotPayload, Kcand)
  │     ├─ Success -> Master Key (Kdb) recovered!
  │     │             ├─ 3a. Probe Canary (ICanaryProbeService.ProbeAsync)
  │     │             │      PRAGMA key = "x'<Kdb>'"; SELECT count(*) FROM sqlite_master;
  │     │             ├─ 3b. Build Authenticated Host (IAuthenticatedHostFactory.CreateHostAsync)
  │     │             │      Registers EphemeralMasterKeyProtector and EF Core DbContext.
  │     │             ├─ 3c. Run Migrations (Database.MigrateAsync)
  │     │             │      Executes any pending schema migrations dynamically.
  │     │             └─ 3d. Return UnlockResult.AsSuccess(session)
  │     │
  │     └─ Auth Failed -> Proceed to Duress Check (Step 4)
  │
  ├─ 4. Duress Slot Evaluation (ISlotCipherService.TryDecryptDuressSlot)
  │     ChaCha20Poly1305.Decrypt(DuressSlotNonce, DuressSlotPayload, Kcand)
  │     ├─ Success & Payload == "PERCOLATOR_DURESS_SENTINEL_OK" ->
  │     │     ├─ 4a. Trigger Crypto-Shredding (IDuressWipeHandler.ExecuteWipeAsync)
  │     │     │      ├─ Overwrite percolator.header with random bytes & flush
  │     │     │      ├─ Overwrite percolator.db, wal, shm with random bytes & delete
  │     │     │      └─ Purge avatar/attachment cache directories
  │     │     ├─ 4b. Status = DatabaseSetupStatus.Uninitialized
  │     │     └─ 4c. Return UnlockResult.AsDuressDecoy()
  │     │
  │     └─ Auth Failed -> Return Result.Failure("Invalid passphrase")
```

### 3.1 Post-Duress Decoy State UI Handoff
When `UnlockResult.AsDuressDecoy()` is returned:
1. `UnlockViewModel` receives the decoy signal without displaying an alert or error popup.
2. The coordinator informs `IWindowHandoffCoordinator`.
3. The UI smoothly transitions into `IdentityGateWindow` / First-Time Setup mode as a clean, blank slate.
4. The adversary observes an empty, unconfigured installation.

### 3.2 Master Password Update ($O(1)$ Re-Wrap)
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
   - Zeroizes temporary keys.
   - **Performance Guarantee**: Because $K_{\text{db}}$ remains identical, zero SQLite tables or pages are rewritten. Operation completes in $< 200\text{ ms}$ with zero risk of database corruption.

### 3.3 Duress Password Configuration within Authenticated Session
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
- **Zero Headless Coupling**:
  - All vault coordination lives in `Percolator.Application` and `Percolator.Infrastructure`. Headless Linux relay hosts, integration tests, and desktop clients use the exact same secure unlock code.

---

## 5. High-Value Unit Testing Matrix

| Test Scenario | Input / State | Expected Observable Behavior | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **First-Time Setup (With Duress)** | Master password + Duress password | Creates `DatabaseHeader` with valid master slot and encrypted duress slot; returns session | Asserts result success; verifies header file exists |
| **First-Time Setup (Without Duress)** | Master password only | Creates `DatabaseHeader` with valid master slot and dummy random duress slot; returns session | Asserts dummy slot length == 48 bytes |
| **Normal Unlock** | Valid master password | Unlocks master slot, canary passes, migrations execute, returns `UnlockOutcome.Success` | Asserts successful session creation |
| **Duress Unlock Triggered** | Valid duress password | Decrypts duress slot, executes `IDuressWipeHandler`, deletes database/header files, returns `UnlockOutcome.DuressDecoyActivated` | Verifies disk files are deleted and status is `Uninitialized` |
| **Invalid Passphrase Attempt** | Incorrect password | Fails both master and duress slots, returns `Result.Failure("Invalid passphrase")`, zero wipe occurs | Asserts failure result; verifies database files remain intact |
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
    - `ISlotCipherService` $\rightarrow$ `ChaCha20Poly1305SlotCipher`
    - `ICanaryProbeService` $\rightarrow$ `SqlCipherCanaryProbe`
    - `IDuressWipeHandler` $\rightarrow$ `DuressWipeHandler`
    - `IAuthenticatedHostFactory` $\rightarrow$ `Stage2HostFactory`
  - *Authenticated Container*:
    - `IMasterKeyProtector` $\rightarrow$ `EphemeralMasterKeyProtector`
    - `DbConnectionInterceptor` $\rightarrow$ `SqlCipherPragmaInterceptor`
    - `IAuthenticatedSession` $\rightarrow$ `AuthenticatedSession`
