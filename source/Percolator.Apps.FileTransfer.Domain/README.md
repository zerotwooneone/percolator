# Percolator.Apps.FileTransfer.Domain

`Percolator.Apps.FileTransfer.Domain` represents the **innermost domain core** of the File Transfer subsystem within the Percolator Onion Architecture. It encapsulates pure business rules, contiguous bundle geometry, Merkle tree cryptographic integrity, swarm piece-possession algebra, and transfer session state machines.

As an isolated domain boundary, this project depends exclusively on the shared domain kernel (`Percolator.Domain.Kernel`) and compile-time source generators, remaining completely decoupled from network sockets, wire serialization formats, filesystem I/O, and storage engines.

---

## 1. Architectural Invariants

### 1.1 Inward Dependency Rule
Dependencies point strictly inward toward the domain core:
- **Zero Outward Dependencies:** The file transfer domain has no dependencies on application services, plugin contracts, core messaging ratchets, transport libraries, or database providers.
- **Wire & Persistence Agnosticism:** Domain models never reference binary wire schemas (such as Protobuf or JSON) or relational database structures. Translating between domain models and wire or storage representations is the exclusive responsibility of outer layers.
- **Filesystem Independence:** The domain models file trees, byte offsets, and chunk slices mathematically without touching operating system file handles, paths, or disk APIs.

### 1.2 Strict Temporal Determinism
Domain aggregates and state transitions never query system wall clocks directly. All time-sensitive transitions accept an explicit domain clock abstraction, guaranteeing deterministic state progression, reproducible event timestamps, and instantaneous unit testing.

### 1.3 Domain-Driven Type Safety (Zero Primitive Obsession)
Domain concepts—such as byte offsets, file sizes, chunk lengths, indices, counts, hashes, bitfields, and seeding ratios—are modeled as dedicated, self-validating value objects rather than raw primitives:
- **No Implicit or Explicit Conversion Operators:** Value objects prohibit implicit and explicit casts to or from raw primitives, preventing accidental unit confusion (for example, conflating a bundle-wide byte offset with a file-local offset or a chunk index with a file index).
- **Domain-Specific Arithmetic:** Byte and offset calculations are expressed through explicit domain operations that preserve invariants at every step.

---

## 2. What Belongs in the Domain (In Scope)

The domain contains only deterministic rules, mathematical models, and protocol invariants that hold true regardless of how bytes are transmitted or stored:

| Concept | High-Level Scope & Guidelines |
| :--- | :--- |
| **Transfer Session Aggregate** | The consistency boundary governing the lifecycle of a file transfer—from initial offer creation or inbound staging, through selective file acceptance, in-flight chunk verification, stall detection and recovery, pause/resume, seeding, and terminal completion, cancellation, or failure. |
| **Contiguous Bundle & Chunk Geometry** | The mathematical mapping of an ordered collection of files into a single contiguous virtual byte stream, subdivided into fixed-size chunks (with a potentially smaller final chunk) and mapped back into exact per-file byte slices via binary search. |
| **Relative Path Sanitization** | Cross-platform normalization and security validation of relative file paths within a bundle, rejecting directory traversal segments, empty segments, rooted or drive-qualified paths, excessive directory depth, and oversized UTF-8 path or segment lengths. |
| **Merkle Tree Integrity** | Domain-separated SHA-256 cryptographic hashing of leaf chunks (`0x00` prefix) and internal parent nodes (`0x01` prefix), odd-node promotion, root calculation, and constant-time leaf verification against the manifest's Merkle tree. |
| **Bitfield & Piece Possession Algebra** | Immutable big-endian bit vectors tracking chunk possession across a bundle, enforcing strict zeroing of unused trailing padding bits, and supporting set operations (intersection, difference, subset verification, and population counts). |
| **Selective Download Mapping** | Calculation of the exact subset of chunk indices required to satisfy a user's selection of files within a multi-file bundle, including boundary chunks that span across selected and unselected adjacent files. |
| **Swarm Topology & Member State** | Authorization of participating peer identities (one-to-one, multi-peer, or staged incoming offers) and per-peer swarm state tracking (remote bitfields, piece announcements, choke status, and interest status). |
| **Automatic Interest & Stall Recovery** | Reactive re-evaluation of whether connected swarm peers possess missing required chunks whenever local possession or remote bitfields change, automatically transitioning a stalled transfer back to in-flight when a needed piece becomes available. |
| **Rarest-First Piece Selection** | Deterministic selection of the next missing, non-in-flight chunk that has the lowest availability among currently unchoked swarm peers. |
| **Seeding Ratio Governance** | Tracking cumulative uploaded bytes against total bundle size and automatically transitioning a seeding session to completed once the configured target seed ratio is achieved. |
| **Domain Events & Results** | Immutable domain events capturing lifecycle milestones (offered, accepted, peer joined, chunk verified, individual file completed, stalled, paused, resumed, completed, cancelled, failed) and typed result objects for expected rule rejections. |

---

## 3. What Does NOT Belong in the Domain (Out of Scope)

The following concerns are explicitly excluded from `Percolator.Apps.FileTransfer.Domain` and belong in outer layers:

