# Percolator.Apps.FileTransfer

`Percolator.Apps.FileTransfer` is the **application and use-case orchestration layer** of the File Transfer plugin within the Percolator Onion Architecture. It bridges the pure mathematical and state-machine invariants of `Percolator.Apps.FileTransfer.Domain` with the microkernel contracts of `Percolator.PluginSdk`, coordinating end-to-end encrypted control negotiation alongside high-throughput, out-of-band multi-stream bulk data transfer.

For formal layer boundaries, outbound port contracts, and technical design decisions (such as dual-plane bifurcation, verify-before-write chunk processing, and silent pre-consent staging), see the project [Design Document](Design.md).

---

## 1. Architectural Role & Invariants

### 1.1 Hexagonal Port & Adapter Boundary
`Percolator.Apps.FileTransfer` depends strictly inward on `Percolator.Apps.FileTransfer.Domain`, `Percolator.Domain`, and `Percolator.PluginSdk`:
- **Zero Technical Infrastructure:** Contains no direct references to operating system file streams, network sockets (TCP/QUIC), UPnP/STUN libraries, binary wire format compilers (Protobuf), or relational database engines (SQLite/SQLCipher).
- **Port-Driven Orchestration:** All external side effects—random-access disk I/O, socket stream pooling, distributed swarm tracker announcements, bandwidth rate limiting, session persistence, and wire serialization—are expressed as outbound ports implemented by infrastructure adapters.

### 1.2 Dual-Plane Architecture (Control vs. Bulk Data)
To preserve the real-time responsiveness and cryptographic safety of the host messaging pipeline, file transfer traffic is strictly bifurcated into two isolated planes:
- **In-Band Control Plane:** Lightweight negotiation envelopes (transfer offers, selective acceptances, and cancellations) travel over the host's end-to-end encrypted messaging channels (`64 KB` maximum envelope budget).
- **Out-of-Band Bulk Data Plane:** High-bandwidth file chunks bypass the messaging ratchet entirely, flowing over dedicated, multiplexed peer-to-peer streams or blind relay pipes secured with ephemeral session keys and per-chunk authenticated encryption.

### 1.3 Zero-Allocation Hot Path & Verify-Before-Write
High-bandwidth transfers (scaling up to `16 TB` per bundle) require strict memory and disk hygiene:
- **Pooled Buffer Pipeline:** Chunk reads, encryption, decryption, and Merkle tree assembly rent and return buffers from shared memory pools (`ArrayPool<byte>.Shared`), avoiding Large Object Heap (LOH) allocations and GC pause spikes.
- **Single-Pass Cryptographic Verification Before Disk Write:** Every inbound chunk is authenticated via AES-256-GCM and verified against its expected Merkle leaf hash in memory *before* any byte slice is written to disk. Unsolicited, unauthenticated, or corrupted chunks are dropped immediately with zero disk I/O.

---

## 2. Scope & Layer Boundaries

For the complete specification of in-scope orchestration versus domain invariants and infrastructure adapters, refer to [Design.md](Design.md#1-purpose-and-layer-boundary).

In brief:
- **In Scope**: Ingress filtering & edge kill-switches, control plane offer/accept/cancel negotiation, adaptive chunk sizing (`256 KB`–`4 MB`), disk space preflight checks, rarest-first chunk scheduling, per-chunk AES-256-GCM encryption, swarm keep-alives, and PEX candidate filtering.
- **Out of Scope**: Core Merkle mathematics and bitfield set algebra (owned by `Percolator.Apps.FileTransfer.Domain`), host channel encryption, Protobuf schema compilation, and physical OS socket/file handle management.

---

## 3. Core Protocol Workflows

### 3.1 Offer Creation & Manifest Distribution
1. **Source Scanning & Adaptive Chunking:** The application queries storage for file sizes and timestamps, maps files onto a contiguous virtual byte range, and selects an optimal chunk size (`256 KB` to `4 MB`).
2. **Merkle Tree Construction:** Source files are read sequentially into pooled memory buffers to compute leaf hashes and construct the bundle's Merkle tree.
3. **Inline vs. Out-of-Band Manifest:** If the serialized manifest fits within `48 KB`, it is embedded directly inside the in-band offer payload. If it exceeds `48 KB` (up to `8 MB`), it is uploaded via blob storage and referenced by a compact cryptographic descriptor.
4. **In-Band Dispatch:** The offer payload—carrying bundle summary, manifest descriptor, ephemeral session encryption key, base nonce, stream auth token, and direct endpoint candidates—is dispatched over the host channel.

### 3.2 Zero-Leakage Ingress & Pre-Consent Staging
1. **Edge Gatekeeping:** Inbound control frames pass through asynchronous ingress filters. If file transfer ingress is globally disabled or the sender is blocked, the frame is silently discarded.
2. **Structural & Bounds Validation:** Incoming offers are validated against strict ceilings (maximum `16 TB` bundle, `50,000` files, `1,000,000` chunks). Remote manifest downloads are strictly bounded.
3. **Silent Staging:** Validated offers are staged locally in `PendingConsent` without emitting automatic network acknowledgments, preventing passive presence probing prior to explicit user consent.

### 3.3 Selective Acceptance & Multi-Stream Transfer
1. **Consent & Pre-Allocation:** Upon user acceptance, the application verifies disk space, immediately finalizes zero-byte files, pre-allocates remaining files, and sends an in-band acceptance carrying the resumption bitfield.
2. **Symmetric Stream Handshake:** Out-of-band data streams authenticate using a constant-time comparison of the 32-byte stream authentication token and verify authorized swarm membership.
3. **Pipelined Rarest-First Streaming:** Concurrent workers pipeline up to 32 in-flight requests per peer using rarest-first piece selection.
4. **Cryptographic Verification & Poisoned-Peer Defense:** Each chunk is decrypted and verified against the Merkle tree in memory prior to disk write. Corrupted chunks cause immediate peer choking, stream termination, and chunk rescheduling.

### 3.4 Swarm Coordination & Seeding Completion
- **Privacy-Preserving Endpoint Discovery:** Direct WAN endpoint candidates (UPnP/STUN) are excluded by default and only advertised when permitted by session policy. Incoming PEX frames are rate-limited and scrubbed of invalid endpoints.
- **Keep-Alive & Dead-Peer Pruning:** Broadcasts keep-alives across idle streams (`30s`) and prunes peers silent beyond `60s`, releasing in-flight chunks.
- **Seeding Quota Governance:** Outgoing and seeding sessions track cumulative uploaded bytes against the configured target seed ratio, unregistering from trackers when goals are met.

---

## 4. Architectural Hierarchy

```
┌─────────────────────────────────────────────────────────┐
│                Percolator.Infrastructure                │
│   (Protobuf, Disk I/O, TCP/QUIC Streams, SQLite Repo)   │
└───────────────────────────┬─────────────────────────────┘
                            │ implements ports
┌───────────────────────────▼─────────────────────────────┐
│             Percolator.Apps.FileTransfer                │
│  (Negotiation, Ingress Policy, Multi-Stream, Swarm)     │
└─────────────┬─────────────────────────────┬─────────────┘
              │                             │
              │ orchestrates                │ integrates via
┌─────────────▼───────────────┐ ┌───────────▼─────────────┐
│ Percolator.Apps.FileTransfer│ │  Percolator.PluginSdk   │
│           .Domain           │ │  & Percolator.Domain    │
│ (Aggregates, Merkle, Math)  │ │ (Host Microkernel SDK)  │
└─────────────────────────────┘ └─────────────────────────┘
```
