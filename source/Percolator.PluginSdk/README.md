# Percolator.PluginSdk

`Percolator.PluginSdk` represents the **microkernel extension boundary** of the Percolator ecosystem. It defines the public contracts, interfaces, and data models through which pluggable applications integrate with the host platform and communicate across secure, end-to-end encrypted channels.

In accordance with Onion Architecture principles, this library serves as a pure contract layer that decouples pluggable applications from the underlying host orchestration, transport adapters, and storage engines.

---

## 1. Architectural Role & Invariants

### 1.1 Inward Dependency Rule
`Percolator.PluginSdk` depends **strictly and solely** on `Percolator.Domain`:
- **Zero Technical Infrastructure:** Contains no references to database engines (SQLite/SQLCipher), network frameworks (gRPC/HTTP), binary wire format compilers (Protobuf), or platform file systems.
- **Pure Microkernel Boundary:** Host applications implement the service interfaces defined in this SDK, while technical adapters in infrastructure realize network and storage capabilities. Pluggable applications consume these interfaces without direct dependency on host internals.

### 1.2 Application Payload Isolation
Communication in Percolator is multi-tenant and microkernel-driven:
- **Application Segmentation:** Every plugin registers under a distinct application identifier. Payloads are tagged and routed exclusively to their designated plugin handlers.
- **Cross-App Isolation:** Content from different applications is strictly partitioned. Interactive chat feeds, peer discovery announcements, and large-file distribution swarms operate over separate virtual streams and never cross-contaminate.

### 1.3 Cryptographic Agnosticism for Plugins
Pluggable applications focus on domain-specific feature logic without managing low-level cryptographic state:
- The host system owns Double Ratchet state machines, pairwise session initialization, group sender keys, and cryptographic epoch management.
- Plugins handle structured application payloads and data transfer contracts, trusting the host to guarantee confidentiality, authenticity, and forward secrecy.

---

## 2. What Belongs in PluginSdk (In Scope)

The SDK encompasses the abstractions and contracts needed for modular extensions to function atop the secure communication fabric:

| Area | Scope & Conceptual Responsibilities |
| :--- | :--- |
| **Plugin Registration & Lifecycle** | Contracts enabling the host microkernel to discover, register, version, and route to independent application plugins. |
| **Payload Ingress & Egress** | Contracts for handling inbound decrypted payloads delivered to a specific application, and models for submitting outbound payloads into the host delivery pipeline. |
| **Serialization Abstraction** | Ports decoupling application data models from concrete binary wire encodings. Plugins interact with structured domain models, while concrete serialization engines remain external. |
| **Delivery Routing Topology** | High-level delivery routing markers indicating whether an interaction is directed peer-to-peer or routed through store-and-forward channel relays. |
| **Out-of-Band Blob Storage Contracts** | Public service interfaces and data transfer models for handling medium-to-large encrypted attachments (photos, audio, video) that exceed end-to-end messaging payload limits. |
| **Envelope Descriptors** | Compact, cryptographically safe reference models carried inside message envelopes to locate, authenticate, and decrypt out-of-band blobs. |
| **Transfer Telemetry & Progress** | Strongly typed, allocation-conscious progress reporting structures exposing byte throughput, completion fractions, and cancellation hooks for asynchronous operations. |

---

## 3. What Does NOT Belong in PluginSdk (Out of Scope)

The following concerns are explicitly excluded from `Percolator.PluginSdk` and belong to other architectural layers:

| Excluded Concern | Responsible Layer | Architectural Reason |
| :--- | :--- | :--- |
| **Core Domain Entities & Invariants** | `Percolator.Domain` | Business rules, channel membership governance, aggregate roots, and cryptographic protocol state machines belong in the domain core. |
| **Use Case Orchestration & Routing** | Application Layer | Coordinating message outboxes, managing active peer streams, orchestrating retries, and enforcing channel locking belong in application services. |
| **Network Clients & Sockets** | Infrastructure Layer | gRPC streaming clients, HTTP endpoints, socket listeners, and TLS handshake management are transport details. |
| **Persistence & File Storage** | Infrastructure Layer | Writing chunks to disk, managing SQLite databases, and executing background cleanup cron jobs belong in technical storage adapters. |
| **Cryptographic Engine Realization** | Infrastructure Layer | Low-level implementations of symmetric ciphers, chunked authenticated encryption, and digital signatures belong in cryptographic adapters. |
| **Plugin Feature Implementations** | Application Plugins | Concrete feature logic, chat timeline formatting, link parsing, reaction aggregations, and UI presentation belong in individual application modules. |

---

## 4. Key Architectural Patterns

### 4.1 The Payload Envelope Invariant
End-to-end encrypted messaging channels enforce a strict upper bound on in-flight payload sizes (typically 64 KB). This invariant protects memory pools, bounds transactional outbox storage, and prevents head-of-line blocking in message ratchets.

`Percolator.PluginSdk` enables media distribution by maintaining this invariant:
- Small metadata, voice snippets, and visual previews travel directly within the ratcheted message envelope.
- Larger media payloads are encrypted client-side with ephemeral symmetric keys and transferred out-of-band.
- The ratchet envelope carries only a lightweight reference containing content-addressed hashes, decryption keys, unpadded lengths, and layout descriptors.

### 4.2 Topology-Driven Media Expectations
The SDK defines transfer abstractions that align with the physical realities of decentralized networks:
- **Direct Peer-to-Peer:** Media transfer is strictly synchronized; both peers must be online simultaneously to stream bytes directly without intermediate relay storage.
- **Relayed Channels:** Asynchronous delivery is mediated by the channel's designated relay, which serves as the authoritative blind ciphertext cache for the channel's retention window.

### 4.3 Privacy & Cryptographic Safety Posture
- **Metadata Sanitization:** Media transfer contracts mandate the client-side stripping of invasive file metadata (such as GPS coordinates and device identifiers) before payload encryption.
- **Anti-Fingerprinting Padding:** Plaintext payloads are padded to discrete size buckets prior to encryption to defeat passive traffic analysis and file-size matching.
- **Log Hygiene:** Descriptor models must never expose sensitive symmetric keys or nonces in text representations, preventing inadvertent key leakage into diagnostic or telemetry logs.

---

## 5. Architectural Hierarchy

```
┌────────────────────────────────────────────────────────┐
│               Application Plugins                      │
│     (Percolator.Apps.Chat, Discovery, etc.)            │
└───────────────────────────┬────────────────────────────┘
                            │ consumes
┌───────────────────────────▼────────────────────────────┐
│                  Percolator.PluginSdk                  │
│       (Public Microkernel Contracts & DTOs)            │
└───────────────────────────┬────────────────────────────┘
                            │ depends on
┌───────────────────────────▼────────────────────────────┐
│                   Percolator.Domain                    │
│    (Core Business Invariants & Cryptographic Rules)    │
└────────────────────────────────────────────────────────┘
```

The host application layer (`Percolator.Application`) implements the service interfaces defined in `Percolator.PluginSdk`, while technical adapters in `Percolator.Infrastructure` provide concrete implementations for persistence, wire serialization, and network streaming.