| Excluded Concern | Responsible Layer | Architectural Reason |
| :--- | :--- | :--- |
| **In-Band Wire Payloads & Framing** | Application / Infrastructure | Control opcodes, offer/accept/cancel transport envelopes, and binary stream frame headers belong to application orchestration and infrastructure adapters. |
| **Protobuf & Binary Serialization** | Infrastructure | Compiling `.proto` schemas and serializing manifests or control payloads to wire bytes are infrastructure adapter responsibilities behind an application serializer port. |
| **Ingress Filtering & Blocklists** | Application | Edge kill-switches, sender blocklists, relay URI filtering, and pre-consent offer expiration sweeps are application policy orchestration concerns. |
| **Per-Chunk Symmetric Encryption** | Application | Ephemeral session keys, deterministic AES-GCM chunk nonce derivation, and authenticated encryption/decryption of wire chunks belong in the application streaming engine. |
| **Multi-Stream Sockets & Relay Pipes** | Application / Infrastructure | Multiplexed TCP/QUIC socket pools, blind relay pipe splicing, UPnP/STUN endpoint discovery, and stream handshakes belong in application coordinators and transport adapters. |
| **Physical Disk I/O & Pre-Allocation** | Application / Infrastructure | Sparse file pre-allocation, `.part` file staging, physical disk space preflight checks, random-access file reads/writes, and atomic file renames belong in storage adapters. |
| **Distributed Swarm Trackers & PEX** | Application / Infrastructure | Announcing socket candidates to relay trackers, Peer Exchange (PEX) rate limiting, and dead-peer keep-alive timeouts are network coordination concerns. |
| **Bandwidth Rate Limiting** | Application / Infrastructure | Token-bucket upload and download throughput throttling is an infrastructure/application runtime concern. |
| **Database Persistence & Queries** | Infrastructure | SQLite tables, aggregate hydration/persistence queries, and dashboard read-model projections belong in persistence adapters. |

---

## 4. Error Handling Philosophy

- **Result Pattern for State & Operational Rejections:** Domain operations that can be rejected due to state machine constraints, unauthorized peers, mismatched bitfield lengths, out-of-bounds chunk indices, or failed cryptographic chunk verification return a strongly typed result carrying an explicit error code and message.
- **Validation Exceptions for Construction Invariants:** Value objects and entity creation factories enforce structural invariants (such as positive sizes, contiguous file offsets, valid path syntax, and matching Merkle leaf counts) upon instantiation, throwing a domain validation exception if an invalid state is attempted.

---

## 5. Testing Philosophy

Because the file transfer domain is completely free of I/O, threading, and external dependencies:
- All domain tests execute as fast, deterministic, in-memory unit tests.
- Tests verify observable aggregate state transitions, emitted domain events, mathematical boundary conditions, and cryptographic verification invariants.
- Temporal scenarios are tested deterministically using a controlled clock implementation without wall-clock delays.

---

## 6. Core Domain Capabilities & Protocol Mechanics

1. **Contiguous Virtual Byte-Stream Bundling:**
   - Single files and nested directory trees are modeled as a flat, sequential list of file entries mapped onto a single contiguous virtual byte stream (`[0 .. TotalSize)`).
   - Fixed-size chunks slice evenly across the virtual byte stream rather than padding individual files. A single chunk can span multiple small files, and a large file can span thousands of chunks.
   - Binary search over contiguous file offsets resolves any verified chunk into one or more exact file-local byte slices in $O(\log N)$ time, skipping zero-byte files cleanly.

2. **Cross-Platform Path Security:**
   - Every file in a bundle is identified by a sanitized relative path normalized to forward slashes (`/`).
   - Path creation enforces strict security boundaries: no leading slashes, no Windows drive letters, no `.` or `..` traversal segments, no empty segments, a maximum depth of 32 directory segments, a maximum of 255 UTF-8 bytes per segment, and a maximum of 1,024 UTF-8 bytes overall.

3. **Domain-Separated Merkle Tree Verification:**
   - Every chunk in the virtual byte stream is hashed as a Merkle leaf using `SHA-256(0x00 || chunk_bytes)`.
   - Parent nodes are constructed pairwise from child hashes using `SHA-256(0x01 || left_hash || right_hash)`, with odd trailing nodes promoted to the next level until a single 32-byte Merkle root is produced.
   - Domain separation between leaf (`0x00`) and internal (`0x01`) nodes prevents second-preimage attacks, and every received chunk is verified against its expected byte length and leaf hash before being recorded.

4. **Selective Downloads & Multi-File Completion Detection:**
   - Recipients can accept either the entire bundle or a specific subset of file indices.
   - The manifest computes the exact bitfield mask of chunks required to reconstruct the selected files, including boundary chunks shared with unselected neighbors.
   - Whenever a chunk is verified, the session inspects all files touched by that chunk's slices and emits a file-completion event for each file whose spanning chunk range is now fully present in the local bitfield.

5. **Swarm Flow Control & Rarest-First Piece Scheduling:**
   - A transfer session maintains a roster of authorized swarm peers, tracking each peer's chunk possession bitfield alongside bidirectional choke and interest flags.
   - Local interest in a peer is automatically computed and updated whenever local or remote chunk possession changes: the local node is interested in a peer if and only if that peer possesses at least one chunk still missing from the local required set.
   - When selecting the next chunk to request, the rarest-first selector filters out chunks already possessed or currently in flight and chooses the missing required chunk held by the fewest unchoked peers, maximizing piece diversity across the swarm.

6. **Lifecycle State Machine & Seeding Quotas:**
   - Outgoing sessions begin in an offered state with a full local bitfield and transition to in-flight once an authorized recipient accepts and reports its resumption bitfield.
   - Incoming sessions begin in a pending-consent state with an empty bitfield and transition to in-flight upon local user acceptance.
   - Once all required chunks for the selected files are verified, a recipient session transitions either directly to completed (if the target seed ratio is zero) or to seeding (if a positive or unlimited seed ratio is configured), automatically completing once cumulative uploaded bytes reach the target ratio.
