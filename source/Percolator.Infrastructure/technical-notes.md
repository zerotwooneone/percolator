# Percolator.Infrastructure Technical Notes & Architectural Reference

This document serves as the comprehensive deep-technical reference for `Percolator.Infrastructure`. It records the architectural invariants, database schemas, cryptographic wire contracts, protocol framing, and security mechanisms underpinning the Percolator microkernel.

For the actionable, phased implementation roadmap, refer to [implementation-plan.md](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/implementation-plan.md).

---

## 1. Architectural Role & Boundary Constraints

`Percolator.Infrastructure` provides concrete technical adapters, persistent storage repositories, network transport clients/servers, cryptographic primitives, and binary serialization engines for the Percolator microkernel architecture.

Following Clean Architecture & Onion Architecture principles:
- **Target Framework**: Standard `net10.0` with **zero Windows-specific, macOS-specific, or platform-locked dependencies**. Runs uniformly on headless Linux Docker containers (relay hosts), macOS, and cross-platform desktop runtimes.
- **Dependencies Flow Inward**: `Percolator.Infrastructure` references `Percolator.Domain`, `Percolator.Domain.Kernel`, `Percolator.Application`, and `Percolator.PluginSdk`. It does NOT expose infrastructure-specific types (e.g., SQLite connections, gRPC stubs, Protobuf classes, raw sockets) to the domain or application layers.
- **Port Realization & Swappability**: Every component in this project implements an interface (port) defined by `Percolator.Domain`, `Percolator.Application`, `Percolator.PluginSdk`, or application plugins (`Percolator.Apps.*`).
- **Two-Stage Container Separation**:
  - *Stage 1 (Bootstrap Container)*: Pre-unlock environment containing zero SQLite DbContexts, zero repositories, and zero background worker instances. Registers `SQLitePCL.Batteries_V2.Init()`, `SignalCryptoEngine`, `SystemDateTimeProvider`, `ProtobufHeaderStorage`, `DuressWipeHandler`, and `SqlCipherDatabaseUnlockCoordinator`.
  - *Stage 2 (Authenticated Host - `IHost`)*: Post-unlock environment built by `Stage2HostFactory`. Hosts `IMasterKeyHandle` (scoped to session in pinned unmanaged memory), `SqlCipherPragmaInterceptor`, `PercolatorDbContext` (scoped/pooled), all 57 domain repositories/query services, and `IHostedService` background workers.
- **Platform Separation Boundary (`Percolator.Infrastructure.Windows`)**:
  - Core `Percolator.Infrastructure` provides complete, 100% portable baseline implementations.
  - Platform-specific optimizations are decoupled into `Percolator.Infrastructure.Windows` (`net10.0-windows`) and swapped in via dependency injection.
  - **Explicit Non-Goals**:
    - **No Mac or Linux-specific implementations**: No macOS Keychain, Apple APNS, Linux libsecret, or D-Bus adapters.
    - **No low-value Windows implementations**: No Windows Registry key stores, no WMI hardware detectors, no Windows Event Log sinks, no Named Pipe IPC transports.
    - **Zero DPAPI Master Key Persistence**: In accordance with the strict threat model, the database master key $K_{\text{db}}$ and user passphrases are **never** persisted to disk, Windows DPAPI, or OS keychain. The key exists solely in pinned unmanaged memory via `IMasterKeyHandle` for the active duration of the authenticated session.
    - **Only 2 targeted Windows adapters are supported**:
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

## 3. Database Architecture & Storage Subsystems

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

## 4. Vault Header Schema & Cryptographic Mechanics

### 4.1 Protobuf Binary Header Contract (`DatabaseHeader.proto`)

Compiled strictly inside `Percolator.Infrastructure`:

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

### 4.2 ChaCha20-Poly1305 Slot Trials & Exception Control Flow
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

### 4.3 Researched Canary Error Handling (`SQLITE_NOTADB` = 26)
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

### 4.4 Instant Crypto-Shredding Pipeline (`DuressWipeHandler`)
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

### 4.5 Master Password Update ($O(1)$ Re-Wrap Workflow)
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

## 5. Network Protocols & Binary Framing

### 5.1 TCP Multi-Stream Swarm Framing Protocol (0x01–0x0C)

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

### 5.2 Dynamic Port Range Probing & gRPC Interceptors

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

## 6. Wire Contracts & Protobuf Services (`Protos/`)

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

### 6.1 `unauthenticated_delivery.proto` (Public Edge / Sealed Sender & Capability Ingress)
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

### 6.2 `authenticated_relay.proto` (Mailbox Ownership, Pre-Key Directory & Stream Management)
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

### 6.3 `anonymous_group.proto` (Zero-Knowledge Group Relay Service)
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

### 6.4 `session.proto` (Inner Cryptographic Packaging)
Carried inside `SealedEnvelopeProto.encrypted_package`:
- `RatchetHeaderProto`: `bytes ratchet_public_key = 1;`, `uint32 counter = 2;`, `uint32 previous_chain_length = 3;`
- `DirectSessionPackageProto`: `RatchetHeaderProto header = 1;`, `bytes nonce = 2;`, `bytes ciphertext = 3;`
- `GroupSessionPackageProto`: `bytes conversation_id = 1;`, `uint32 iteration = 2;`, `bytes ciphertext = 3;`, `bytes author_signature = 4;`
- `HandshakeInvitationPackageProto`: `bytes initiator_identity_key = 1;`, `bytes initiator_ephemeral_key = 2;`, `bytes signed_prekey_id = 3;`, `bytes onetime_prekey_id = 4;`, `bytes encrypted_payload = 5;`

### 6.5 Application Plugins (`chat.proto`, `discovery.proto`, `file_transfer.proto`)
- `chat.proto`: `ChatPayloadEnvelopeProto` (stepped bucket padding 256, 512, 1024, 2048, 4096 B), `ChatMessageProto`, `ReplyContextProto` (zero snippet text on wire), `MentionProto`, `MediaAttachmentProto`, `MessageEditProto`, `MessageTombstoneProto`, `ReactionUpdateProto`, `ReadReceiptProto`, `SystemNoticeProto`, `GroupInvitationProto`, `GroupInvitationResponseProto`.
- `discovery.proto`: `NearbyDiscoveryQueryProto` (clamped 1–100), `NearbyDiscoveryResponseProto`, `PeerDiscoveryDescriptorProto`, `DeliveryRouteProto`, `DiscoveryRegistrationProto`, `BlindedRendezvousQueryProto`, `BlindedRendezvousResponseProto`.
- `file_transfer.proto`: `TransferManifestProto` (concatenated 32-byte leaf array without per-leaf tags), `FileEntryProto`, `FileTransferOfferProto`, `FileTransferAcceptProto`, `FileTransferCancelProto`.
