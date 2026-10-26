# Percolator.Infrastructure Implementation Plan

## Refinement Status Gate
- [ ] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [ ] Implementation Gate: LOCKED (Awaiting Final User Confirmation of Chunk Execution Order)

---

## 1. Overview & Architectural Role

`Percolator.Infrastructure` provides concrete technical adapters, persistent storage repositories, network transport clients/servers, cryptographic primitives, and binary serialization engines for the Percolator microkernel architecture.

Following Clean Architecture & Onion Architecture principles:
- **Target Framework**: Targets standard `net10.0` with **zero Windows-specific, macOS-specific, or platform-locked dependencies**. Runs uniformly on headless Linux Docker containers (relay hosts), macOS, and cross-platform desktop runtimes.
- **Dependencies Flow Inward**: `Percolator.Infrastructure` references `Percolator.Domain`, `Percolator.Domain.Kernel`, `Percolator.Application`, and `Percolator.PluginSdk`. It does NOT expose infrastructure-specific types (e.g., SQLite connections, gRPC stubs, Protobuf classes, raw sockets) to the domain or application layers.
- **Port Realization & Swappability**: Every component in this project implements an interface (port) defined by `Percolator.Domain`, `Percolator.Application`, `Percolator.PluginSdk`, or application plugins (`Percolator.Apps.*`).
- **Two-Stage Container Separation**: Supports the Two-Stage DI architecture:
  - *Stage 1 (Bootstrap Container)*: Pre-unlock environment containing zero SQLite DbContexts, zero repositories, and zero background worker instances. Registers `SQLitePCL.Batteries_V2.Init()`, `SignalCryptoEngine`, `SystemDateTimeProvider`, `ProtobufHeaderStorage`, `DuressWipeHandler`, and `SqlCipherDatabaseUnlockCoordinator`.
  - *Stage 2 (Authenticated Host - `IHost`)*: Post-unlock environment built by `Stage2HostFactory`. Hosts `IMasterKeyHandle` (scoped to session in pinned unmanaged memory), `SqlCipherPragmaInterceptor`, `PercolatorDbContext` (scoped/pooled), all 57 domain repositories/query services, and `IHostedService` background workers.
- **Clear Platform Boundary (`Percolator.Infrastructure.Windows`)**:
  - Core `Percolator.Infrastructure` provides complete, 100% portable baseline implementations.
  - Platform-specific optimizations are decoupled into `Percolator.Infrastructure.Windows` (`net10.0-windows`) and swapped in via dependency injection.
  - **Explicit Non-Goals**:
    - **No Mac or Linux-specific implementations**: No macOS Keychain, Apple APNS, Linux libsecret, or D-Bus adapters.
    - **No low-value Windows implementations**: No Windows Registry key stores, no WMI hardware detectors, no Windows Event Log sinks, no Named Pipe IPC transports.
    - **Zero DPAPI Master Key Persistence**: In accordance with the Lens 3 strict threat model, the database master key $K_{\text{db}}$ and user passphrases are **never** persisted to disk, Windows DPAPI, or OS keychain. The key exists solely in pinned unmanaged memory via `IMasterKeyHandle` for the active duration of the authenticated session.
    - **Only 2 high-value targeted Windows adapters are supported**:
      1. `WindowsSChannelCertificateProvider`: Temporary PKCS#12 `.pfx` on-disk export with CNG key deletion and antivirus lock retries (required because Windows SChannel rejects purely in-memory certificates in TLS handshakes).
      2. `WindowsSparseDesktopFileStore`: Win32 `FSCTL_SET_SPARSE` (`DeviceIoControl` `0x000900C4`) for instant zero-write file pre-allocation on NTFS.

---

## 2. Core Technical Invariants & Guardrails

