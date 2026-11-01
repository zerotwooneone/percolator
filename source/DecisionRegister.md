# Percolator Decision Register

Spec: [decision-register-spec](spec/decision-register-spec.md)

## 1. Architectural and Structural Decisions

1. **`DEC-SQLCIPHER-VAULT-STORAGE` (Zero-OS Keyring SQLCipher Vault with Plausible Deniability Header):**
   - **Status & Review Record:** `Approved` (2026-10-30 | Architecture Review)
   - **Context & Problem Statement:** Secure local storage is required for private keys, contacts, and message history without relying on operating-system-specific keyrings (DPAPI, Keychain) that can be accessed by other local processes ([`SYS-AT-REST-VAULT-ENCRYPTION`](SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).
   - **Decision & Technical Rationale:** Implement database encryption using SQLCipher via native `sqlite3_key` with master keys derived from user passphrases, stored in a fixed-size twin-slot header (`percolator.header`) for plausible deniability, and held in pinned memory with explicit zeroization upon session lock ([`FACT-DEAD-STORE-ELIMINATION`](facts/platform-runtime.md#2-axioms-and-runtime-invariants)).
   - **Rejected Alternatives:**
     1. *OS Keyring Storage (DPAPI/Keychain):* Rejected because OS credentials do not protect against local root/admin processes and cannot support plausible deniability or cross-platform memory pinning.
     2. *Plaintext SQLite with Application-Level Column Encryption:* Rejected because unencrypted database page headers, indexes, and B-tree metadata leak communication frequency, peer identifiers, and timestamps.
   - **Safety, Risk, and Security Impact:** Protective control (ISO 14971 Clause 7.1) directly mitigating [`HAZ-OFFLINE-DISK-EXTRACTION`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards) and [`HAZ-POST-LOCK-MEMORY-RESIDUE`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards).

## 2. Cryptographic, Protocol, and Security Decisions

1. **`DEC-SIGNAL-INTEROP-ADOPTION` (Adoption of Signal.Interop Rust C-ABI Wrapper as Class C SOUP):**
   - **Status & Review Record:** `Approved` (2026-10-31 | Security & Architecture Review)
   - **Context & Problem Statement:** The system requires production-grade Ed25519 digital signatures for Sealed Sender inner authentication ([`SYS-BLINDED-RELAY-ENVELOPES`](SystemRequirements.md#2-security-privacy-and-risk-control-invariants)), Groups V2 zero-knowledge credentials, and SenderKey multi-device ratchet encryption ([`SYS-RATCHETED-CONFIDENTIALITY`](SystemRequirements.md#2-security-privacy-and-risk-control-invariants)). Implementing raw curve arithmetic in pure C# risks side-channel timing flaws, scalar clamping errors, and zero-knowledge presentation defects ([`FACT-SIGNAL-SEALED-SENDER-INNER-AUTHENTICATION`](facts/signal-sealed-sender.md#2-axioms-and-platform-invariants)).
   - **Decision & Technical Rationale:** Adopt `Signal.Interop` (and its native Rust C-ABI engine `signal_shim.dll`, [`SOUP-SIGNAL-ED25519-KEYGEN`](soup/signal-interop.md#31-active-functional--capacity-envelope), [`SOUP-SIGNAL-ED25519-SIGN`](soup/signal-interop.md#31-active-functional--capacity-envelope), [`SOUP-SIGNAL-ED25519-VERIFY`](soup/signal-interop.md#31-active-functional--capacity-envelope), [`SOUP-SIGNAL-ZKGROUP-CREDENTIALS`](soup/signal-interop.md#31-active-functional--capacity-envelope)) as a Class C SOUP component quarantined exclusively within `Percolator.Infrastructure`. The C# wrapper provides memory-safe `SafeHandle` abstractions while delegating curve math to upstream audited Rust `libsignal`.
   - **Rejected Alternatives:**
     1. *Hand-rolled pure C# Ed25519 & zkgroup curve arithmetic:* Rejected due to high risk of cryptographic flaws, lack of formal audit/fuzzing pedigree, and timing side-channel vulnerabilities.
     2. *Direct P/Invoke interop throughout Application/Domain layers:* Rejected because leaking unmanaged handles (`IntPtr`), native exceptions, or FFI DTOs violates Hexagonal/Onion DDD architecture and IEC 62304 Clause 5.3.5 segregation.
     3. *Compiling full Rust libsignal codebase into Percolator solution:* Rejected due to massive build-toolchain complexity (cargo/rustc cross-compilation within .NET CI/CD pipelines); decoupling via a pre-compiled SOUP shim keeps Percolator focused.
   - **Safety, Risk, and Security Impact:** Inherent safety control (ISO 14971 Clause 7.1) for cryptographic soundness. Directly mitigates [`HAZ-KEY-SUBSTITUTION-MITM`](RiskRegister.md#1-cryptographic-identity-and-privacy-hazards) and [`HAZ-SESSION-KEY-COMPROMISE`](RiskRegister.md#1-cryptographic-identity-and-privacy-hazards). Enforces IEC 62304 Class C quarantine through `SignalCryptoEngine` and `ZkgroupCryptographyService`.

2. **`DEC-SEALED-SENDER-TRANSPORT` (Unidentified Sealed Sender Routing with Inner-Payload Authentication):**
   - **Status & Review Record:** `Approved` (2026-10-30 | Security Review)
   - **Context & Problem Statement:** Relaying encrypted envelopes across untrusted network intermediaries risks social-graph leakage if transport or envelope headers reveal the sender's identity ([`SYS-BLINDED-RELAY-ENVELOPES`](SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).
   - **Decision & Technical Rationale:** Implement the Signal Sealed Sender protocol routing model where clients transmit envelopes using unauthenticated access tokens derived from recipient profile keys ([`FACT-SIGNAL-SEALED-SENDER-ACCESS-KEY`](facts/signal-sealed-sender.md#2-axioms-and-platform-invariants), [`FACT-SIGNAL-SEALED-SENDER-UNAUTH-CHANNEL`](facts/signal-sealed-sender.md#2-axioms-and-platform-invariants)). The sender's identity and Ed25519 digital signature ([`SOUP-SIGNAL-ED25519-SIGN`](soup/signal-interop.md#31-active-functional--capacity-envelope)) reside exclusively inside the encrypted payload ([`FACT-SIGNAL-SEALED-SENDER-INNER-AUTHENTICATION`](facts/signal-sealed-sender.md#2-axioms-and-platform-invariants)).
   - **Rejected Alternatives:**
     1. *Authenticated Relay Transport (Client TLS / Bearer Tokens):* Rejected because transport authentication links sender network sessions to recipient delivery tokens at the relay layer, exposing communication metadata.
     2. *Tor Onion Routing for All Frames:* Rejected as the primary transport due to high latency, mobile battery drain, and unreliability for real-time mobile messaging, while sealed sender provides metadata minimization over standard IP transport.
   - **Safety, Risk, and Security Impact:** Inherent safety control (ISO 14971 Clause 7.1) directly mitigating [`HAZ-RELAY-HEADER-CORRELATION`](RiskRegister.md#1-cryptographic-identity-and-privacy-hazards) and [`HAZ-PREKEY-POOL-STARVATION`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards).

## 3. Integration, Transport, and Platform Decisions

1. **`DEC-RESULT-ORIENTED-HOTPATH` (Result-Driven Error Handling over CLR Exceptions):**
   - **Status & Review Record:** `Approved` (2026-10-30 | Architecture Review)
   - **Context & Problem Statement:** Validating high-throughput network packets, cryptographic signatures, and relay queue tokens on the message hot path triggers severe latency and memory fragmentation if expected rejections throw CLR exceptions ([`FACT-CLR-EXCEPTION-HOTPATH-OVERHEAD`](facts/platform-runtime.md#2-axioms-and-runtime-invariants)).
   - **Decision & Technical Rationale:** Enforce value-type `DomainResult<T>` and `Result<T>` patterns for all predictable wire validation, signature verification ([`SOUP-SIGNAL-ED25519-VERIFY`](soup/signal-interop.md#31-active-functional--capacity-envelope)), and rate-limiting operations across public deployable boundaries ([`SYS-RESULT-DRIVEN-INGRESS`](SystemRequirements.md#3-external-interfaces-and-resource-boundaries)). Exceptions are reserved strictly for unrecoverable programmer defects or infrastructure crashes.
   - **Rejected Alternatives:**
     1. *Idiomatic .NET Exception Throwing for Validation:* Rejected due to 1,000–10,000 cycle unmanaged stack-walk overhead and GC object allocation per invalid frame, enabling Denial of Service under network packet flooding.
   - **Safety, Risk, and Security Impact:** Protective control (ISO 14971 Clause 7.1) mitigating [`HAZ-INGRESS-RESOURCE-EXHAUSTION`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards).
