# Percolator.Apps.FileTransfer.Domain

`Percolator.Apps.FileTransfer.Domain` represents the **innermost domain core** of the File Transfer subsystem within the Percolator Onion Architecture. It encapsulates pure business rules, contiguous bundle geometry, Merkle tree cryptographic integrity, swarm piece-possession algebra, and transfer session state machines.

As an isolated domain boundary, this project depends exclusively on the shared domain kernel (`Percolator.Domain.Kernel`) and compile-time source generators, remaining completely decoupled from network sockets, wire serialization formats, filesystem I/O, and storage engines.

For formal layer boundaries, aggregate reconstitution rules, and technical design decisions, see the project [Design Document](Design.md).

---

## 1. Architectural Invariants

### 1.1 Inward Dependency Rule
Dependencies point strictly inward toward the domain core:
- **Zero Outward Dependencies:** The file transfer domain has no dependencies on application services, plugin contracts, core messaging ratchets, transport libraries, or database providers.
- **Wire & Persistence Agnosticism:** Domain models never reference binary wire schemas (such as Protobuf or JSON) or relational database structures.
- **Filesystem Independence:** The domain models file trees, byte offsets, and chunk slices mathematically without touching operating system file handles, paths, or disk APIs.

### 1.2 Strict Temporal Determinism
Domain aggregates and state transitions never query system wall clocks directly. All time-sensitive transitions accept an explicit domain clock abstraction, guaranteeing deterministic state progression and instantaneous unit testing.

### 1.3 Domain-Driven Type Safety (Zero Primitive Obsession)
Domain concepts—such as byte offsets, file sizes, chunk lengths, indices, counts, hashes, bitfields, and seeding ratios—are modeled as dedicated, self-validating value objects rather than raw primitives.

---

## 2. Scope & Layer Boundaries

The file transfer domain contains only deterministic rules, mathematical models, and protocol invariants. For the full breakdown of in-scope capabilities versus outer-layer responsibilities, see [Design.md](Design.md#1-purpose-and-layer-boundary).

In brief:
- **In Scope**: `TransferSession` aggregate, contiguous virtual bundle geometry, relative path sanitization, domain-separated SHA-256 Merkle tree verification, bitfield set algebra, selective file chunk mapping, and rarest-first piece scheduling.
- **Out of Scope**: Transport envelopes, Protobuf serialization, per-chunk AES-256-GCM encryption, multi-stream sockets, physical disk pre-allocation, sparse files, and SQLite storage.

---

## 3. Error Handling Philosophy

- **Result Pattern for State & Operational Rejections:** Domain operations that can be rejected due to state machine constraints, unauthorized peers, mismatched bitfield lengths, out-of-bounds chunk indices, or failed cryptographic chunk verification return a strongly typed result carrying an explicit error code and message.
- **Validation Exceptions for Construction Invariants:** Value objects and entity creation factories enforce structural invariants upon instantiation, throwing a domain validation exception if an invalid state is attempted.

---

## 4. Testing Philosophy

Because the file transfer domain is completely free of I/O, threading, and external dependencies:
- All domain tests execute as fast, deterministic, in-memory unit tests.
- Tests verify observable aggregate state transitions, emitted domain events, mathematical boundary conditions, and cryptographic verification invariants.
- Temporal scenarios are tested deterministically using a controlled clock implementation without wall-clock delays.

---

## 5. Core Domain Capabilities & Protocol Mechanics

1. **Contiguous Virtual Byte-Stream Bundling:** Single files and nested directory trees are mapped onto a single contiguous virtual byte stream (`[0 .. TotalSize)`). Chunks slice evenly across the virtual stream rather than padding individual files, and binary search resolves any verified chunk into exact file-local byte slices in $O(\log N)$ time.
2. **Cross-Platform Path Security:** Relative file paths are normalized to `/` and enforce strict boundaries: no leading slashes, no Windows drive letters, no `.` or `..` segments, maximum depth of 32 segments, and maximum of 255 bytes per segment.
3. **Domain-Separated Merkle Tree Verification:** Leaf chunks use `SHA-256(0x00 || chunk_bytes)` and parent nodes use `SHA-256(0x01 || left || right)` with odd-node promotion to prevent second-preimage attacks.
4. **Selective Downloads & Multi-File Completion:** Computes the exact bitfield mask of chunks required for user-selected files (including boundary chunks) and emits file completion events as each file's spanning chunk range is fully assembled.
5. **Swarm Flow Control & Rarest-First Piece Scheduling:** Tracks peer bitfields, choke status, and interest flags. The rarest-first selector prioritizes missing required chunks held by the fewest unchoked peers.
6. **Lifecycle State Machine & Seeding Quotas:** Governs transitions across Offered/PendingConsent, InFlight, Stalled, Paused, Seeding, and Completed, automatically finalizing when the target seed ratio is reached.