1. **Zero Domain Logic in Infrastructure**: Infrastructure adapters must solely translate between external APIs/data formats and domain value objects / entities.
2. **Zero Plaintext Sensitive State at Rest**: All private keys, ratchet chain keys, and session secrets stored on disk must be encrypted using SQLCipher and/or master secret key derivation. All database records live in AES-256-CBC encrypted `percolator.db` keyed by the wrapped 32-byte master key $K_{\text{db}}$. Sensitive cryptographic fields must never be stored as unencrypted BLOBs in SQLite.
3. **Deterministic Memory Zeroization**: Any unmanaged buffers or cryptographic key spans (`IMasterKeyHandle`, `VaultPassphrase`, KDF buffers) must be deterministically cleared via `CryptographicOperations.ZeroMemory` when disposed or upon session relock.
4. **Resilient Network Handling**: All gRPC network calls must obey cancellation tokens, transport timeouts, and surface transient vs. permanent network failures cleanly to `OutboxRetryPolicy`.
5. **Cryptographic Logging Guardrails**: Honor `CryptographyOptions.EnableCryptographicMaterialLogging = false` by default across all infrastructure loggers and diagnostics. Sensitive cryptographic keys, KDF digests, and plaintexts must never be emitted to logs.
6. **Strict Authentication & Metadata Isolation**:
   - **Public Edge / Ingress & Deposit (Sealed Sender & Capability Ingress)**: Unauthenticated (zero sender identity or channel ID leakage on the wire). Relies on recipient-issued `DeliveryToken`s, `ProfileKey`-derived blinded locators, and Sealed Sender cryptography. Relays see strictly routing tokens and blinded capability locators.
   - **Mailbox Retrieval, Directory & Management**: Authenticated via challenge-response or signature cryptographic proofs to prevent unauthorized message draining, OPK exhaustion attacks, and mass directory scraping (mirroring Signal's security model).
   - **Group Operations**: Protected via Zero-Knowledge presentation proofs against `RelayGroupLedger` epochs without revealing individual member identities to relays.
7. **RFC 4122 UUID Endianness Normalization**: When bridging between .NET `Guid` structures and native cryptographic libraries (`libsignal`, `zkgroup`) or cross-platform network payloads, explicitly normalize byte orders via `Guid.ToByteArray(bigEndian: true)` to avoid Windows little-endian corruption.
8. **Unmanaged Native Callback Threading**: Interop bridge adapters must consume `IDbContextFactory<T>` to spin up short-lived isolated database contexts on unmanaged native callback threads, preventing concurrency clashes and cross-thread context contamination.
9. **Plausible Deniability Invariant**: When no duress password is set, the Duress Slot in `percolator.header` is populated with CSPRNG random noise of identical length (48 bytes: 32-byte dummy ciphertext + 16-byte Poly1305 tag, plus 12-byte nonce). The serialized wire layout and file length (~120 bytes) are 100% indistinguishable to forensic disk analysis regardless of whether duress protection is configured.
10. **Instant Crypto-Shredding Pipeline**: Entering the duress passphrase triggers instant overwriting of `percolator.header` with CSPRNG random bytes and forced physical disk controller flush (`fs.Flush(flushToDisk: true)`) before truncation and deletion ($< 2\text{ ms}$), permanently destroying $K_{\text{db}}$ and rendering the encrypted database unrecoverable. Secondary deletion of DB/WAL/SHM tolerates external OS file locks via `IOException` trapping without leaking failure to the attacker.
11. **Cold Launch Self-Healing Invariant**: If `percolator.header` is missing (e.g., following a duress wipe where `percolator.db` was locked by an external process), `DetermineStatusAsync` returns `DatabaseSetupStatus.Uninitialized`. Upon new setup, `InitializeAsync` force-unlinks any orphaned ciphertext `percolator.db*` files before initializing a clean vault.
12. **Native SQLCipher C-API Keying & Researched Canary Trap**: Apply the 32-byte key span directly to unmanaged handles via `SQLitePCL.raw.sqlite3_key`, eliminating managed string allocations of sensitive key material. Verify key validity on an isolated probe connection executing `SELECT count(*) FROM sqlite_master;`, explicitly trapping native SQLite error code **`26` (`SQLITE_NOTADB`)**.
13. **$O(1)$ Master Password Envelope Re-Wrapping**: Master password changes re-derive the Argon2id wrapping key, retrieve $K_{\text{db}}$ via `IMasterKeyHandle.WithKeySpan`, and re-encrypt the 48-byte master slot with a fresh nonce, retaining the salt and duress slot. No SQLite database re-encryption (`PRAGMA rekey`) or table rewriting occurs.

---

## 3. Deep Technical Specifications

### 3.1 Unified Encrypted SQLite Database Schema (`percolator.db`)

Local storage is unified into a single SQLCipher-encrypted SQLite database file (`percolator.db`). This eliminates 4x connection pool and multi-database migration overhead, ensures atomic transactional integrity across domain aggregates, and utilizes SQLite Write-Ahead Logging (WAL) for lock-free concurrent reads and writes.

```
AppData / Data Directory
├── percolator.db     # Unified SQLCipher AES-256-CBC database with WAL mode
├── percolator.db-wal # SQLite WAL journal
├── percolator.db-shm # SQLite shared memory index
└── percolator.header # Protobuf DatabaseHeader (Argon2id salt, master slot, duress decoy slot)
```

#### Native SQLCipher Keying & Connection Pragmas
Instead of string-interpolated `PRAGMA key = "x'...'"` commands that allocate immutable hex strings in the managed GC heap, keying is performed directly on unmanaged SQLite handles via `SQLitePCL.raw.sqlite3_key` within `SqlCipherPragmaInterceptor`:

```csharp
public sealed class SqlCipherPragmaInterceptor : DbConnectionInterceptor
{
    private readonly IMasterKeyHandle _keyHandle;

    public SqlCipherPragmaInterceptor(IMasterKeyHandle keyHandle) => _keyHandle = keyHandle;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqliteConn)
        {
            // 1. Pass raw 32-byte key span directly to SQLite C-API
            _keyHandle.ApplyKeyToConnection(sqliteConn);

            // 2. Configure SQLite engine parameters
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();
        }
    }
}
```

#### Table Definitions by Subsystem Namespace

##### 1. Operational Outbox & Ingress (`ops_*`)
* `ops_outbox_jobs`:
  - `id` (`TEXT` GUID, PK)
  - `recipient_id` (`BLOB` 32 bytes, indexed)
  - `delivery_route` (`INTEGER`)
  - `payload` (`BLOB`)
  - `status` (`INTEGER`: `Queued=0`, `InFlight=1`, `Delivered=2`, `Failed=3`, `Paused=4`)
  - `attempt_count` (`INTEGER`)
  - `next_retry_at_utc` (`TEXT` ISO 8601, indexed)
  - `created_at_utc` (`TEXT` ISO 8601)
  - `last_error` (`TEXT` nullable)
  - *Composite Index*: `CREATE INDEX ix_ops_outbox_retry ON ops_outbox_jobs(status, next_retry_at_utc);`
* `ops_inbound_queue`:
  - `id` (`TEXT` GUID, PK)
  - `sender_id` (`BLOB` 32 bytes)
  - `app_id` (`INTEGER`)
  - `raw_payload` (`BLOB`)
  - `received_at_utc` (`TEXT` ISO 8601)
  - `processed_at_utc` (`TEXT` nullable)
* `ops_reserved_ports`:
  - `port_number` (`INTEGER` PK)
  - `allocated_at_utc` (`TEXT` ISO 8601)
  - `lease_expiry_utc` (`TEXT` ISO 8601)

##### 2. Cryptographic Vault (`vault_*`)
* `vault_identity_keys`:
  - `id` (`BLOB` 32 bytes PublicIdentityId, PK)
  - `private_key_ciphertext` (`BLOB`, column-encrypted)
  - `public_key` (`BLOB` 32 bytes)
  - `created_at_utc` (`TEXT` ISO 8601)
* `vault_signed_prekeys`:
  - `id` (`INTEGER` PK)
  - `identity_id` (`BLOB` 32 bytes, FK to `vault_identity_keys`)
  - `private_key_ciphertext` (`BLOB`, column-encrypted)
  - `public_key` (`BLOB` 32 bytes)
  - `signature` (`BLOB` 64 bytes)
  - `created_at_utc` (`TEXT` ISO 8601)
* `vault_onetime_prekeys`:
  - `id` (`INTEGER` PK)
  - `identity_id` (`BLOB` 32 bytes, FK to `vault_identity_keys`)
  - `private_key_ciphertext` (`BLOB`, column-encrypted)
  - `public_key` (`BLOB` 32 bytes)
  - `is_consumed` (`INTEGER` boolean, indexed)
* `vault_ratchet_sessions`:
  - `session_id` (`TEXT` GUID, PK)
  - `peer_identity_id` (`BLOB` 32 bytes, indexed)
  - `root_key_ciphertext` (`BLOB`, column-encrypted)
  - `send_chain_key_ciphertext` (`BLOB`, column-encrypted)
  - `recv_chain_key_ciphertext` (`BLOB`, column-encrypted)
  - `ratchet_state_blob` (`BLOB`, column-encrypted serialized ratchet state)
  - `updated_at_utc` (`TEXT` ISO 8601)
* `vault_group_sender_keys`:
  - `group_id` (`TEXT` GUID)
  - `author_identity_id` (`BLOB` 32 bytes)
  - `sender_key_state_ciphertext` (`BLOB`, column-encrypted)
  - `updated_at_utc` (`TEXT` ISO 8601)
  - *Composite PK*: `PRIMARY KEY(group_id, author_identity_id)`

##### 3. Chat History & Timelines (`chat_*`)
* `chat_messages`:
  - `id` (`TEXT` GUID, PK)
  - `channel_id` (`TEXT` GUID, indexed)
  - `sender_id` (`BLOB` 32 bytes)
  - `content` (`TEXT`)
  - `timestamp_utc` (`TEXT` ISO 8601)
  - `delivery_status` (`INTEGER`)
  - `is_edited` (`INTEGER` boolean)
  - `is_deleted` (`INTEGER` boolean)
  - `expires_at_utc` (`TEXT` ISO 8601 nullable, indexed)
  - *Composite Index*: `CREATE INDEX ix_chat_timeline ON chat_messages(channel_id, timestamp_utc);`
  - *Eviction Index*: `CREATE INDEX ix_chat_expiry ON chat_messages(expires_at_utc) WHERE expires_at_utc IS NOT NULL;`
* `chat_reactions`:
  - `message_id` (`TEXT` GUID)
  - `reactor_id` (`BLOB` 32 bytes)
  - `emoji` (`TEXT` 32 chars)
  - `created_at_utc` (`TEXT` ISO 8601)
  - *Composite PK*: `PRIMARY KEY(message_id, reactor_id, emoji)`
* `chat_attachments`:
  - `id` (`TEXT` GUID, PK)
  - `message_id` (`TEXT` GUID, FK to `chat_messages` ON DELETE CASCADE)
  - `blob_sha256` (`BLOB` 32 bytes)
  - `file_name` (`TEXT`)
  - `file_size_bytes` (`INTEGER`)
  - `mime_type` (`TEXT`)
* `chat_group_invitations`:
  - `id` (`TEXT` GUID, PK)
  - `channel_id` (`TEXT` GUID)
  - `inviter_id` (`BLOB` 32 bytes)
  - `invitee_id` (`BLOB` 32 bytes)
  - `status` (`INTEGER`: `Pending=0`, `Accepted=1`, `Declined=2`, `Expired=3`)
  - `created_at_utc` (`TEXT` ISO 8601)
  - `expires_at_utc` (`TEXT` ISO 8601)

##### 4. Swarm File Transfers (`ft_*`)
* `ft_transfer_sessions`:
  - `transfer_id` (`TEXT` GUID, PK)
  - `channel_id` (`TEXT` GUID)
  - `manifest_hash` (`BLOB` 32 bytes)
  - `total_size_bytes` (`INTEGER`)
  - `transferred_size_bytes` (`INTEGER`)
  - `status` (`INTEGER`)
  - `direction` (`INTEGER`: `Inbound=0`, `Outbound=1`)
  - `created_at_utc` (`TEXT` ISO 8601)
* `ft_manifest_files`:
  - `id` (`TEXT` GUID, PK)
  - `transfer_id` (`TEXT` GUID, FK to `ft_transfer_sessions` ON DELETE CASCADE)
  - `relative_path` (`TEXT`)
  - `file_size_bytes` (`INTEGER`)
  - `merkle_root` (`BLOB` 32 bytes)
* `ft_transfer_bitfields`:
  - `transfer_id` (`TEXT` GUID, PK, FK to `ft_transfer_sessions` ON DELETE CASCADE)
  - `bitfield_bytes` (`BLOB`)
  - `total_pieces` (`INTEGER`)
  - `verified_pieces` (`INTEGER`)
* `ft_swarm_endpoints`:
  - `transfer_id` (`TEXT` GUID)
  - `peer_id` (`BLOB` 32 bytes)
  - `endpoint` (`TEXT`)
  - `last_seen_utc` (`TEXT` ISO 8601)
  - *Composite PK*: `PRIMARY KEY(transfer_id, peer_id)`
* `ft_filter_policies`:
  - `peer_id` (`BLOB` 32 bytes, PK)
  - `auto_accept_under_bytes` (`INTEGER`)
  - `is_blocked` (`INTEGER` boolean)

---

### 3.2 Protobuf Binary Header Contract (`DatabaseHeader.proto`)

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

#### Internal Header Storage Contract (`Percolator.Infrastructure.Vault.IHeaderStorage`)

```csharp
namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;

internal interface IHeaderStorage
{
    /// <summary>
    /// Reads and deserializes percolator.header. Returns failure if file is missing or corrupted.
    /// </summary>
    Task<DomainResult<DatabaseHeader>> ReadHeaderAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically writes DatabaseHeader to percolator.header via temporary file flush and replace.
    /// </summary>
    Task<DomainResult> WriteHeaderAsync(DatabaseHeader header, CancellationToken ct = default);

    /// <summary>
    /// Overwrites percolator.header with random bytes before deleting it from disk.
    /// </summary>
    Task<DomainResult> OverwriteAndWipeAsync(CancellationToken ct = default);
}
```

---

### 3.3 Vault Cryptographic Mechanics & Exception Control Flow

#### 3.3.1 ChaCha20-Poly1305 Slot Trials & Exception Control Flow
`SqlCipherDatabaseUnlockCoordinator` derives $K_{\text{cand}} = \text{Argon2id}(\text{passphrase}, \text{salt})$ exactly once, then evaluates the two 48-byte AEAD slots using BCL `System.Security.Cryptography.ChaCha20Poly1305`:

```csharp
// Standard fixed 32-byte sentinel for duress slot verification:
private static readonly byte[] DuressSentinelBytes = 
    SHA256.HashData(Encoding.UTF8.GetBytes("PERCOLATOR_VAULT_DURESS_SENTINEL_V1"));

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

#### 3.3.2 Researched Canary Error Handling (`SQLITE_NOTADB` = 26)
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

#### 3.3.3 Instant Crypto-Shredding Pipeline (`DuressWipeHandler`)
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

#### 3.3.4 Master Password Update ($O(1)$ Re-Wrap Workflow)
1. User navigates to Settings $\rightarrow$ Security $\rightarrow$ *Change Master Password*.
2. User provides `currentPassword` and `newPassword`.
3. `ChangeMasterPasswordAsync`:
   - Validates `currentPassword` against active `IMasterKeyHandle` in the authenticated session.
   - Derives new $K_{\text{new\_master}} = \text{Argon2id}(\text{newPassword}, \text{header.Salt})$.
   - Uses `IMasterKeyHandle.WithKeySpan` to retrieve the active 32-byte $K_{\text{db}}$.
   - Re-encrypts $K_{\text{db}}$ using ChaCha20-Poly1305 under $K_{\text{new\_master}}$ with a fresh 12-byte nonce.
   - **Duress Slot Preservation**: Because `header.Salt` is retained, the existing duress slot remains 100% valid without requiring the user to re-enter their duress passphrase.
   - Atomically overwrites `percolator.header` via `IHeaderStorage`.
   - Zeroizes temporary keys via `CryptographicOperations.ZeroMemory`.
   - **Performance Guarantee**: Because $K_{\text{db}}$ remains identical, zero SQLite tables or pages are rewritten. Operation completes in $< 200\text{ ms}$ with zero risk of database corruption.

---

### 3.4 TCP Multi-Stream Swarm Framing Protocol (0x01–0x0C)

The swarm file transfer engine operates over a binary framing protocol with a fixed 5-byte header:

```
┌───────────────┬──────────────────────────────────┬─────────────────────────────┐
│ Frame Opcode  │ Payload Length (Big-Endian UInt) │        Frame Payload        │
│    (1 Byte)   │            (4 Bytes)             │      (Variable Length)      │
└───────────────┴──────────────────────────────────┴─────────────────────────────┘
```

* Maximum payload size: `262,144 bytes` (256 KB); standard piece chunk size: `65,536 bytes` (64 KB).
* **Frame Definitions**:
  - `0x01` `STREAM_HANDSHAKE`: `[TransferId: 16 B GUID] [PeerIdentityId: 32 B] [SessionAuthToken: 32 B]`
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

---

### 3.5 Dynamic Port Range Probing & gRPC Interceptors

#### Dynamic Port Range Probing (5200–5299)
1. On node activation, query `ops_reserved_ports` in `PercolatorDbContext` to filter out currently leased ports.
2. Probe ports sequentially using `TcpListener`:
   ```csharp
   var listener = new TcpListener(IPAddress.IPv6Any, port);
   listener.Server.DualMode = true;
   listener.Server.ExclusiveAddressUse = true;
   listener.Start();
   ```
3. If `SocketException` (`AddressAlreadyInUse`), advance to the next port.
4. Upon successful bind, commit lease to `ops_reserved_ports` with 30-minute renewal heartbeat.

#### gRPC Interceptors
1. **`IdentityReadinessInterceptor`**:
   - Executes before every incoming RPC.
   - Evaluates `INetworkStealthGate.IsNetworkActive` and checks whether the database is unlocked.
   - If false, short-circuits with `Status(StatusCode.Unavailable, "Node operates in total network stealth. Unlock identity to proceed.")`.
2. **`DeliveryCertificateAuthInterceptor`**:
   - Executes on authenticated relay calls (`authenticated_relay.proto`).
   - Extracts metadata headers: `x-percolator-identity` (hex-encoded `PublicIdentityId`), `x-percolator-timestamp` (Unix ms), and `x-percolator-signature`.
   - Rejects if timestamp delta exceeds $\pm 60\text{ seconds}$ from relay UTC.
   - Verifies Ed25519 signature over `method_path || timestamp_bytes || request_payload_hash`.

---

## 4. Wire Contracts & Protobuf Services (`Protos/`)

Concrete Protobuf services organized strictly by **trust boundary** and **authentication model**:

```
                                  ┌────────────────────────────────────────┐
                                  │      Client / Sender / Peer Node       │
                                  └───────────────────┬────────────────────┘
                                      │                   │              │
                   (Unauthenticated / │       (Auth via   │   (Auth via  │
                    Capability Token) │        Signature) │    ZK-Proof) │
                                      ▼                   ▼              ▼
┌──────────────────────────────────────┐ ┌──────────────────┐ ┌─────────────────────┐
│    UnauthenticatedDeliveryService    │ │ Authenticated    │ │ AnonymousGroupRelay │
├──────────────────────────────────────┤ │ RelayService     │ │ Service             │
│ • DeliverDirect(SealedEnvelope)      │ ├──────────────────┤ ├─────────────────────┤
│ • EnqueueMailbox(SealedEnvelope)     │ │ • ConnectMailbox │ │ • SubscribeGroup    │
│ • GetProfile(ProfileFetch)           │ │   Stream()       │ │   Stream()          │
│ • UploadProfile(ProfileUpload)       │ │ • DrainMailbox() │ │ • DispatchGroupMsg()│
│ • DeleteProfile(ProfileDelete)       │ │ • RegisterMbox() │ │ • CommitMutation()  │
│ • DownloadBlindBlob(BlindBlobReq)    │ │ • PublishPreKey()│ │ • FetchGroupState() │
│                                      │ │ • FetchPreKey()  │ └─────────────────────┘
└──────────────────────────────────────┘ └──────────────────┘
```

### 4.1 `unauthenticated_delivery.proto` (Public Edge / Sealed Sender & Capability Ingress)
- **Trust Boundary**: Open to peers, anonymous senders, and capability token holders. Zero sender identity metadata and zero channel identifiers on wire/logs.
- **Messages**:
  - `SealedEnvelopeProto`: `bytes recipient_routing_token = 1;`, `bytes delivery_token = 2;`, `bytes encrypted_package = 3;`, `google.protobuf.Timestamp expires_at_utc = 4;`
  - `DeliveryAckProto`: `bool success = 1;`, `string error_message = 2;`
  - `EnqueueAckProto`: `bool accepted = 1;`, `google.protobuf.Timestamp expires_at_utc = 2;`
  - `ProfileFetchRequestProto`: `bytes blinded_locator = 1;`
  - `ProfileFetchResponseProto`: `bool found = 1;`, `bytes nonce = 2;`, `bytes ciphertext = 3;` (1040 B padded block), `uint32 revision = 4;`, `google.protobuf.Timestamp expires_at_utc = 5;`
  - `ProfileUploadRequestProto`: `bytes blinded_locator = 1;`, `bytes write_token = 2;`, `uint32 revision = 3;`, `bytes nonce = 4;`, `bytes ciphertext = 5;`, `google.protobuf.Timestamp expires_at_utc = 6;`
  - `ProfileUploadResponseProto`: `bool success = 1;`, `string error_message = 2;`
  - `ProfileDeleteRequestProto`: `bytes blinded_locator = 1;`, `bytes write_token = 2;`
  - `ProfileDeleteResponseProto`: `bool success = 1;`, `string error_message = 2;`
  - `BlindBlobDownloadRequestProto`: `bytes blob_id = 1;` (32-byte SHA-256 blob locator)
  - `BlindBlobChunkProto`: `bytes chunk_data = 1;`, `uint32 chunk_index = 2;`, `bool is_final_chunk = 3;`
- **Service**:
  - `service UnauthenticatedDeliveryService`:
    - `rpc DeliverDirect(SealedEnvelopeProto) returns (DeliveryAckProto);`
    - `rpc EnqueueMailbox(SealedEnvelopeProto) returns (EnqueueAckProto);`
    - `rpc GetProfile(ProfileFetchRequestProto) returns (ProfileFetchResponseProto);`
    - `rpc UploadProfile(ProfileUploadRequestProto) returns (ProfileUploadResponseProto);`
    - `rpc DeleteProfile(ProfileDeleteRequestProto) returns (ProfileDeleteResponseProto);`
    - `rpc DownloadBlindBlob(BlindBlobDownloadRequestProto) returns (stream BlindBlobChunkProto);`

### 4.2 `authenticated_relay.proto` (Mailbox Ownership, Pre-Key Directory & Stream Management)
- **Trust Boundary**: Challenge-response / signature authenticated. Restricted exclusively to the private key holder of the `PublicIdentityId`.
- **Messages**:
  - `MailboxStreamClientMessage`: `oneof payload { AuthResponseProto auth_response = 1; MailboxAckProto ack = 2; }`
  - `MailboxStreamServerMessage`: `oneof payload { AuthChallengeProto auth_challenge = 1; AuthResultProto auth_result = 2; QueuedSealedEnvelopeProto envelope = 3; }`
  - `RegisterMailboxRequest`: `bytes owner_identity_key = 1;`, `bytes routing_token = 2;`, `bytes authorized_delivery_token = 3;`, `bytes signature = 4;`
  - `RegisterMailboxResponse`: `bool success = 1;`
  - `DrainMailboxRequest`: `bytes routing_token = 1;`, `bytes auth_token = 2;`
  - `DrainMailboxResponse`: `repeated QueuedSealedEnvelopeProto envelopes = 1;`
  - `PublishPreKeyBundleRequest`: `bytes identity_key = 1;`, `bytes signed_prekey = 2;`, `bytes prekey_signature = 3;`, `bytes signed_prekey_id = 4;`, `repeated OneTimePreKeyProto one_time_prekeys = 5;`, `bytes signature = 6;`
  - `PublishPreKeyBundleResponse`: `bool success = 1;`
  - `FetchPreKeyBundleRequest`: `bytes recipient_routing_token = 1;`, `bytes requester_identity_key = 2;`, `bytes requester_signature = 3;`, `google.protobuf.Timestamp timestamp_utc = 4;`
  - `FetchPreKeyBundleResponse`: `bytes identity_key = 1;`, `bytes signed_prekey = 2;`, `bytes prekey_signature = 3;`, `bytes signed_prekey_id = 4;`, `optional bytes one_time_prekey = 5;`, `optional bytes one_time_prekey_id = 6;`
  - `OneTimePreKeyProto`: `bytes key_id = 1;`, `bytes public_key = 2;`
- **Service**:
  - `service AuthenticatedRelayService`:
    - `rpc ConnectMailboxStream(stream MailboxStreamClientMessage) returns (stream MailboxStreamServerMessage);`
    - `rpc RegisterMailbox(RegisterMailboxRequest) returns (RegisterMailboxResponse);`
    - `rpc DrainMailbox(DrainMailboxRequest) returns (DrainMailboxResponse);`
    - `rpc PublishPreKeyBundle(PublishPreKeyBundleRequest) returns (PublishPreKeyBundleResponse);`
    - `rpc FetchPreKeyBundle(FetchPreKeyBundleRequest) returns (FetchPreKeyBundleResponse);`

### 4.3 `anonymous_group.proto` (Zero-Knowledge Group Relay Service)
- **Trust Boundary**: Zero-Knowledge membership verification. Group members prove authorization to read and write without revealing individual identities to the relay.
- **Messages**:
  - `GroupStreamClientMessage`: `bytes conversation_id = 1;`, `uint32 current_epoch = 2;`, `bytes zk_presentation_proof = 3;`
  - `GroupStreamServerMessage`: `oneof payload { GroupBroadcastEnvelope broadcast = 1; EpochMutationNotification mutation = 2; }`
  - `GroupBroadcastEnvelope`: `bytes conversation_id = 1;`, `uint32 epoch = 2;`, `uint32 sender_key_iteration = 3;`, `bytes ciphertext = 4;`, `bytes author_signature = 5;`
  - `EpochMutationNotification`: `bytes conversation_id = 1;`, `uint32 new_epoch = 2;`
  - `DispatchGroupMessageRequest`: `bytes conversation_id = 1;`, `uint32 epoch = 2;`, `bytes zk_presentation_proof = 3;`, `uint32 sender_key_iteration = 4;`, `bytes ciphertext = 5;`, `bytes author_signature = 6;`
  - `DispatchGroupMessageResponse`: `bool accepted = 1;`
  - `CommitRosterMutationRequest`: `bytes conversation_id = 1;`, `uint32 base_epoch = 2;`, `bytes new_roster_blob = 3;`, `repeated bytes new_routing_tokens = 4;`, `bytes zk_presentation_proof = 5;`
  - `CommitRosterMutationResponse`: `bool committed = 1;`, `uint32 new_epoch = 2;`
  - `FetchGroupStateRequest`: `bytes conversation_id = 1;`, `bytes zk_presentation_proof = 2;`
  - `FetchGroupStateResponse`: `uint32 current_epoch = 1;`, `bytes encrypted_roster_blob = 2;`
- **Service**:
  - `service AnonymousGroupRelayService`:
    - `rpc SubscribeGroupStream(GroupStreamClientMessage) returns (stream GroupStreamServerMessage);`
    - `rpc DispatchGroupMessage(DispatchGroupMessageRequest) returns (DispatchGroupMessageResponse);`
    - `rpc CommitRosterMutation(CommitRosterMutationRequest) returns (CommitRosterMutationResponse);`
    - `rpc FetchGroupState(FetchGroupStateRequest) returns (FetchGroupStateResponse);`

### 4.4 `session.proto` (Inner Cryptographic Packaging)
Carried inside `SealedEnvelopeProto.encrypted_package` (opaque to relays and passive network observers):
- `RatchetHeaderProto`: `bytes ratchet_public_key = 1;`, `uint32 counter = 2;`, `uint32 previous_chain_length = 3;`
- `DirectSessionPackageProto`: `RatchetHeaderProto header = 1;`, `bytes nonce = 2;`, `bytes ciphertext = 3;`
- `GroupSessionPackageProto`: `bytes conversation_id = 1;`, `uint32 iteration = 2;`, `bytes ciphertext = 3;`, `bytes author_signature = 4;`
- `HandshakeInvitationPackageProto`: `bytes initiator_identity_key = 1;`, `bytes initiator_ephemeral_key = 2;`, `bytes signed_prekey_id = 3;`, `bytes onetime_prekey_id = 4;`, `bytes encrypted_payload = 5;`

### 4.5 Application Plugins (`chat.proto`, `discovery.proto`, `file_transfer.proto`)
- `chat.proto`: `ChatPayloadEnvelopeProto` (with pseudo-random stepped bucket padding expanding frame to 256, 512, 1024, 2048, 4096 B), `ChatMessageProto`, `ReplyContextProto` (zero snippet text on wire), `MentionProto`, `MediaAttachmentProto`, `MessageEditProto`, `MessageTombstoneProto`, `ReactionUpdateProto`, `ReadReceiptProto`, `SystemNoticeProto`, `GroupInvitationProto`, `GroupInvitationResponseProto`.
- `discovery.proto`: `NearbyDiscoveryQueryProto` (clamped 1–100), `NearbyDiscoveryResponseProto`, `PeerDiscoveryDescriptorProto`, `DeliveryRouteProto`, `DiscoveryRegistrationProto`, `BlindedRendezvousQueryProto`, `BlindedRendezvousResponseProto`.
- `file_transfer.proto`: `TransferManifestProto` (concatenated 32-byte leaf array without per-leaf tags), `FileEntryProto`, `FileTransferOfferProto`, `FileTransferAcceptProto`, `FileTransferCancelProto`.

---

## 5. Implementation Chunks & Milestones

### Chunk 1: Foundation, Cryptography & Platform Primitives
- [ ] Scaffold `Percolator.Infrastructure/` with `net10.0`, nullable enabled, referencing `Percolator.Domain`, `Percolator.Application`, and `Percolator.PluginSdk`.
  - Required package references:
    - `Microsoft.EntityFrameworkCore.Sqlite.Core` (v10.x): EF Core SQLite provider without unencrypted bundle.
    - `SQLitePCLRaw.bundle_e_sqlcipher` (v2.1.x+): Native SQLCipher C-library bundle and C-API bindings (`SQLitePCL.raw.sqlite3_key`).
    - `Konscious.Security.Cryptography.Argon2` (v1.3.x): Managed memory-hard Argon2id key derivation engine ($m=64\text{ MB}, t=3, p=4$).
    - `Google.Protobuf` (v3.25.2, existing): Compiles `DatabaseHeader.proto`.
    - `Grpc.Tools` (v2.57.0, existing): Protobuf code generator.
- [ ] Author Protobuf schema `Percolator.Infrastructure/Protos/DatabaseHeader.proto` (`percolator.infrastructure.DatabaseHeader`):
  - Fixed-layout twin slots: `version`, `salt` (16 bytes), `master_slot_nonce` (12 bytes), `master_slot_payload` (48 bytes: 32-byte master key + 16-byte Poly1305 tag), `duress_slot_nonce` (12 bytes), `duress_slot_payload` (48 bytes: 32-byte sentinel + tag, or uniform dummy random noise).
- [ ] Implement `ProtobufHeaderStorage` (`Percolator.Infrastructure.Vault.IHeaderStorage`):
  - Internal infrastructure port for reading and writing `DatabaseHeader` to/from `percolator.header`.
  - Atomic flush and replace via temp file staging and `FileStream.Flush(flushToDisk: true)`.
- [ ] Implement `DuressWipeHandler` (`Percolator.Application.Ports.IDuressWipeHandler`):
  - Multi-phase cryptographic shredding: overwrites `percolator.header` with CSPRNG random bytes and forces physical disk controller flush (`flushToDisk: true`) before truncation and deletion in $< 2\text{ ms}$.
  - Tolerates external OS file locks on `percolator.db*` via `IOException` trapping without leaking failure to the caller.
  - Purges local cached attachments, avatars, and temporary staging stores.
- [ ] Implement `MasterKeyHandle` (`Percolator.Domain.Security.Ports.IMasterKeyHandle`):
  - Allocates and pins a 32-byte key buffer in unmanaged memory.
  - Implements `ApplyKeyToConnection(DbConnection connection)`: passes the raw 32-byte key span directly to `SQLitePCL.raw.sqlite3_key`, avoiding string allocations of sensitive key material on the managed GC heap.
  - Implements `WithKeySpan(Action<ReadOnlySpan<byte>> action)` and `WithKeySpan<TResult>(ReadOnlySpanFunc<byte, TResult> func)` visitor methods for $O(1)$ master password envelope re-wrapping.
  - Implements deterministic memory scrubbing via `CryptographicOperations.ZeroMemory` upon `Dispose()`.
- [ ] Implement `SqlCipherPragmaInterceptor` (`Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor`):
  - Intercepts `ConnectionOpened` on pooled `SqliteConnection` instances, invokes `_keyHandle.ApplyKeyToConnection(sqliteConn)`, and executes `PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;`.
- [ ] Implement `Stage2HostFactory`:
  - Assembles the Stage 2 post-unlock `IHost`.
  - Binds the active `IMasterKeyHandle`, registers `SqlCipherPragmaInterceptor` with `PercolatorDbContext`, and executes `Database.MigrateAsync()` prior to yielding the operational container.
- [ ] Implement `AuthenticatedSession` (`Percolator.Application.Ports.IAuthenticatedSession`):
  - Encapsulates inner `IHost` and exposes `IServiceProvider Services`.
  - Implements `StartAsync` and `StopAsync` to cleanly control `IHostedService` lifecycles.
- [ ] Implement `SqlCipherDatabaseUnlockCoordinator` (`Percolator.Application.Ports.IDatabaseUnlockCoordinator`):
  - Tracks `DatabaseSetupStatus` (`Uninitialized`, `Locked`, `Unlocked`).
  - `DetermineStatusAsync`: checks disk for existing `percolator.header`. If missing, returns `DatabaseSetupStatus.Uninitialized`.
  - `InitializeAsync(masterPassword, duressPassword, ct)`: generates 16-byte random salt, derives Argon2id key, generates random 32-byte $K_{\text{db}}$, encrypts master slot via ChaCha20-Poly1305, encrypts duress slot (sentinel or uniform random noise), writes `percolator.header`, probes canary, unlinks any orphaned `percolator.db*` files, runs EF Core migrations, builds Stage 2 `IHost`, and returns `DomainResult<IAuthenticatedSession>`.
  - `UnlockAsync(candidatePassword, ct)`: derives candidate key, attempts master slot ChaCha20-Poly1305 decryption; if match, probes canary `SELECT count(*) FROM sqlite_master;` (trapping native code 26 `SQLITE_NOTADB`), runs migrations, builds `AuthenticatedHost`, and returns `UnlockResult.Success(session)`.
  - If master slot fails, attempts duress slot decryption: if duress sentinel matches, executes `IDuressWipeHandler.ExecuteWipeAsync()` and returns `UnlockResult.DuressActivated()`. Otherwise returns `UnlockResult.InvalidCredentials()`.
  - `ChangeMasterPasswordAsync(currentPassword, newPassword, ct)`: re-wraps the 32-byte database encryption key under a fresh Argon2id key and updates `percolator.header` in an instantaneous $O(1)$ operation without re-encrypting the SQLite database file, preserving the existing duress slot.
  - `ConfigureDuressPasswordAsync(duressPassword, ct)`: updates or clears duress slot from active authenticated session, enforcing non-empty/non-whitespace validation.
  - `LockAsync()`: cleanly stops all hosted services, clears connection pools (`SqliteConnection.ClearAllPools()`), zeroizes key memory via `_masterKeyHandle.Dispose()`, and tears down `AuthenticatedHost`.
- [ ] Implement `SignalCryptoEngine` / `SodiumCryptoEngine` (`Percolator.Domain.Security.Ports.ICryptoEngine`):
  - Ed25519 signatures and verification (`VerifyEd25519Signature`, `SignEd25519`).
  - Curve25519 (X25519) Diffie-Hellman scalar multiplication (`ComputeDiffieHellman`, `DeriveX3dhMasterSecret`).
  - HKDF SHA-256 HMAC root key update (`KdfRk`) and symmetric chain key advances (`StepRatchet`).
  - AES-256-GCM authenticated encryption/decryption (`EncryptAesGcm`, `DecryptAesGcm`).
  - Resilient public key parsing (raw 32/64/65-byte point spans and ASN.1 DER SubjectPublicKeyInfo).
  - Memory zeroization with `CryptographicOperations.ZeroMemory` on sensitive spans.
  - Logging guardrail: redact all keys when `CryptographyOptions.EnableCryptographicMaterialLogging = false`.
  - RFC 4122 UUID endianness normalization (`Guid.ToByteArray(bigEndian: true)`).
  - Native callback context isolation via `IDbContextFactory<T>`.
- [ ] Implement `ZkgroupCryptographyService` (`Percolator.Domain.Security.Ports.IZkProofEngine`):
  - Wraps native `Signal.Interop` / `libzkgroup` C-ABI with safe handle management.
  - Generates and verifies zero-knowledge group membership proofs (`VerifyGroupPresentation`, `GenerateGroupPresentation`).
- [ ] Implement `SystemDateTimeProvider` (`Percolator.Domain.Common.IDateTimeProvider`):
  - Returns `DateTimeOffset.UtcNow`.

### Chunk 2: Wire Contracts, Protobuf Schemas & Packaging
- [ ] Configure `Google.Protobuf` and `Grpc.Tools` in `Percolator.Infrastructure.csproj`.
- [ ] Author Protobuf schemas in `Percolator.Infrastructure/Protos/`:
  - `unauthenticated_delivery.proto`
  - `authenticated_relay.proto`
  - `anonymous_group.proto`
  - `session.proto`
  - `chat.proto`
  - `discovery.proto`
  - `file_transfer.proto`
- [ ] Implement `ProtobufSessionWirePacker` (`Percolator.Application.Ports.ISessionWirePacker`):
  - Packs `DirectSessionPackageProto` and `GroupSessionPackageProto`.
  - Wraps into `SealedEnvelopeProto` with recipient `BlindedRoutingToken` and `DeliveryToken`.
  - Unpacks incoming `SealedEnvelopeProto` records into typed `InboundDirectEnvelope` or `InboundGroupEnvelope`.
- [ ] Implement `ProtobufPayloadSerializer` (`Percolator.PluginSdk.IPayloadSerializer`):
  - Binary serialization via `Google.Protobuf.CodedOutputStream` and `IBufferWriter<byte>`.
  - Span-based deserialization via `Google.Protobuf.MessageParser<T>`.
  - Stepped bucket padding for `ChatPayloadEnvelopeProto` (padded to 256, 512, 1024, 2048, 4096 B).

### Chunk 3: SQLite Persistence Engine & Domain Aggregate Repositories
- [ ] Configure SQLCipher via `SQLitePCLRaw.bundle_e_sqlcipher` with EF Core on unified `percolator.db`.
- [ ] Implement `PercolatorDbContext` with model configurations across operational outbox, cryptographic vault, chat history, and file transfer partitions, configured via `SqlCipherPragmaInterceptor`.
- [ ] Implement EF Core deterministic BLOB mapping with `ValueComparer<byte[]>`.
- [ ] Implement column-level encryption for private keys, chain keys, and master keys.
- [ ] Implement Domain & Application Repositories:
  - `SqliteOutboxRepository` (`Percolator.Application.Delivery.Ports.IOutboxRepository`)
  - `SqlitePeerContactRepository` (`Percolator.Domain.Identities.Ports.IPeerContactRepository`)\
  - `SqliteRatchetSessionRepository` (`Percolator.Application.Ports.IRatchetSessionRepository`)
  - `SqliteGroupReceiverSessionRepository` (`Percolator.Application.Ports.IGroupReceiverSessionRepository`)
  - `SqliteGroupSenderKeyRepository` (`Percolator.Application.Ports.IGroupSenderKeyRepository`)
  - `SqlitePendingHandshakeRepository` (`Percolator.Application.Ports.IPendingHandshakeRepository`)
  - `SqlitePrivatePreKeyStore` (`Percolator.Domain.Identities.Ports.IPrivatePreKeyStore`)
  - `SqliteLocalIdentityKeyStore` (`Percolator.Application.Ports.ILocalIdentityKeyStore`)
  - `SqliteGroupCredentialsRepository` (`Percolator.Domain.Security.Ports.IGroupCredentialsRepository`)
  - `SqliteChannelRepository` (`Percolator.Domain.Channels.Ports.IChannelRepository`)
  - `SqliteRelayPreKeyRepository` (`Percolator.Domain.Delivery.Ports.IRelayPreKeyRepository`)
  - `SqliteRelayProfileRepository` (`Percolator.Domain.Delivery.Ports.IRelayProfileRepository`)
  - `SqlitePeerProfileRepository` (`Percolator.PluginSdk.Profiles.IPeerProfileRepository`)
  - `SqlitePeerMailboxRoutingRepository` (`Percolator.PluginSdk.Profiles.IPeerMailboxRoutingRepository`)
  - `SqliteRelayLedgerRepository` (`Percolator.Domain.Delivery.Ports.IRelayLedgerRepository`)
- [ ] Implement Plugin Repositories:
  - `SqlChatMessageRepository` (`Percolator.Apps.Chat.Ports.IChatMessageRepository`)
  - `SqlGroupInvitationRepository` (`Percolator.Apps.Chat.Ports.IGroupInvitationRepository`)
  - `SqlUnknownGroupMessageCacheRepository` (`Percolator.Apps.Chat.Ports.IUnknownGroupMessageCacheRepository`)
  - `SqliteTransferSessionRepository` (`Percolator.Apps.FileTransfer.Ports.ITransferSessionRepository`)
  - `SqliteFilterPolicyRepository` (`Percolator.Apps.FileTransfer.Ports.IFileTransferFilterPolicyRepository`)
  - `SqliteRelayPeerDirectory` (`Percolator.Apps.Discovery.Features.RelayDirectoryHosting.IRelayPeerDirectory`)

### Chunk 4: CQRS Fast-Path Read Query Services
- [ ] Implement `SqlChatMessageQueryService` (`Percolator.Apps.Chat.Ports.IChatMessageQueryService`):
  - Direct paged SQL over `chat_messages` joined with reactions and attachments (`ChatMessageReadModel`).
- [ ] Implement `SqlConversationListQueryService` (`Percolator.Apps.Chat.Ports.IConversationListQueryService`):
  - Direct SQL query aggregating active channels, last message snippet, timestamp, unread badge count.
- [ ] Implement `SqlGroupInvitationQueryService` (`Percolator.Apps.Chat.Ports.IGroupInvitationQueryService`):
  - Direct SQL query for pending group invitations awaiting user consent.
- [ ] Implement `SqlOutboxQueryService` (`Percolator.Application.Delivery.Ports.IOutboxQueryService`):
  - Fast-path summary query for UI diagnostics without hydrating byte payload buffers.
- [ ] Implement `SqlPeerContactQueryService` (`Percolator.Application.Ports.IPeerContactQueryService`):
  - Fast-path query projecting contacts and pending contact requests into `PeerContactSummaryReadModel` and `PeerContactDetailReadModel`.
- [ ] Implement `SqlFileTransferQueryService` (`Percolator.Apps.FileTransfer.Ports.IFileTransferQueryService`):
  - Fast-path query against `ft_transfer_sessions` and `ft_manifest_files` (`TransferSessionReadModel`, `ManifestFileReadModel`).

### Chunk 5: Ingress Filtering, Stream Registry & Wire Dispatching
- [ ] Implement `IngressFilterService` (`Percolator.Application.Ports.IIngressFilterService`):
  - Enforces blacklist filtering (`PeerTrustLevel.Blocked`), sender rate limits, and identity dormancy status.
- [ ] Implement `StreamRegistry` (`Percolator.Application.Ports.IStreamRegistry`):
  - Tracks active gRPC duplex and server-streaming connections for direct peers and home relays.
  - Detects connection drops and handles flow-control saturation (`StreamWriteResult`).
- [ ] Implement `TransportDispatcher` (`Percolator.Application.Delivery.Ports.ITransportDispatcher`):
  - Direct 1:1: dispatches `DeliverDirect` over ephemeral TLS.
  - Relayed 1:1: dispatches `EnqueueMailbox` to recipient's home relay using `DeliveryToken`.
  - Relayed Group: dispatches `DispatchGroupMessage` to shared group relay with ZK membership proof.
- [ ] Implement `SealedEnvelopeUnwrapper` (`Percolator.Application.Ports.ISealedEnvelopeUnwrapper`):
  - Bridges relay `MailboxEnvelope` instances to strongly typed `InboundEnvelope` objects.

### Chunk 6: Network Transport Infrastructure & gRPC Services
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
- [ ] Implement `UnauthenticatedDeliveryEndpoint` (ASP.NET Core gRPC Service on relays and direct peer listening ports):
  - Handles `DeliverDirect`, `EnqueueMailbox`, `GetProfile`, `UploadProfile`, `DeleteProfile`, and `DownloadBlindBlob`.
- [ ] Implement Duplex & Streaming Workers (`IHostedService`):
  - `RelayMailboxStreamWorker`: Duplex stream to home relay with automatic reconnection and jittered exponential backoff.
  - `RelayGroupStreamWorker`: Live subscription stream with ZK proofs for active group channels.
- [ ] Implement Discovery & Client Adapters:
  - `DiscoveryQueryClient` (`Percolator.Apps.Discovery.Ports.INearbyDiscoveryQueryClientPort`)
  - `DiscoveryHandshakeAdapter` (`Percolator.Apps.Discovery.Ports.IDiscoveryHandshakePort`)
  - `PreKeyAvailabilityAdapter` (`Percolator.Apps.Discovery.Ports.IPreKeyAvailabilityPort`)
  - `PreKeyBundleProviderAdapter` (`Percolator.Application.Ports.IPreKeyBundleProvider`)
  - `GrpcProfileDeliveryClient` (`Percolator.PluginSdk.Profiles.IProfileDeliveryClient`)
  - `PrivateRendezvousService`: 24-hour UTC epoch blinded locators.

### Chunk 7: Encrypted Blob Storage & Streaming Adapters
- [ ] Implement `StreamingAesGcmCryptoService` (`Percolator.Application.Ports.IBlobCryptoService`):
  - EXIF & geolocation metadata scrubbing from raw media streams client-side before encryption.
  - Stepped bucket padding (< 1 MB to 32 KB; 1–10 MB to 256 KB; > 10 MB to 1 MB).
  - Chunked STREAM AEAD in fixed 64 KB blocks using `AesGcm` and sequential counter nonces.
  - Bounded memory usage via `ArrayPool<byte>.Shared`.
- [ ] Implement `RelayBlobClient` (`Percolator.Application.Ports.IRelayBlobClient`):
  - Resumable streaming upload (`POST /blobs`) and download (`GET /blobs/{sha256}`) with HTTP Range headers.
- [ ] Implement `PeerStreamBlobClient` (`Percolator.Application.Ports.IPeerStreamBlobClient`):
  - On-demand chunk streaming over active direct peer connections in `IStreamRegistry`.
- [ ] Implement `LocalFileChunkStore` (`Percolator.Application.Ports.ILocalChunkStore`):
  - Content-addressed disk chunk storage (`%AppData%/Percolator/Blobs/{sha256Hex}.blob`).
  - Atomic commit via temporary `.tmp` files and `File.Move(..., overwrite: true)`.
- [ ] Implement `ChannelRelayResolver` (`Percolator.Application.Ports.IChannelRelayResolver`):
  - Resolves designated blob relay endpoints per channel.

### Chunk 8: High-Performance Swarm File Transfer Engine & Desktop Storage
- [ ] Implement `TcpMultiStreamPool` (`Percolator.Apps.FileTransfer.Ports.ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`):
  - Pool of 4–8 multiplexed TCP connections with `TCP_NODELAY = true`.
  - Binary framing engine implementing frames 0x01–0x0C (`STREAM_HANDSHAKE`, `BITFIELD`, `HAVE`, `REQUEST_CHUNK`, `CHUNK_DATA`, `CANCEL_CHUNK`, `CHOKE`, `UNCHOKE`, `INTERESTED`, `NOT_INTERESTED`, `KEEP_ALIVE`, `PEX_PEERS`).
- [ ] Implement `NetworkInterfaceEndpointDiscoveryPort` (`Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort`):
  - Discovers local LAN candidate endpoints by querying non-loopback active `NetworkInterface` unicast IP addresses.
- [ ] Implement `RelayTransferPipeClient` & Relay Server Pipe Service:
  - 4–8 parallel streams connected to relay endpoint with `X-Pipe-Auth`.
  - Zero-copy bidirectional socket splicing on relay.
  - Remote manifest blob bounded stream guard (aborts if > 8 MB).
- [ ] Implement `RelaySwarmTrackerClient` & Relay Swarm Tracker Endpoint (`Percolator.Apps.FileTransfer.Ports.IDistributedTrackerPort`):
  - Ephemeral in-memory tracker with 90-second sliding expiration.
- [ ] Implement `PortableDesktopFileStore` (`Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage`):
  - Portable pre-allocation via `FileStream.SetLength`.
  - Zero-byte file immediate materialization (avoids dangling `.percolator-part`).
  - `.percolator-part` staging and atomic rename via `File.Move`.
  - Resumption bitfield scanner computing SHA-256 over existing chunks against Merkle leaf hashes.
  - Path traversal & OS security sanitization: Win32 reserved device names rejection (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`), NTFS Alternate Data Stream prohibition (`:`), canonical path boundary verification (`StartsWith`), and Unicode FormC normalization.
