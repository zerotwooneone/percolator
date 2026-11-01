# Percolator.Infrastructure Requirements

Spec: [deployable-requirements-spec](../spec/deployable-requirements-spec.md)

## 1. Segregation Role and Boundary Constraints

1. **`DEP-INFRA-SEGREGATION-ROLE` (Technical Adapter & Unmanaged SOUP Boundary):** `[Verify: StaticAnalysis]`
   `Percolator.Infrastructure` is isolated as the outward boundary adapter deployable for all third-party SOUP dependencies (`Signal.Interop`, `signal_shim`, SQLCipher). It is forbidden from defining domain business logic or exposing unmanaged pointers (`IntPtr`, `SafeHandle`), vendor DTOs, or native exceptions across its public port implementations (IEC 62304 Clause 5.3.4). Furthermore, operating-system-specific kernel and cryptographic APIs (e.g., Windows CNG / `advapi32`) are strictly prohibited within this portable deployable and must reside exclusively in `Percolator.Infrastructure.Windows`.

## 2. Allocated Responsibilities

1. **`DEP-INFRA-SEALED-SENDER-CRYPTO` (Sealed Sender Digital Signature Generation & Verification):** `[Verify: Integration]`
   The deployable encapsulates [`SOUP-SIGNAL-ED25519-SIGN`](../soup/signal-interop.md#31-active-functional--capacity-envelope) and [`SOUP-SIGNAL-ED25519-VERIFY`](../soup/signal-interop.md#31-active-functional--capacity-envelope) within `SignalCryptoEngine`, providing allocation-free Ed25519 signature generation and non-throwing boolean verification for inner-payload sender authentication (realizes [`SYS-BLINDED-RELAY-ENVELOPES`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-AUTHENTIC-CRYPTO-PROVENANCE`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants); constrained by [`FACT-SIGNAL-SEALED-SENDER-INNER-AUTHENTICATION`](../facts/signal-sealed-sender.md#2-axioms-and-platform-invariants)).

2. **`DEP-INFRA-ZKGROUP-CREDENTIALS` (Anonymous Group Membership Proofs & Presentations):** `[Verify: Integration]`
   The deployable encapsulates [`SOUP-SIGNAL-ZKGROUP-CREDENTIALS`](../soup/signal-interop.md#31-active-functional--capacity-envelope) within `ZkgroupCryptographyService`, isolating group secret parameters and master key derivation behind domain port `IZkgroupCryptographyService` without exposing native handle lifecycles (realizes [`SYS-AUTHENTIC-CRYPTO-PROVENANCE`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

3. **`DEP-INFRA-SENDER-KEY-RATCHET` (SenderKey Multi-Party Group Message Encryption):** `[Verify: Integration]`
   The deployable encapsulates [`SOUP-SIGNAL-SENDER-KEY`](../soup/signal-interop.md#31-active-functional--capacity-envelope) within `SignalSenderKeyStoreAdapter` and `SignalCryptoEngine`, providing SenderKey ratchet encryption, decryption, and distribution record serialization via pinned unmanaged VTable callbacks (`SenderKeyStoreVTable`) without leaking native delegates to the CLR garbage collector (realizes [`SYS-RATCHETED-CONFIDENTIALITY`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-AUTHENTIC-CRYPTO-PROVENANCE`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

4. **`DEP-INFRA-VAULT-ENCRYPTION` (SQLCipher Database Vault Persistence):** `[Verify: Integration]`
   The deployable encapsulates SQLCipher relational persistence, executing native `sqlite3_key` binding inside EF Core connection interceptors using pinned memory keys derived during Stage 1 unlock (realizes [`SYS-AT-REST-VAULT-ENCRYPTION`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

## 3. Public Contract Invariants

1. **`DEP-INFRA-CONCURRENCY-MODEL` (Adapter Thread-Safety and Lifetime Scoping):** `[Verify: StaticAnalysis]`
   All cryptographic adapter implementations (`ISignalCryptoEngine`, `IZkgroupCryptographyService`) exposed across public port boundaries must be stateless and reentrant across concurrent threads. Relational database contexts (`PercolatorDbContext`) and Stage 1 bootstrap containers must be single-threaded and scoped to an individual session lifecycle, prohibiting unsynchronized concurrent access (IEC 62304 Clause 5.3.2).

2. **`DEP-INFRA-DEFENSIVE-SPAN-VALIDATION` (Pre-FFI Defensive Span Non-Emptiness):** `[Verify: Unit]`
   All cryptographic adapter entrypoints must validate that destination and source byte spans are non-empty and match exact buffer sizes (32 bytes for keys, 64 bytes for signatures) before transitioning to native Rust FFI code, preventing native status code 1 rejections ([`SOUP-SIGNAL-ANO-EMPTY-SPAN`](../soup/signal-interop.md#4-known-anomalies-and-cybersecurity-evaluation)).

3. **`DEP-INFRA-NATIVE-FAULT-TRANSLATION` (Fault Containment and DomainResult Conversion):** `[Verify: Unit]`
   All native return status codes and unmanaged exceptions must be caught and converted into value-type `DomainResult` failures or logged without crashing the host process, satisfying [`SYS-RESULT-DRIVEN-INGRESS`](../SystemRequirements.md#3-external-interfaces-and-resource-boundaries).

4. **`DEP-INFRA-VOLATILE-ZEROIZATION` (Memory-Barrier Sensitive Buffer Erasure):** `[Verify: Unit]`
   All ephemeral stack buffers and pinned arrays containing private keys or decrypted secrets must be explicitly overwritten with zeros via `CryptographicOperations.ZeroMemory` immediately upon completion of the operation (realizing [`SYS-DETERMINISTIC-ZEROIZATION`](../SystemRequirements.md#2-security-privacy-and-risk-control-invariants); constrained by [`FACT-DEAD-STORE-ELIMINATION`](../facts/platform-runtime.md#2-axioms-and-platform-invariants)).
