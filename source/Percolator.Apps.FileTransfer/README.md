# Percolator.Apps.FileTransfer

`Percolator.Apps.FileTransfer` is the **application and use-case orchestration layer** of the File Transfer plugin within the Percolator Onion Architecture. It bridges the pure mathematical and state-machine invariants of `Percolator.Apps.FileTransfer.Domain` with the microkernel contracts of `Percolator.PluginSdk`, coordinating end-to-end encrypted control negotiation alongside high-throughput, out-of-band multi-stream bulk data transfer.

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
- **Pooled Buffer Pipeline:** Chunk reads, encryption, decryption, and Merkle tree assembly rent and return buffers from shared memory pools, avoiding Large Object Heap (LOH) allocations and GC pause spikes during multi-stream saturation.
- **Single-Pass Cryptographic Verification Before Disk Write:** Every inbound chunk is authenticated via AES-256-GCM and verified against its expected Merkle leaf hash in memory *before* any byte slice is written to disk. Unsolicited, unauthenticated, or corrupted chunks are dropped immediately with zero disk I/O.

---

## 2. What Belongs in This Layer (In Scope)

This project orchestrates the application workflows, security policies, and streaming protocols required to negotiate and execute file transfers:

| Area | High-Level Scope & Responsibilities |
| :--- | :--- |
| **Ingress Filtering & Policy Enforcement** | Evaluating inbound control frames at the edge against user-configured kill-switches, sender blocklists, and relay endpoint policies, silently dropping disallowed traffic without generating network acknowledgments. |
| **Control Plane Negotiation** | Constructing, validating, and handling in-band offer, accept, and cancel payloads; managing inline (`<= 48 KB`) vs. remote blob (`<= 8 MB`) manifest distribution; and persisting session transport context across lifecycle transitions. |
| **Adaptive Bundle Preparation** | Scanning source file metadata, selecting optimal chunk sizes (`256 KB` to `4 MB`) scaled to total bundle size, computing Merkle leaf hashes via pooled buffers, and initiating 1-to-1 or multi-peer swarm sessions. |
| **Selective Consent & Disk Preflight** | Calculating exact disk space requirements for user-selected files within a multi-file bundle, enforcing preflight storage checks, immediately finalizing zero-byte files, and pre-allocating non-empty target files prior to streaming. |
| **Multi-Stream Chunk Engine** | Coordinating concurrent upload and download workers across parallel transport streams, enforcing per-peer in-flight request window limits, scheduling missing chunks via rarest-first piece selection, and throttling throughput via rate-limiter ports. |
| **Per-Chunk Authenticated Encryption** | Deriving deterministic 96-bit chunk nonces (`BaseNonce XOR BigEndianUInt96(ChunkIndex)`) and executing single-buffer AES-256-GCM encryption and decryption over wire chunks. |
| **Contiguous Disk Reassembly** | Translating verified contiguous bundle chunks into exact per-file byte slices and dispatching random-access writes to the desktop storage port. |
| **Swarm Liveness & Peer Exchange (PEX)** | Managing peer heartbeat intervals, pruning dead swarm peers, rate-limiting incoming Peer Exchange announcements, filtering LAN/WAN endpoint candidates against local privacy policies, and unregistering tracker endpoints when seeding quotas complete. |
| **Stale Offer Reaping & Read Projections** | Periodically expiring unanswered pre-consent offers via a deterministic clock abstraction and projecting active session state into immutable read models for UI dashboards. |

---

## 3. What Does NOT Belong in This Layer (Out of Scope)

The following concerns are explicitly excluded from `Percolator.Apps.FileTransfer`:

| Excluded Concern | Responsible Layer | Architectural Reason |
| :--- | :--- | :--- |
| **Core Aggregate Invariants & Merkle Math** | `Percolator.Apps.FileTransfer.Domain` | Pure state machines, bitfield set algebra, binary-search slice geometry, and domain-separated SHA-256 Merkle verification belong in the innermost domain core. |
| **Double Ratchet & Channel Cryptography** | `Percolator.Domain` / `Percolator.Application` | Host channel encryption, outbox queuing, and device fanout are managed transparently by the host messaging pipeline. |
| **Wire Schema Compilation (`.proto`)** | `Percolator.Infrastructure` | Protobuf contract definitions and binary wire encoders/decoders live in infrastructure behind the application serializer port. |
| **OS Socket & File Handle Management** | `Percolator.Infrastructure` | Physical TCP/QUIC sockets, NAT traversal (UPnP/STUN), sparse file system calls, and SQLite persistence adapters belong in infrastructure. |

