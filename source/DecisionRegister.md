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

2. **`DEC-CHUNKED-STREAM-AEAD-TRANSFER` (Chunked 64 KB STREAM AEAD & Merkle-Rooted Bulk Transfer):**
   - **Status & Review Record:** `Approved` (2026-10-31 | Architecture Review)
   - **Context & Problem Statement:** Bulk data, media blobs, and multi-gigabyte file transfers must execute over untrusted networks with integrity verification, corruption recovery, and bounded RAM consumption ([`SYS-STREAM-AEAD-CHUNKED-TRANSFER`](SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).
   - **Decision & Technical Rationale:** Stream bulk data in discrete 64 KB blocks using chunked AES-256-GCM AEAD encryption with deterministic sequential nonces and authenticated final-block flags. Verify individual chunks against a pre-computed SHA-256 Merkle tree leaf array, writing received pieces atomically via temporary staging files (`.percolator-part` / `.tmp`). This ensures constant $O(1)$ memory usage and enables immediate per-chunk corruption detection without restarting entire multi-gigabyte transfers.
   - **Rejected Alternatives:**
     1. *In-Memory Whole-File Encryption / Buffering:* Rejected due to severe memory exhaustion (OOM crashes) when handling files exceeding 500 MB.
     2. *Unauthenticated TCP Byte Streaming with End-of-File Hash:* Rejected because bitflips or network corruption cannot be localized, requiring complete transfer restarts upon failure.
   - **Safety, Risk, and Security Impact:** Inherent safety control (ISO 14971 Clause 7.1) mitigating [`HAZ-INGRESS-RESOURCE-EXHAUSTION`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards) and [`HAZ-CRASH-STATE-DESYNC`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards).

3. **`DEC-PENDING-INGRESS-SPOOL` (Persist-Then-Ack Local Ingress Spool for Mailbox Retrieval):**
   - **Status & Review Record:** `Proposed`
   - **Context & Problem Statement:** Offline relay delivery ([`SYS-ASYNC-STORE-AND-FORWARD`](SystemRequirements.md#1-functional-system-capabilities)) requires a durable receipt boundary on the inbound path symmetric to the outbound transactional queue ([`SYS-ATOMIC-STATE-PERSISTENCE`](SystemRequirements.md#2-security-privacy-and-risk-control-invariants)). The existing retrieval surface conflated *fetch* with *process*: a destructive drain deleted relay rows before the recipient processed them, while acknowledging only after processing lets a single undecryptable envelope either stall the delivery stream or replay out of order — disordering sequential ratchet state and bounding recovery by relay TTL.
   - **Decision & Technical Rationale:** Relay acknowledgement means *durable receipt*, not *processed*. Each retrieved sealed envelope is first upserted into a vault-resident pending-ingress spool keyed by its relay queue identifier (deduplicating redelivery), only then acknowledged to the relay, and finally processed through unwrap and ingress; the spool record is deleted on success or deterministic terminal rejection, retry-marked with an attempt budget on transient faults, and replayed in receive order on the next session connection before live envelopes are consumed. The relay queue remains a pure transport spool; the local encrypted store is the client's durability point — the ingress complement of the established outbound transactional-outbox pattern.
   - **Rejected Alternatives:**
     1. *Destructive unary drain (fetch-deletes-before-return):* Rejected because process termination between deletion and processing silently loses envelopes — the exact desynchronization window [`HAZ-CRASH-STATE-DESYNC`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards) requires eliminating.
     2. *Acknowledge-after-processing (relay-as-spool):* Rejected because a poison envelope either head-of-line blocks all subsequent delivery or must be skipped and later replays after its successors already processed — disordering sequential cryptographic session state and ceding retry bounds to relay-side expiry.
   - **Safety, Risk, and Security Impact:** Inherent safety control (ISO 14971 Clause 7.1) mitigating [`HAZ-CRASH-STATE-DESYNC`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards): the crash window between relay deletion and local persistence is structurally removed rather than masked by retries. Allocated to the inbound retrieval ports under [`DEP-INFRA-MAILBOX-RETRIEVAL`](Percolator.Infrastructure/Requirements.md#2-allocated-responsibilities).

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

3. **`DEC-BLINDED-RENDEZVOUS-ROTATION` (Rotating Ephemeral Blinded Rendezvous Locators):**
   - **Status & Review Record:** `Approved` (2026-10-31 | Security Review)
   - **Context & Problem Statement:** Staging discovery and contact exchange routes on untrusted relay servers exposes persistent user locators to linkability and traffic correlation over time ([`SYS-RELAY-ONLY-CLOAKING`](SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).
   - **Decision & Technical Rationale:** Implement rotating blinded rendezvous locators derived via HMAC-SHA256 over 24-hour UTC day epochs ($E = \lfloor\text{UnixSeconds}/86400\rfloor$) with a 3-epoch clock tolerance window $[E-1, E, E+1]$. Rendezvous route payloads are encrypted via AES-256-GCM using an HKDF-derived key that binds the 32-byte `BlindedLocator` as Associated Authenticated Data (AAD) to prevent route transplantation across locators.
   - **Rejected Alternatives:**
     1. *Static Public Rendezvous Keys:* Rejected because unchanging locator keys allow relay operators to track peer query patterns and correlate rendezvous frequency indefinitely.
     2. *Unauthenticated Plaintext Routing Descriptors:* Rejected because untrusted relays could tamper with candidate IPs or inject forged rendezvous endpoints.
   - **Safety, Risk, and Security Impact:** Inherent safety control (ISO 14971 Clause 7.1) directly mitigating [`HAZ-RELAY-HEADER-CORRELATION`](RiskRegister.md#1-cryptographic-identity-and-privacy-hazards) and [`HAZ-PRESENCE-AND-BLOCK-ORACLE`](RiskRegister.md#1-cryptographic-identity-and-privacy-hazards).

## 3. Integration, Transport, and Platform Decisions

1. **`DEC-RESULT-ORIENTED-HOTPATH` (Result-Driven Error Handling over CLR Exceptions):**
   - **Status & Review Record:** `Approved` (2026-10-30 | Architecture Review)
   - **Context & Problem Statement:** Validating high-throughput network packets, cryptographic signatures, and relay queue tokens on the message hot path triggers severe latency and memory fragmentation if expected rejections throw CLR exceptions ([`FACT-CLR-EXCEPTION-HOTPATH-OVERHEAD`](facts/platform-runtime.md#2-axioms-and-runtime-invariants)).
   - **Decision & Technical Rationale:** Enforce value-type `DomainResult<T>` and `Result<T>` patterns for all predictable wire validation, signature verification ([`SOUP-SIGNAL-ED25519-VERIFY`](soup/signal-interop.md#31-active-functional--capacity-envelope)), and rate-limiting operations across public deployable boundaries ([`SYS-RESULT-DRIVEN-INGRESS`](SystemRequirements.md#3-external-interfaces-and-resource-boundaries)). Exceptions are reserved strictly for unrecoverable programmer defects or infrastructure crashes.
   - **Rejected Alternatives:**
     1. *Idiomatic .NET Exception Throwing for Validation:* Rejected due to 1,000–10,000 cycle unmanaged stack-walk overhead and GC object allocation per invalid frame, enabling Denial of Service under network packet flooding.
   - **Safety, Risk, and Security Impact:** Protective control (ISO 14971 Clause 7.1) mitigating [`HAZ-INGRESS-RESOURCE-EXHAUSTION`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards).

2. **`DEC-CANONICAL-PORT-RANGE` (Canonical Port Range 5100–5199 for Peers and Relays):**
   - **Status & Review Record:** `Approved` (2026-11-18 | Architecture Review)
   - **Context & Problem Statement:** Dynamic peer listening and relay hosting require a deterministic, bounded port allocation range that avoids collisions with well-known IANA services (0–1023), registered application ports (e.g., 5000/5001 ASP.NET defaults), and wide ephemeral dynamic ranges ([`SYS-MUTUAL-AUTHENTICATED-WIRE-TRANSPORT`](SystemRequirements.md#3-external-interfaces-and-resource-boundaries)). Previous revisions loosely referenced 5100–5299.
   - **Decision & Technical Rationale:** Standardize Percolator's canonical listening range to `5100–5199` inclusive (100 discrete ports) for both Peer nodes and Relay nodes. Domain value object `PortRange.DefaultDynamicRange` defaults to `[5100, 5199]`. `IPortReservationService` probes and reserves sequential loopback/LAN ports within this boundary. Direct peer invitations omit the port or specify an explicit sub-range within `[5100, 5199]`.
   - **Rejected Alternatives:**
     1. *Unbounded Ephemeral Allocation (Port 0 / 49152–65535):* Rejected because firewalls and router NAT pinholes cannot be pre-configured, and invitation payloads would require broadcasting ephemeral port shifts.
     2. *Wide 5100–5299 Range (200 ports):* Rejected as unnecessarily wide; 100 ports is more than sufficient for multi-persona developer workspaces while tightening the local port-scanning attack surface.
   - **Safety, Risk, and Security Impact:** Protective control (ISO 14971 Clause 7.1) mitigating [`HAZ-INGRESS-RESOURCE-EXHAUSTION`](RiskRegister.md#2-endpoint-availability-and-integrity-hazards) and port collision denial-of-service.

3. **`DEC-CANONICAL-URI-SCHEME` (Unified `percolator://` Application and Network URI Scheme):**
   - **Status & Review Record:** `Approved` (2026-11-18 | Architecture Review)
   - **Context & Problem Statement:** Peer invitation payloads and relay endpoint descriptors previously used inconsistent schemes across presentation and transport layers (`percolator://`, `relay://`, and `https://`), complicating OS protocol registration and violating user interface cohesion ([`SYS-MUTUAL-AUTHENTICATED-WIRE-TRANSPORT`](SystemRequirements.md#3-external-interfaces-and-resource-boundaries)).
   - **Decision & Technical Rationale:** Enforce `percolator://` as the sole canonical URI scheme for all Percolator network and out-of-band links: `percolator://invite?...` for peer invitations and `percolator://{host}:{port}` for relay endpoints. The OS registers a single application protocol handler (`percolator://`). Outward infrastructure transport adapters (`PeerGrpcChannelFactory`) transparently translate `percolator://` endpoints to underlying HTTP/2 TLS sockets (`https://`) exclusively when invoking the .NET gRPC client, ensuring all application, domain, and presentation surfaces remain decoupled from transport-specific scheme requirements.
   - **Rejected Alternatives:**
     1. *Dual Schemes (`percolator://` for peers and `relay://` for relays):* Rejected because relays and peers share the same transport layer and crypto protocol (relays are network topology roles, not distinct protocols), and registering multiple custom protocol handlers with host operating systems creates installation friction and link routing failures.
     2. *Raw `https://` Schemes:* Rejected because generic HTTPS URIs trigger web browsers rather than launching or routing to the Percolator desktop application, leaking user network topology to browser process pools.
   - **Safety, Risk, and Security Impact:** Protective control (ISO 14971 Clause 7.1) mitigating [`HAZ-TRANSPORT-SPOOFING`](RiskRegister.md#1-cryptographic-identity-and-privacy-hazards) and host protocol confusion.

