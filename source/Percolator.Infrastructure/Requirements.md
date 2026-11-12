# Percolator.Infrastructure Requirements

Spec: [deployable-requirements-spec](../spec/deployable-requirements-spec.md)

## 1. Segregation Role and Boundary Constraints

1. **`DEP-INFRA-SEGREGATION-ROLE` (Technical Adapter & Unmanaged SOUP Boundary):** `[Verify: StaticAnalysis]`
   `Percolator.Infrastructure` is isolated as the outward boundary adapter deployable for all third-party SOUP dependencies (`Signal.Interop`, `signal_shim`, SQLCipher). It is forbidden from defining domain business logic or exposing unmanaged pointers (`IntPtr`, `SafeHandle`), vendor DTOs, or native exceptions across its public port implementations (IEC 62304 Clause 5.3.4). Furthermore, operating-system-specific kernel and cryptographic APIs (e.g., Windows CNG / `advapi32`) are strictly prohibited within this portable deployable and must reside exclusively in `Percolator.Infrastructure.Windows`.

## 2. Allocated Responsibilities

1. **`DEP-INFRA-SEALED-SENDER-CRYPTO` (Sealed Sender Digital Signature Generation & Verification):** `[Verify: Integration]`
   The deployable encapsulates [`SOUP-SIGNAL-ED25519-SIGN`](../soup/signal-interop.md#31-active-functional--capacity-envelope) and [`SOUP-SIGNAL-ED25519-VERIFY`](../soup/signal-interop.md#31-active-functional--capacity-envelope) behind domain port `ISignalCryptoEngine`, providing allocation-free Ed25519 signature generation and non-throwing boolean verification for inner-payload sender authentication (realizes [`SYS-BLINDED-RELAY-ENVELOPES`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-AUTHENTIC-CRYPTO-PROVENANCE`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants); constrained by [`FACT-SIGNAL-SEALED-SENDER-INNER-AUTHENTICATION`](../facts/signal-sealed-sender.md#2-axioms-and-platform-invariants)).

2. **`DEP-INFRA-ZKGROUP-CREDENTIALS` (Anonymous Group Membership Proofs & Presentations):** `[Verify: Integration]`
   The deployable encapsulates [`SOUP-SIGNAL-ZKGROUP-CREDENTIALS`](../soup/signal-interop.md#31-active-functional--capacity-envelope), isolating group secret parameters and master key derivation behind domain port `IZkgroupCryptographyService` without exposing native handle lifecycles (realizes [`SYS-AUTHENTIC-CRYPTO-PROVENANCE`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

3. **`DEP-INFRA-SENDER-KEY-RATCHET` (SenderKey Multi-Party Group Message Encryption):** `[Verify: Integration]`
   The deployable encapsulates [`SOUP-SIGNAL-SENDER-KEY`](../soup/signal-interop.md#31-active-functional--capacity-envelope), providing SenderKey ratchet encryption, decryption, and distribution record serialization via pinned unmanaged VTable callbacks without leaking native delegates to the CLR garbage collector (realizes [`SYS-RATCHETED-CONFIDENTIALITY`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-AUTHENTIC-CRYPTO-PROVENANCE`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

4. **`DEP-INFRA-VAULT-ENCRYPTION` (SQLCipher Database Vault Persistence):** `[Verify: Integration]`
   The deployable encapsulates SQLCipher transparent 256-bit AES page-level database vault encryption, executing native `sqlite3_key` binding inside database connection interceptors using pinned memory keys derived from the user's password during Stage 1 unlock (realizes [`SYS-AT-REST-VAULT-ENCRYPTION`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)). Because the entire SQLite database file is encrypted at rest under this master key, cryptographic keys and session secrets are stored as native relational binary values without redundant column-level double-encryption, and structured entity domains are persisted across normalized relational schemas rather than JSON blobs.

5. **`DEP-INFRA-CHAT-PERSISTENCE` (Chat History, Keyset Pagination, and Cascade Purge):** `[Verify: Integration]`
   The deployable implements relational persistence and fast-path CQRS query projections for chat conversations, messages, reactions, and attachments, enforcing composite timeline indexing, cryptographic deletion cascades, and scheduled disappearing message purging across disk caches (realizes [`SYS-RETENTION-PURGE`](../SystemRequirements.md#1-functional-system-capabilities)).

6. **`DEP-INFRA-DISCOVERY-RENDEZVOUS-STORE` (Blinded Rendezvous Table & Lease Eviction):** `[Verify: Integration]`
   The deployable persists discovery directory leases and blinded rendezvous locators with sliding TTL eviction (24h/48h) and strict capacity clamping (max 10,000 entries), enforcing pre-key presence checks and clamping batch query results to 100 entries (realizes [`SYS-RELAY-ONLY-CLOAKING`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-BOUNDED-RELAY-QUOTAS`](../SystemRequirements.md#3-external-interfaces-and-resource-boundaries)).

7. **`DEP-INFRA-LOCAL-BLOB-STORE` (Content-Addressed Blind Blob Storage & Quota Guard):** `[Verify: Integration]`
   The deployable implements content-addressed local chunk and media blob storage at the local storage cache root using atomic temporary file staging, enforcing a 100 MB single-blob ceiling and 5 GB local cache quota returning `DomainResult.Failure` without process termination (realizes [`SYS-STREAM-AEAD-CHUNKED-TRANSFER`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-BOUNDED-RELAY-QUOTAS`](../SystemRequirements.md#3-external-interfaces-and-resource-boundaries)).

8. **`DEP-INFRA-PORTABLE-FILE-STORAGE` (Canonical Root Containment & Staged Piece Assembly):** `[Verify: Unit]`
   The deployable implements desktop file transfer storage, verifying that all written and read chunk paths remain strictly under the canonical root directory to prevent directory traversal attacks, and staging in-flight piece downloads via `.percolator-part` extensions with atomic rename upon full verification (realizes [`SYS-PATH-TRAVERSAL-DEFENSE`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

9. **`DEP-INFRA-MEDIA-METADATA-SANITIZATION` (Media Attachment EXIF and Geolocation Sanitization):** `[Verify: Unit]`
   The deployable strips EXIF, IPTC, XMP metadata, GPS coordinates, camera serials, and device identifiers from outbound image and video streams prior to chunking and AEAD encryption, preventing geolocation and device fingerprint leakage (realizes [`SYS-MEDIA-METADATA-SANITIZATION`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

10. **`DEP-INFRA-PREKEY-AVAILABILITY` (Pre-Key Availability Isolation & Bundle Provider):** `[Verify: Integration]`
    The deployable implements `IPreKeyAvailabilityPort` by verifying Signed PreKey presence without reading, popping, or modifying One-Time PreKeys from the relay's hosted pre-key ledger, and isolates One-Time PreKey retrieval and consumption exclusively to `IPreKeyBundleProvider` during explicit session establishment (realizes [`SYS-DISCOVERY-PREKEY-ISOLATION`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

11. **`DEP-INFRA-DURESS-DECOY-VAULT-LIFECYCLE` (Emergency Wipe & Decoy Vault Re-Initialization):** `[Verify: Integration]`
    The deployable implements `IDatabaseUnlockCoordinator` such that an emergency-credential match destroys the vault header and database ciphertext and immediately re-initializes an authentic empty vault whose header keys the emergency credential as its primary credential, then establishes a live authenticated session against it so decoy state persists identically to a genuine vault (realizes [`SYS-DECOY-VAULT-DENIABILITY`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

12. **`DEP-INFRA-LOCAL-STORAGE-CONTAINMENT` (Machine-Local Application Data Roots):** `[Verify: Unit]`
    The deployable resolves all default storage roots for the vault database, headers, blob caches, and file-transfer partials inside operating-system machine-local user storage scope, and never within OS roaming or directory-synced profile mechanisms (realizes [`SYS-LOCAL-STORAGE-CONTAINMENT`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

13. **`DEP-INFRA-VAULT-SCHEMA-MIGRATION` (Versioned Vault Schema Evolution):** `[Verify: Integration]`
    The deployable establishes and upgrades the vault's persisted schema through versioned migrations executed against the authenticated key prior to authenticated-host composition, preserving all existing content across application version upgrades without destructive re-initialization (realizes [`SYS-VAULT-UPGRADE-DATA-PRESERVATION`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

## 3. Public Contract Invariants

1. **`DEP-INFRA-CONCURRENCY-MODEL` (Adapter Thread-Safety and Lifetime Scoping):** `[Verify: StaticAnalysis]`
   All cryptographic adapter implementations (`ISignalCryptoEngine`, `IZkgroupCryptographyService`) exposed across public port boundaries must be stateless and reentrant across concurrent threads. Relational database contexts and authenticated session scopes must be single-threaded and scoped to an individual session lifecycle, prohibiting unsynchronized concurrent access (IEC 62304 Clause 5.3.2).

2. **`DEP-INFRA-DEFENSIVE-SPAN-VALIDATION` (Pre-FFI Defensive Span Non-Emptiness):** `[Verify: Unit]`
   All cryptographic adapter entrypoints must validate that destination and source byte spans are non-empty and match exact buffer sizes (32 bytes for keys, 64 bytes for signatures) before transitioning to native Rust FFI code, preventing native status code 1 rejections ([`SOUP-SIGNAL-ANO-EMPTY-SPAN`](../soup/signal-interop.md#4-known-anomalies-and-cybersecurity-evaluation)).

3. **`DEP-INFRA-NATIVE-FAULT-TRANSLATION` (Fault Containment and DomainResult Conversion):** `[Verify: Unit]`
   All native return status codes and unmanaged exceptions must be caught and converted into value-type `DomainResult` failures or logged without crashing the host process, satisfying [`SYS-RESULT-DRIVEN-INGRESS`](../SystemRequirements.md#3-external-interfaces-and-resource-boundaries)).

4. **`DEP-INFRA-VOLATILE-ZEROIZATION` (Memory-Barrier Sensitive Buffer Erasure):** `[Verify: Unit]`
   All ephemeral stack buffers and pinned arrays containing private keys or decrypted secrets must be explicitly overwritten with zeros via `CryptographicOperations.ZeroMemory` immediately upon completion of the operation (realizing [`SYS-DETERMINISTIC-ZEROIZATION`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants); constrained by [`FACT-DEAD-STORE-ELIMINATION`](../facts/platform-runtime.md#2-axioms-and-platform-invariants)).