---

## 4. Core Protocol Workflows

### 4.1 Offer Creation & Manifest Distribution
1. **Source Scanning & Adaptive Chunking:** When a user initiates a transfer, the application queries the storage port for file sizes and timestamps, maps the files onto a contiguous virtual byte range, and selects an optimal chunk size (`256 KB` for small/medium bundles up to `4 MB` for multi-gigabyte/terabyte bundles).
2. **Merkle Tree Construction:** Source files are read sequentially into pooled memory buffers to compute leaf hashes and construct the bundle's Merkle tree.
3. **Inline vs. Out-of-Band Manifest:**
   - If the serialized manifest fits within `48 KB`, it is embedded directly inside the in-band offer payload.
   - If the serialized manifest exceeds `48 KB` (up to the `8 MB` remote manifest cap), it is uploaded once via the host blob storage service, and a compact cryptographic blob descriptor is embedded in the offer envelope.
4. **In-Band Dispatch:** The offer payload—carrying the bundle summary, manifest distribution descriptor, ephemeral session encryption key, base nonce, stream authentication token, and direct endpoint candidates—is dispatched over the host channel via the payload sender port.

### 4.2 Zero-Leakage Ingress & Pre-Consent Staging
1. **Edge Gatekeeping:** Inbound control frames pass through the asynchronous ingress filter before handler dispatch. If file transfer ingress is globally disabled or the sender is blocked, the frame is silently discarded.
2. **Structural & Bounds Validation:** Incoming offers are validated against strict protocol ceilings (maximum `16 TB` bundle size, `50,000` files, `1,000,000` chunks, `48 KB` inline manifest, or `8 MB` remote manifest). When downloading a remote manifest blob, stream reads are strictly bounded to abort deceptive overflows.
3. **Silent Staging:** Validated offers are staged locally in a pending-consent state alongside their transport context without emitting any automatic network acknowledgment, preventing passive online/offline probing prior to explicit user consent.

### 4.3 Selective Acceptance & Multi-Stream Transfer
1. **Consent & Pre-Allocation:** Upon user acceptance of all or a subset of files, the application verifies available disk space for the selected files, immediately finalizes any selected zero-byte files, pre-allocates the remaining target files, and sends an in-band acceptance carrying the recipient's resumption bitfield.
2. **Symmetric Stream Handshake:** Out-of-band data streams authenticate using a constant-time comparison of the session's `32-byte` stream authentication token and verify that the remote peer is an authorized participant in the session topology.
3. **Pipelined Rarest-First Streaming:** Concurrent stream workers pipeline up to `32` in-flight chunk requests per peer using rarest-first piece selection. Inbound requests exceeding the `32`-request window immediately choke the offending peer.
4. **Cryptographic Verification & Poisoned-Peer Defense:** Each received chunk is decrypted with its deterministically derived AES-256-GCM nonce and verified against the manifest's Merkle tree prior to disk assembly. If AEAD authentication or Merkle leaf verification fails, the coordinator immediately chokes the sender, increments its offense counter, releases the chunk for rescheduling, and terminates the compromised stream.

### 4.4 Swarm Coordination & Seeding Completion
- **Privacy-Preserving Endpoint Discovery:** Direct WAN endpoint candidates (UPnP/STUN) are excluded by default and only advertised or accepted when explicitly permitted by the session's WAN policy. Incoming Peer Exchange (PEX) frames are rate-limited per peer and scrubbed of loopback, link-local, unspecified, and duplicate endpoints.
- **Keep-Alive & Dead-Peer Pruning:** Swarm coordinators broadcast keep-alive frames across idle streams (`30s` interval) and prune peers that remain silent beyond the dead-peer timeout (`60s`), releasing their in-flight chunks back to the scheduler.
- **Seeding Quota Governance:** Outgoing and seeding sessions track cumulative uploaded bytes against the configured target seed ratio. Once the target ratio is reached (or when a session is cancelled), the application disposes the stream pool and deterministically derives the session's swarm rendezvous identifier from the session key to unregister from the distributed tracker.

---

## 5. Architectural Hierarchy

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
