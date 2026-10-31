# Percolator.PluginSdk

`Percolator.PluginSdk` represents the **microkernel extension boundary** of the Percolator ecosystem. It defines the public contracts, interfaces, and data models through which pluggable applications integrate with the host platform and communicate across secure, end-to-end encrypted channels.

In accordance with Onion Architecture principles, this library serves as a pure contract layer that decouples pluggable applications from host orchestration, transport adapters, and storage engines.

For formal layer boundaries, microkernel contract specifications, and technical design decisions, see the project [Design Document](Design.md).

---

## 1. Architectural Role & Invariants

### 1.1 Inward Dependency Rule
`Percolator.PluginSdk` depends **strictly and solely** on `Percolator.Domain`:
- **Zero Technical Infrastructure:** Contains no references to database engines (SQLite/SQLCipher), network frameworks (gRPC/HTTP), binary wire format compilers (Protobuf), or platform file systems.
- **Pure Microkernel Boundary:** Host applications implement the service interfaces defined in this SDK, while technical adapters in infrastructure realize network and storage capabilities. Pluggable applications consume these interfaces without direct dependency on host internals.

### 1.2 Application Payload Isolation
Communication in Percolator is multi-tenant and microkernel-driven:
- **Application Segmentation:** Every plugin registers under a distinct application identifier (`AppId`). Payloads are tagged and routed exclusively to their designated plugin handlers.
- **Cross-App Isolation:** Content from different applications is strictly partitioned. Interactive chat feeds, peer discovery announcements, and large-file distribution swarms operate over separate virtual streams and never cross-contaminate.

### 1.3 Cryptographic Agnosticism for Plugins
Pluggable applications focus on domain-specific feature logic without managing low-level cryptographic state:
- The host system owns Double Ratchet state machines, pairwise session initialization, group sender keys, and cryptographic epoch management.
- Plugins handle structured application payloads and data transfer contracts, trusting the host to guarantee confidentiality, authenticity, and forward secrecy.

---

## 2. Scope & Layer Boundaries

For the complete specification of in-scope SDK abstractions versus outer layer responsibilities, refer to [Design.md](Design.md#1-purpose-and-layer-boundary).

In brief:
- **In Scope**: Plugin registration and lifecycle contracts (`IPercolatorAppPlugin`), payload ingress/egress models, serialization port (`IPayloadSerializer`), out-of-band blob storage contracts (`IMediaBlobService`, `BlobReference`), and presentation timeline sinks (`ITimelinePostSink`).
- **Out of Scope**: Core domain business invariants, use-case orchestration (outbox queues, retries), network clients, physical file/database persistence, and concrete plugin feature logic.

---

## 3. Key Architectural Patterns

### 3.1 The Payload Envelope Invariant
End-to-end encrypted messaging channels enforce a strict upper bound on in-flight payload sizes (typically 64 KB). This invariant protects memory pools, bounds transactional outbox storage, and prevents head-of-line blocking in message ratchets:
- Small metadata, voice snippets, and visual previews travel directly within the ratcheted message envelope.
- Larger media payloads are encrypted client-side with ephemeral symmetric keys and transferred out-of-band.
- The ratchet envelope carries only a lightweight reference containing content-addressed hashes, decryption keys, unpadded lengths, and layout descriptors.

### 3.2 Topology-Driven Media Expectations
The SDK defines transfer abstractions that align with the physical realities of decentralized networks:
- **Direct Peer-to-Peer:** Media transfer is strictly synchronized; both peers must be online simultaneously to stream bytes directly without intermediate relay storage.
- **Relayed Channels:** Asynchronous delivery is mediated by the channel's designated relay, which serves as the authoritative blind ciphertext cache for the channel's retention window.

### 3.3 Privacy & Cryptographic Safety Posture
- **Metadata Sanitization:** Media transfer contracts mandate client-side stripping of invasive file metadata (GPS coordinates, device identifiers) before payload encryption.
- **Anti-Fingerprinting Padding:** Plaintext payloads are padded to discrete size buckets prior to encryption to defeat passive traffic analysis.
- **Log Hygiene:** Descriptor models redact sensitive symmetric keys or nonces in text representations, preventing inadvertent key leakage into diagnostic logs.

---

## 4. Architectural Hierarchy

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