- [ ] Implement `TokenBucketRateLimiter` (`Percolator.Apps.FileTransfer.Ports.ITransferRateLimiter`):
  - High-precision token-bucket rate limiter with global and per-session speed caps.

### Chunk 9: Background Maintenance Workers & Composition Root
- [ ] Implement Scheduled `IHostedService` Pruning Workers:
  - `DisappearingMessageEvictionWorker`: Runs every 30s, calls `IChatMessageRepository.PurgeExpiredMessagesAsync` and evicts local blob caches.
  - `OutboxRetentionPruner`: Daily pruner for delivered outbox jobs older than 7 days.
  - `RelayPreKeyDirectoryPruner`: Daily pruner for expired prekey bundles from `IRelayPreKeyRepository`.
  - `RelayProfileSlotPruner`: Daily pruner for expired profile slots from `relay_profile_slots`.
  - `UnknownGroupMessageCachePruner`: Daily pruner for buffered out-of-order group messages exceeding 48h TTL.
  - `AbandonedInvitePruner`: Daily pruner for pending invites/handshakes exceeding expiration policies.
  - `RelayDiscoveryRegistrationPruner`: Daily pruner for stale relay peer registrations in `disc_relay_peer_registrations`.
  - `RelayBlobRetentionPruner`: Daily pruner for expired relay blobs and disk watermark enforcement.
  - `TransferOfferReaperWorker` (`ITransferOfferReaperService`): Runs every 15m, sweeps stale transfer offers older than 24h.
  - `SwarmEndpointRetentionPruner`: Daily pruner for transient `ft_swarm_endpoints` older than 7 days.
