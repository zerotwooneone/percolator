# Percolator.Apps.FileTransfer.Domain

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Apps.FileTransfer.Domain` is the isolated domain core of the File Transfer bounded context. Segregated from `Percolator.Domain` so file-swarm mathematics never couple to messaging ratchets, it models multi-file bundle geometry, Merkle tree integrity, bitfield algebra, and swarm transfer state machines.

- **In Scope:** `TransferSession` lifecycle state machine, contiguous virtual byte-stream geometry (`TransferManifest`), cross-platform relative path validation, domain-separated Merkle tree verification, bitfield set algebra, selective file chunk masks, and rarest-first piece selection.
- **Out of Scope:** In-band wire framing, per-chunk AEAD encryption, multi-stream socket pools, Peer Exchange (PEX) networking, physical disk handles, sparse file pre-allocation, and database persistence.

---

## 2. Architectural Invariants and Encapsulation

Inherits the tactical DDD, `DomainResult<T>`, and temporal determinism rules from [`Percolator.Domain.Kernel`](../Percolator.Domain.Kernel/Design.md) and the `internal` aggregate rehydration encapsulation pattern from [`Percolator.Domain`](../Percolator.Domain/Design.md), plus:

1. **Dimensional Value-Object Separation:** Distinct value objects separate bundle-wide byte offsets (`BundleByteOffset`) from file-local byte offsets (`FileByteOffset`), and chunk indices (`ChunkIndex`) from file indices (`FileIndex`), making unit-confusion bugs unrepresentable at compile time.
2. **Filesystem-Agnostic Path Sanitization (`RelativeFilePath`):** Relative paths inside a bundle are normalized to forward slashes (`/`) and validated purely as UTF-8 value objects, rejecting rooted paths, drive letters, `.` or `..` traversal segments, and empty segments before an aggregate can be created.

---

## 3. Key Technical Design Choices

1. **Padding-Free Contiguous Virtual Byte Stream over Per-File Chunking:**
   - *Design Choice:* Ordered files in a bundle are mapped onto a single contiguous virtual byte range `[0 .. TotalSize)` and sliced into fixed-size chunks that may span across file boundaries, using binary search over file offsets to resolve any verified chunk into exact file-local slices.
   - *Rejected Alternative:* Per-file chunk alignment with zero-padding, which wastes bandwidth and inflates Merkle tree leaf counts when transferring directories containing many small files.
2. **Second-Preimage Resistant Merkle Trees (`0x00` Leaf / `0x01` Internal Prefix):**
   - Merkle leaf hashes prepend `0x00` and internal parent nodes prepend `0x01` prior to SHA-256 hashing, preventing an attacker from substituting a 64-byte concatenation of two internal node hashes for a leaf chunk.
3. **Reactive Interest & Automatic Stall Recovery:**
   - Whenever local chunk possession or a peer's remote bitfield changes, the session recomputes whether each peer holds any still-missing required chunk and automatically transitions a `Stalled` session back to `InFlight` as soon as a needed piece becomes available.
