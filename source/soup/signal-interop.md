# Signal.Interop SOUP Specification

Spec: [soup-spec](../spec/soup-spec.md)

## 1. Component Identification and Provenance

| Property | Value |
| :--- | :--- |
| **Component Name** | `Signal.Interop` (Managed C# Wrapper) and `signal_shim.dll` (Native Rust C-ABI Bridge) |
| **Supplier / Maintainer** | `zerotwooneone` (`https://github.com/zerotwooneone/libsignal-csharp`) wrapping upstream Signal Messenger LLC (`libsignal` & `zkgroup`) |
| **Safety Classification & Rationale** | **Class C** (IEC 62304 Clause 4.3).<br/>*Rationale:* The component provides the core cryptographic engine for Sealed Sender Ed25519 digital signatures, Zero-Knowledge Credential verification via ZkGroup, and SenderKey multi-party group messaging. An unmitigated software failure, unhandled crash, or key-schedule corruption directly results in compromise of secret identity keys, permanent session desynchronization, or unauthorized interception of confidential user communications. |
| **Approved Version & Hash** | `1.0.0` (Commit: `53c7164b1a26ea75b07a3cb6d48920ae5d7fa0a3`)<br/>`Signal.Interop.dll` SHA-256: `026AAB97675C0CB9B92890C9BA003D147DD0EEA4C769A4902ECCCA556A888572`<br/>`signal_shim.dll` SHA-256: `DD7A7DD276FF6CB9D5235148CACCF871C529AD4B0B60035279147C333E4C4B76` |
| **License Model** | GPL-3.0 / AGPL-3.0 |
| **Delivery & Linking Format** | Pre-compiled managed assembly (`net9.0`) + unmanaged dynamic link library (`signal_shim.dll` via P/Invoke C-ABI) |
| **Upstream Repository URL** | `https://github.com/zerotwooneone/libsignal-csharp` |

## 2. Hardware and Software Prerequisites

- **Supported Operating Systems & Runtimes:** Windows 10/11 64-bit (x64), Microsoft .NET 9.0 Runtime (`net9.0`), and Microsoft Visual C++ 2015–2022 Redistributable (`vcruntime140.dll` / Windows Universal CRT).
- **Hardware Architecture:** x86-64 (AMD64) architecture with 64-bit address space.
- **Native / Unmanaged Prerequisites:** Native dynamic library `signal_shim.dll` deployed in the application working directory or standard dynamic loader search path; unmanaged heap allocations with standard C allocator (`malloc`/`free`).

## 3. Specified Functional and Performance Capabilities

### 3.1 Active Functional & Capacity Envelope

1. **`SOUP-SIGNAL-ED25519-KEYGEN` (Deterministic Clamped Ed25519 Key Generation):** [Verify: Integration]
   The SOUP component provides allocation-free generation of 32-byte Ed25519 private keys and corresponding 32-byte public keys with native curve clamping required for Sealed Sender identity establishment.
   - *Capacity & Concurrency Constraints:* Destination spans must be exactly 32 bytes each. Function is stateless and reentrant across concurrent threads.
   - *Architectural Quarantine Adapter:* `SignalCryptoEngine` in `Percolator.Infrastructure`.

2. **`SOUP-SIGNAL-ED25519-SIGN` (Ed25519 Digital Signature Generation):** [Verify: Integration]
   The SOUP component generates 64-byte Ed25519 signatures over arbitrary non-empty message byte spans using a 32-byte clamped private key without managed heap allocation, realizing inner-payload authentication ([`FACT-SIGNAL-SEALED-SENDER-INNER-AUTHENTICATION`](../facts/signal-sealed-sender.md#2-axioms-and-platform-invariants)).
   - *Capacity & Concurrency Constraints:* Input message span must be non-empty ($\ge 1$ byte); private key must be exactly 32 bytes; signature buffer must be exactly 64 bytes. Function is stateless and thread-safe.
   - *Architectural Quarantine Adapter:* `SignalCryptoEngine` in `Percolator.Infrastructure`.

3. **`SOUP-SIGNAL-ED25519-VERIFY` (Ed25519 Signature Verification):** [Verify: Integration]
   The SOUP component validates 64-byte Ed25519 signatures against message byte spans and 32-byte public keys, returning a boolean status without throwing exceptions on cryptographic mismatch, enabling high-performance relay validation ([`FACT-CLR-EXCEPTION-HOTPATH-OVERHEAD`](../facts/platform-runtime.md#2-axioms-and-runtime-invariants)).
   - *Capacity & Concurrency Constraints:* Public key span must be 32 bytes; signature span must be 64 bytes. Function is stateless and thread-safe.
   - *Architectural Quarantine Adapter:* `SignalCryptoEngine` in `Percolator.Infrastructure`.

4. **`SOUP-SIGNAL-ZKGROUP-CREDENTIALS` (Anonymous Group Credential and Presentation Generation):** [Verify: Integration]
   The SOUP component provides Groups V2 zero-knowledge group membership proofs and anonymous profile credential presentations (`AuthCredentialWithPni`), enabling member verification without revealing long-term identity keys to untrusted group relays.
   - *Capacity & Concurrency Constraints:* Master keys are fixed at 32 bytes; anonymous presentations are bounded serialized byte arrays. Handle instances own native heap memory and must not be concurrently disposed during active operations.
   - *Architectural Quarantine Adapter:* `ZkgroupCryptographyService` in `Percolator.Infrastructure`.

5. **`SOUP-SIGNAL-SENDER-KEY` (SenderKey Ratchet Group Message Encryption):** [Verify: Integration]
   The SOUP component performs SenderKey ratchet encryption and decryption for multi-party group messaging, using VTable-based unmanaged callback structures (`SenderKeyStoreVTable`) for key state persistence and distribution record serialization.
   - *Capacity & Concurrency Constraints:* Sender device ID must fall in the Signal protocol range `[1, 127]`; UUIDs must be exactly 16 bytes. Store delegates must be pinned in memory via `GCHandle.Alloc` for the active store lifetime.
   - *Architectural Quarantine Adapter:* `SignalSenderKeyStoreAdapter` in `Percolator.Infrastructure`.

6. **`SOUP-SIGNAL-SAFE-HANDLES` (Deterministic Unmanaged Pointer Lifecycle):** [Verify: StaticAnalysis]
   All native heap pointers created by `signal_shim.dll` are encapsulated within derived `SafeHandle` instances (`GroupSecretParamsSafeHandle`, `SenderKeyRecordSafeHandle`, `GroupMasterKeySafeHandle`), ensuring zero native memory leaks upon garbage collection or explicit disposal.
   - *Architectural Quarantine Adapter:* `Percolator.Infrastructure` cryptographic services.

### 3.2 Unused and Prohibited Capabilities (FDA OTS Guidance §V.A.2)

1. **Server Secret Parameter Generation & Issuance:**
   - *Prohibited Functions:* `SignalCrypto.GenerateServerSecretParams`, `SignalCrypto.GetServerPublicParams`, `SignalCrypto.SerializeServerPublicParams`, `SignalCrypto.DeserializeServerPublicParams`, and `SignalCrypto.IssueAuthCredentialWithPni`.
   - *Quarantine Enforcement:* These functions exist for server-side verification and integration test fixtures. They are strictly excluded from client dependency injection. The client host container never registers or invokes these APIs, ensuring they remain dormant in client runtime binaries.
2. **Direct P/Invoke and Raw Pointer Exports:**
   - *Prohibited Functions:* Raw unmanaged C-ABI exports in `signal_shim.dll`.
   - *Quarantine Enforcement:* The domain and application layers have zero assembly references to `Signal.Interop` or `signal_shim.dll`. All operations are strictly mediated by `ISignalCryptoEngine` and `IZkgroupCryptographyService` interfaces implemented in `Percolator.Infrastructure`.

## 4. Known Anomalies and Cybersecurity Evaluation

1. **`SOUP-SIGNAL-ANO-EMPTY-SPAN` (Null Pointer Status Code 1 on Empty Input Spans):**
   - **External Reference / Defect:** `signal_shim` Rust FFI null-check boundary enforcement.
   - **Supplier Summary:** In `signal_shim`, passing a null pointer (which C# produces when pinning `Span.Empty` or `ReadOnlySpan<byte>.Empty`) causes an immediate status code `1` (`invalid argument`) failure.
   - **Relevance & Reachability:** Reachable if the application attempts to sign or verify a 0-byte message payload without prior length checks.
   - **Safety & Security Impact:** Premature operation abort leading to transient transmission failure ([`HAZ-CRASH-STATE-DESYNC`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards)).
   - **Compensating Risk Control:** Inherent defensive validation in `SignalCryptoEngine`: input spans are validated for non-zero length before invoking `SignalCrypto.Ed25519Sign` or `SignalCrypto.Ed25519Verify`.

2. **`SOUP-SIGNAL-ANO-RAW-SEED` (RFC 8032 Raw Seed Incompatibility):**
   - **External Reference / Defect:** Upstream `libsignal` clamped Ed25519 key representation.
   - **Supplier Summary:** `SignalCrypto.Ed25519Sign` and `Ed25519Verify` expect private keys formatted and clamped by `SignalCrypto.GenerateEd25519KeyPair`. Raw 32-byte RFC 8032 test seeds passed directly will fail signature verification.
   - **Relevance & Reachability:** Reachable during test execution or if external raw seeds are injected into identity creation flows.
   - **Safety & Security Impact:** Identity verification failure and denial of communication ([`HAZ-KEY-SUBSTITUTION-MITM`](../RiskRegister.md#1-cryptographic-identity-and-privacy-hazards)).
   - **Compensating Risk Control:** Infrastructure adapters strictly generate and persist key pairs exclusively through `SignalCrypto.GenerateEd25519KeyPair`.

3. **`SOUP-SIGNAL-ANO-VTABLE-GC` (Premature Garbage Collection of Unmanaged Function Pointers):**
   - **External Reference / Defect:** CLR interop delegate lifetime management.
   - **Supplier Summary:** The function pointers in `SenderKeyStoreVTable` (`LoadSenderKey`, `StoreSenderKey`) will cause an unmanaged Access Violation (process crash) if their underlying managed delegates are collected by the CLR garbage collector while native Rust code holds the store pointer.
   - **Relevance & Reachability:** Reachable during asynchronous or long-running group message encryption/decryption cycles.
   - **Safety & Security Impact:** Abrupt process termination ([`HAZ-CRASH-STATE-DESYNC`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards)).
   - **Compensating Risk Control:** The infrastructure adapter pins all delegate references using `GCHandle.Alloc` upon VTable creation and maintains them alive until the adapter container is explicitly disposed.

4. **`SOUP-SIGNAL-ANO-HEAP-RESIDUE` (Transient Key Retention in Managed Memory):**
   - **External Reference / Defect:** CLR garbage collection lack of automatic secret erasure.
   - **Supplier Summary:** While native Rust code securely zeroes memory on drop, managed buffers passed to or returned from interop calls remain on the managed heap or stack until overwritten ([`FACT-DEAD-STORE-ELIMINATION`](../facts/platform-runtime.md#2-axioms-and-runtime-invariants)).
   - **Relevance & Reachability:** Reachable on every cryptographic signing or key derivation operation.
   - **Safety & Security Impact:** Forensic memory disclosure after session lock ([`HAZ-POST-LOCK-MEMORY-RESIDUE`](../RiskRegister.md#2-endpoint-availability-and-integrity-hazards), [`HAZ-SESSION-KEY-COMPROMISE`](../RiskRegister.md#1-cryptographic-identity-and-privacy-hazards)).
   - **Compensating Risk Control:** Infrastructure adapters utilize stack-allocated spans (`stackalloc byte[32]`) and execute `CryptographicOperations.ZeroMemory` immediately following operation completion.

5. **`SOUP-SIGNAL-ANO-PANIC-STATUS` (Unmanaged Rust Panic Handling):**
   - **External Reference / Defect:** Rust panic boundary in `signal_shim`.
   - **Supplier Summary:** Panics within upstream Rust code are caught via `catch_unwind` and translated to status code `2`, returning cleanly across the C-ABI rather than aborting the process.
   - **Relevance & Reachability:** Reachable if malformed serialized structures or unexpected states trigger internal Rust assertion panics.
   - **Safety & Security Impact:** Cryptographic operation failure.
   - **Compensating Risk Control:** `Signal.Interop` converts status code `2` into a managed `CryptographicException`, which `Percolator.Infrastructure` catches and maps to a clean `DomainResult.Failure` without unhandled crash.

## 5. Maintenance and Obsolescence Plan

- **Vulnerability Monitoring Cadence:** The engineering team reviews upstream repositories (`https://github.com/zerotwooneone/libsignal-csharp` and upstream `signalapp/libsignal`) quarterly and monitors GitHub Security Advisories and RustSec databases for reported vulnerabilities.
- **Patch Qualification & Regression Verification:** Before any updated `Signal.Interop.dll` or `signal_shim.dll` binary is accepted into `vendor/Signal.Interop`, the full solution test suite (`Percolator.Infrastructure.Tests`) must be executed with 100% pass rate, validating key generation, Ed25519 roundtrips, presentation generation/verification, and memory boundary sanitization.
- **Obsolescence & Deprecation Contingency:** All `Signal.Interop` functionality is quarantined behind `Percolator.Domain` port interfaces (`ISignalCryptoEngine`, `IZkgroupCryptographyService`). In the event that upstream maintenance ceases, the repository retains the complete C-ABI bindings and can recompile `signal_shim` directly from source or replace the infrastructure adapter with another compliant FFI binding without modifying domain core logic.