- [ ] Implement Two-Stage Composition Root:
  - `InfrastructureServiceCollectionExtensions.AddBootstrapInfrastructure(IServiceCollection, IConfiguration)`:
    - Calls `SQLitePCL.Batteries_V2.Init()`.
    - Registers non-DB primitives: `SignalCryptoEngine`, `SystemDateTimeProvider`, `ProtobufHeaderStorage`, `DuressWipeHandler`, `SqlCipherDatabaseUnlockCoordinator`.
  - `InfrastructureServiceCollectionExtensions.AddAuthenticatedInfrastructure(IServiceCollection, IConfiguration, IMasterKeyHandle)`:
    - Registers `IMasterKeyHandle` (instance passed from unlock).
    - Registers `SqlCipherPragmaInterceptor`.
    - Registers `PercolatorDbContext` configured with `SqlCipherPragmaInterceptor`.
    - Registers cryptographic engines, wire packers, serializers, repositories, and query services.
    - Registers transport dispatchers, gRPC channels, interceptors, and background workers.
    - Registers `Stage2HostFactory`.

---

## 6. High-Value, Low-Brittle Unit Testing Matrix

To guarantee robust infrastructure without introducing brittle tests that break on refactoring:
- **Strictly Banned Anti-Patterns**:
  - Never verify exact mock call counts unless testing idempotency.
  - Never use private member reflection.
  - Never assert serialized JSON or raw string output format; assert observable typed properties and state changes.
  - Never rely on wall-clock `Task.Delay` or timers; inject and advance `FakeTimeProvider`.

