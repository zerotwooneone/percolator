# Percolator.Apps.FileTransfer

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Apps.FileTransfer` is the use-case orchestration and streaming coordination layer of the File Transfer plugin (`AppId: 0x03`). It connects the pure bundle geometry and state machines of [`Percolator.Apps.FileTransfer.Domain`](../Percolator.Apps.FileTransfer.Domain/Design.md) with [`Percolator.PluginSdk`](../Percolator.PluginSdk/Design.md) to coordinate in-band control negotiation alongside out-of-band multi-stream bulk chunk transfer.

- **In Scope:** In-band offer/accept/cancel negotiation, edge ingress policy filtering, adaptive chunk sizing and inline-vs-blob manifest distribution, pipelined rarest-first multi-stream chunk scheduling, per-chunk AEAD encryption, verify-before-write disk assembly, and swarm liveness/Peer Exchange (PEX) governance.
- **Out of Scope:** Pure bundle math and Merkle tree algebra (`Percolator.Apps.FileTransfer.Domain`), host messaging ratchets, Protobuf `.proto` compilation, physical OS file handles, sparse file ioctls, TCP/QUIC sockets, and SQLite storage (`Percolator.Infrastructure`).

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-VERIFY-BEFORE-WRITE` (Verify-Before-Write Disk Hygiene):** Every inbound chunk must pass both per-chunk AEAD authentication and domain `MerkleTree` leaf verification in memory (per [`DEC-DOMAIN-SEPARATED-MERKLE`](../Percolator.Apps.FileTransfer.Domain/Design.md#3-key-technical-design-choices)) *before* any byte slice is written via `IDesktopFileStorage`. Unauthenticated or corrupted chunks immediately choke the offending peer and drop the stream with zero disk writes.
2. **`INV-POOLED-STREAM-BUFFERS` (Zero-Allocation Pooled Streaming Path):** Chunk reads, encryption, decryption, and Merkle hashing rent and return buffers from `ArrayPool<byte>.Shared` to prevent Large Object Heap (LOH) fragmentation during multi-stream saturation.
3. **`INV-SILENT-OFFER-STAGING` (Silent Pre-Consent Staging — Anti-Presence-Probing):** Inbound transfer offers are filtered at the edge and staged locally in `PendingConsent` without emitting any automatic network receipt or probing direct peer endpoints until the user explicitly accepts.

---

## 3. Key Technical Design Choices

1. **`DEC-DUAL-PLANE-TRANSFER` (Dual-Plane Bifurcation — In-Band Control vs. Out-of-Band Bulk Data):**
   - *Design Choice:* Lightweight control frames (offer, selective accept, cancel) travel over the host's ratcheted messaging channel, whereas bulk file chunks stream over dedicated out-of-band connections authenticated via a constant-time session token and encrypted with an ephemeral session key and deterministic per-chunk nonce (`BaseNonce XOR ChunkIndex`). `#bitfield-swarm`
   - *Rejected Alternative:* Streaming bulk file chunks through the Double Ratchet pipeline, which causes severe head-of-line blocking for interactive chat and millions of redundant ratchet KDF steps.
2. **`DEC-TWO-TIER-MANIFEST` (Two-Tier Manifest Distribution — Inline vs. Out-of-Band Blob):**
   - Small manifests travel inline inside the offer control frame for single-roundtrip startup, while large multi-file manifests are uploaded once via `IMediaBlobService` (per [`DEC-OOB-BLOB-REFERENCE`](../Percolator.PluginSdk/Design.md#3-key-technical-design-choices)) with a bounded stream reader on download to prevent memory-exhaustion bombs.
3. **`DEC-PRIVACY-GATED-PEX` (Privacy-Gated Peer Exchange):**
   - Direct WAN endpoint candidates are excluded by default unless explicitly permitted by session policy, and incoming PEX announcements are rate-limited and scrubbed of loopback, link-local, and unspecified addresses. `#relay-privacy`