| Chunk | Test Category | Target Feature / State | Verifies (Observable Behavior) | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- | :--- |
| **C1** | RFC Vector | Ed25519 / X25519 Crypto | Known test vectors match RFC 8032 / RFC 7748 | Tests mathematical output; zero implementation coupling. |
| **C1** | Memory Invariant | Sensitive Key Disposal | `MasterKeyHandle` buffer memory zeroized on dispose; subsequent access throws `ObjectDisposedException` | Checks memory content directly before and after dispose. |
| **C1** | Normalization | RFC 4122 Big-Endian UUID | GUID bytes match big-endian RFC 4122 order | Verifies byte order output array. |
| **C1** | Vault Setup | Blank Password Setup & SQLCipher Enforcement | Setup with `VaultPassphrase.Empty` creates encrypted header; raw unkeyed connection fails with native code 26 (`SQLITE_NOTADB`); unlock with empty passphrase succeeds | Verifies database is genuinely encrypted under SQLCipher even with a blank password. |
| **C1** | Vault Unlock | Happy Path Master Unlock | Correct master password unlocks master slot, canary probe succeeds, migrations run, returns `UnlockResult.Success(session)` | Asserts observable session creation and service provider resolution. |
| **C1** | Plausible Deny | Plausible Deniability Wire & Decryption Invariant | Header without duress has identical 48B payload wire size; decryption of duress slot throws `AuthenticationTagMismatchException` | Asserts structural wire parity and cryptographic failure invariants without brittle entropy bounds. |
| **C1** | Duress Shred | Duress Shredding & File-Lock Fault Tolerance | Duress password overwrites header with random bytes and flushes to disk in $< 2\text{ ms}$; catches `IOException` on locked DB without throwing; returns `UnlockResult.DuressActivated` | Verifies key destruction takes absolute precedence over secondary file deletion and survives external locks. |
| **C1** | Auth Failure | Invalid Passphrase Attempt | Incorrect password fails master and duress slot decryption; returns `UnlockResult.InvalidCredentials`; zero files modified | Asserts negative authentication result; verifies zero side-effects on disk. |
| **C1** | Canary Trap | Explicit SQLite Canary Error Verification | Invalid key or corrupted file traps `SqliteException` with `SqliteErrorCode == 26` (`SQLITE_NOTADB`) and returns controlled failure | Directly tests researched native SQLite error code 26 trap. |
| **C1** | Relock Hygiene | In-Session Locking & Memory Scrubbing | Calling `LockAsync()` clears connection pools (`ClearAllPools()`), zeroizes `MasterKeyHandle`, disposes host, transitions status to `Locked` | Asserts unmanaged key memory is zeroized and post-lock access is strictly blocked. |
| **C1** | Envelope Re-wrap | Master Password Change & Duress Preservation | Re-wraps master slot with new key; database remains readable; subsequent unlock requires new password; existing duress password remains valid | Verifies envelope re-wrapping and duress slot retention across salt reuse. |
| **C1** | Re-wrap Reject | Master Password Change Rejection | Invalid current master password returns `DomainResult.Failure`; header remains byte-for-byte identical; original password remains valid | Asserts rejection and guarantees zero disk mutations on invalid current credentials. |
| **C1** | Domain Invariant | Cross-Passphrase Collision Rejection | `InitializeAsync` or `ConfigureDuressPasswordAsync` rejects duress password identical to master; header is untouched | Asserts domain invariant preventing ambiguous master/duress slot collisions. |
| **C1** | Duress Deactivate| Duress Clear in Active Session | Setting duress to `null` overwrites duress slot with CSPRNG noise; previous duress password returns `InvalidCredentials` | Verifies duress deactivation wipes duress capability cleanly. |
| **C1** | Fault Tolerance | Corrupted / Truncated Header Trapping | 0-byte or corrupted `percolator.header` traps `InvalidProtocolBufferException` gracefully, returning structured failure without crash | Asserts fault tolerance against disk corruption. |
| **C2** | Traffic Padding | Stepped Bucket Serialization | Serialized frames expand to 256/512/1024/2048/4096 B | Asserts length bounds; ignores padding payload contents. |
| **C2** | Deniability | Reply Wire Frame | Quote snippet text is strictly absent from wire payload | Asserts wire message contains only parent ID and author ID. |
| **C3** | Security at Rest | SQLCipher Column Encryption | Private keys and chain keys stored as ciphertext | Direct byte inspection of raw DB confirms plaintext is absent. |
| **C3** | LINQ Mapping | BLOB Content Equality | `ValueComparer<byte[]>` matches byte contents | Queries by byte array values and asserts match success. |
| **C3** | Aggregate Root | Atomicity of Repo Save | Full aggregate is committed or rolled back atomically | Asserts all child tables update together or throw without orphan rows. |
| **C4** | Query CQRS | Paged Conversation Timeline | Limit/offset paging clamps accurately to boundary | Asserts returned items count and ordering; zero aggregate hydration. |
| **C4** | Unread Rollup | Unread Badge Count | Count aggregates messages strictly past read receipt timestamp | Verifies aggregate integer count; ignores query execution plan. |
| **C5** | Ingress Defense | Blocked Identity Ingress | Envelopes from blocked identities rejected before crypto | Tests filter result enum; zero ratchet crypto calls executed. |
| **C5** | Flow Control | Stream Registry Backpressure | Buffer saturation returns `StreamWriteResult.Dropped` | Tests return status under queue overflow condition. |
| **C6** | SSRF Defense | Callback Endpoint Validator | Rejects loopback/RFC1918 unless dev flag enabled | Asserts rejection result for intranet IPs. |
| **C6** | Stealth Mode | Identity Readiness Interceptor | Incoming RPC returns `Unavailable` when stealth active | Asserts gRPC status code `StatusCode.Unavailable`. |
| **C6** | Anti-Replay | Delivery Certificate Auth | Rejects signatures with timestamp delta > 60s | Tests rejection for expired/future timestamps. |
| **C7** | Privacy Defense | Media EXIF Scrubbing | Strips GPS and device tags from JPEG/PNG before upload | Reads scrubbed stream with metadata reader; asserts tags empty. |
| **C7** | Streaming AEAD | Chunked STREAM AEAD | Corrupt chunk tag throws authenticated decryption failure | Injects 1 bitflip; asserts cryptographic verification fails. |
| **C7** | Resumption | Resumable Blob Download | HTTP Range header returns byte slice from requested offset | Asserts stream starts at requested offset without reading from byte 0. |
| **C8** | Path Traversal | Desktop File Store Sanitization | Rejects `CON`, `PRN`, `AUX`, `:` ADS, and `../` paths | Asserts `ArgumentException` / rejection without touching disk. |
| **C8** | Zero-Byte Invariant | Pre-allocation Staging | Creates zero-byte file immediately; stages `.percolator-part` | Verifies file existence on disk and staging name pattern. |
| **C8** | Merkle Verify | Resumption Bitfield Scanner | Validates existing chunks against Merkle leaf hashes | Asserts bitfield marks corrupt chunks as missing and valid as present. |
| **C8** | Swarm Framing | Multiplexed TCP Framing | Serializes and deserializes all 12 frames (0x01–0x0C) | Roundtrip byte parsing asserting typed frame equality. |
| **C9** | Deterministic Time | Disappearing Message Pruner | Expired messages deleted after advancing `FakeTimeProvider` | Advances virtual clock by 31s; asserts expired messages purged. |
| **C9** | Lock Defense | Retention Pruning Batching | Deletion uses `LIMIT` clauses to avoid SQLite lock escalation | Asserts batch execution succeeds under concurrent read workload. |
| **C9** | DI Composition | `AddAuthenticatedInfrastructure` Root | All planned infrastructure ports resolve from DI container | Resolves every interface; asserts non-null without circular dependency. |

---

## 7. Port-to-Adapter Realization Matrix

| # | Port (Interface) | Defining Project | Concrete Adapter | Target Chunk |
|---|---|---|---|---|
| 1 | `ICryptoEngine` | `Percolator.Domain` | `SignalCryptoEngine` / `SodiumCryptoEngine` | Chunk 1 |
| 2 | `IZkProofEngine` | `Percolator.Domain` | `ZkgroupCryptographyService` | Chunk 1 |
| 3 | `IDateTimeProvider` | `Percolator.Domain.Common` | `SystemDateTimeProvider` | Chunk 1 |
| 4 | `IDatabaseUnlockCoordinator` | `Percolator.Application` (or `Percolator.Application2`) | `SqlCipherDatabaseUnlockCoordinator` | Chunk 1 |
| 5 | `IAuthenticatedSession` | `Percolator.Application` (or `Percolator.Application2`) | `AuthenticatedSession` | Chunk 1 |
| 6 | `IMasterKeyHandle` | `Percolator.Domain.Security.Ports` | `MasterKeyHandle` | Chunk 1 |
| 7 | `IDuressWipeHandler` | `Percolator.Application` (or `Percolator.Application2`) | `DuressWipeHandler` | Chunk 1 |
| 8 | `IHeaderStorage` | `Percolator.Infrastructure.Vault` (Internal) | `ProtobufHeaderStorage` | Chunk 1 |
| 9 | `ISessionWirePacker` | `Percolator.Application` | `ProtobufSessionWirePacker` | Chunk 2 |
| 10 | `IPayloadSerializer` | `Percolator.PluginSdk` | `ProtobufPayloadSerializer` | Chunk 2 |
| 11 | `DbConnectionInterceptor` | `Microsoft.EntityFrameworkCore.Diagnostics` | `SqlCipherPragmaInterceptor` | Chunk 1 |
| 12 | `IOutboxRepository` | `Percolator.Application` | `SqliteOutboxRepository` | Chunk 3 |
| 13 | `IPeerContactRepository` | `Percolator.Domain` | `SqlitePeerContactRepository` | Chunk 3 |
| 14 | `IRatchetSessionRepository` | `Percolator.Application` | `SqliteRatchetSessionRepository` | Chunk 3 |
| 15 | `IGroupReceiverSessionRepository` | `Percolator.Application` | `SqliteGroupReceiverSessionRepository` | Chunk 3 |
| 16 | `IGroupSenderKeyRepository` | `Percolator.Application` | `SqliteGroupSenderKeyRepository` | Chunk 3 |
| 17 | `IPendingHandshakeRepository` | `Percolator.Application` | `SqlitePendingHandshakeRepository` | Chunk 3 |
| 18 | `IPrivatePreKeyStore` | `Percolator.Domain` | `SqlitePrivatePreKeyStore` | Chunk 3 |
| 19 | `ILocalIdentityKeyStore` | `Percolator.Application` | `SqliteLocalIdentityKeyStore` | Chunk 3 |
| 20 | `IGroupCredentialsRepository` | `Percolator.Domain` | `SqliteGroupCredentialsRepository` | Chunk 3 |
| 21 | `IChannelRepository` | `Percolator.Domain` | `SqliteChannelRepository` | Chunk 3 |
| 22 | `IRelayPreKeyRepository` | `Percolator.Domain` | `SqliteRelayPreKeyRepository` | Chunk 3 |
| 23 | `IRelayProfileRepository` | `Percolator.Domain` | `SqliteRelayProfileRepository` | Chunk 3 |
| 24 | `IPeerProfileRepository` | `Percolator.PluginSdk` | `SqlitePeerProfileRepository` | Chunk 3 |
| 25 | `IPeerMailboxRoutingRepository` | `Percolator.PluginSdk` | `SqlitePeerMailboxRoutingRepository` | Chunk 3 |
| 26 | `IRelayLedgerRepository` | `Percolator.Domain` | `SqliteRelayLedgerRepository` | Chunk 3 |
| 27 | `IChatMessageRepository` | `Percolator.Apps.Chat` | `SqlChatMessageRepository` | Chunk 3 |
| 28 | `IGroupInvitationRepository` | `Percolator.Apps.Chat` | `SqlGroupInvitationRepository` | Chunk 3 |
| 29 | `IUnknownGroupMessageCacheRepository` | `Percolator.Apps.Chat` | `SqlUnknownGroupMessageCacheRepository` | Chunk 3 |
| 30 | `ITransferSessionRepository` | `Percolator.Apps.FileTransfer` | `SqliteTransferSessionRepository` | Chunk 3 |
| 31 | `IFileTransferFilterPolicyRepository` | `Percolator.Apps.FileTransfer` | `SqliteFilterPolicyRepository` | Chunk 3 |
| 32 | `IRelayPeerDirectory` | `Percolator.Apps.Discovery` | `SqliteRelayPeerDirectory` | Chunk 3 |
| 33 | `IChatMessageQueryService` | `Percolator.Apps.Chat` | `SqlChatMessageQueryService` | Chunk 4 |
| 34 | `IConversationListQueryService` | `Percolator.Apps.Chat` | `SqlConversationListQueryService` | Chunk 4 |
| 35 | `IGroupInvitationQueryService` | `Percolator.Apps.Chat` | `SqlGroupInvitationQueryService` | Chunk 4 |
| 36 | `IOutboxQueryService` | `Percolator.Application` | `SqlOutboxQueryService` | Chunk 4 |
| 37 | `IPeerContactQueryService` | `Percolator.Application` | `SqlPeerContactQueryService` | Chunk 4 |
| 38 | `IFileTransferQueryService` | `Percolator.Apps.FileTransfer` | `SqlFileTransferQueryService` | Chunk 4 |
| 39 | `IIngressFilterService` | `Percolator.Application` | `IngressFilterService` | Chunk 5 |
| 40 | `IStreamRegistry` | `Percolator.Application` | `StreamRegistry` | Chunk 5 |
| 41 | `ITransportDispatcher` | `Percolator.Application` | `TransportDispatcher` | Chunk 5 |
| 42 | `ISealedEnvelopeUnwrapper` | `Percolator.Application` | `SealedEnvelopeUnwrapper` | Chunk 5 |
| 43 | `INearbyDiscoveryQueryClientPort` | `Percolator.Apps.Discovery` | `DiscoveryQueryClient` | Chunk 6 |
| 44 | `IDiscoveryHandshakePort` | `Percolator.Apps.Discovery` | `DiscoveryHandshakeAdapter` | Chunk 6 |
| 45 | `IPreKeyAvailabilityPort` | `Percolator.Apps.Discovery` | `PreKeyAvailabilityAdapter` | Chunk 6 |
| 46 | `IPreKeyBundleProvider` | `Percolator.Application` | `PreKeyBundleProviderAdapter` | Chunk 6 |
| 47 | `IProfileDeliveryClient` | `Percolator.PluginSdk` | `GrpcProfileDeliveryClient` | Chunk 6 |
| 48 | `IBlobCryptoService` | `Percolator.Application` | `StreamingAesGcmCryptoService` | Chunk 7 |
| 49 | `IRelayBlobClient` | `Percolator.Application` | `RelayBlobClient` | Chunk 7 |
| 50 | `IPeerStreamBlobClient` | `Percolator.Application` | `PeerStreamBlobClient` | Chunk 7 |
| 51 | `ILocalChunkStore` | `Percolator.Application` | `LocalFileChunkStore` | Chunk 7 |
| 52 | `IChannelRelayResolver` | `Percolator.Application` | `ChannelRelayResolver` | Chunk 7 |
| 53 | `ITransferStreamPool` / `Factory` / `Stream` | `Percolator.Apps.FileTransfer` | `TcpMultiStreamPool` & `TransferStream` | Chunk 8 |
| 54 | `INetworkEndpointDiscoveryPort` | `Percolator.Apps.FileTransfer` | `NetworkInterfaceEndpointDiscoveryPort` | Chunk 8 |
| 55 | `IDesktopFileStorage` | `Percolator.Apps.FileTransfer` | `PortableDesktopFileStore` | Chunk 8 |
| 56 | `ITransferRateLimiter` | `Percolator.Apps.FileTransfer` | `TokenBucketRateLimiter` | Chunk 8 |
| 57 | `IDistributedTrackerPort` | `Percolator.Apps.FileTransfer` | `RelaySwarmTrackerClient` | Chunk 8 |
| 58 | `ITransferOfferReaperService` | `Percolator.Apps.FileTransfer` | `TransferOfferReaperWorker` | Chunk 9 |
