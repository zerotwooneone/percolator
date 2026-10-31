# Percolator.Application

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Application` is the core use-case orchestration and microkernel host layer. It coordinates [`Percolator.Domain`](../Percolator.Domain/Design.md) aggregates, implements the host side of [`Percolator.PluginSdk`](../Percolator.PluginSdk/Design.md), and defines the outbound ports for persistence, wire packaging, and network transport.

- **In Scope:** Ingress/egress ratchet pipelines, constant-time `AppId` plugin dispatch, X3DH handshake orchestration, contact consent & greeting staging, group sender key fanout & out-of-order buffering, transactional outbox coordination, and CQRS write/read port definitions.
- **Out of Scope:** Cryptographic math implementations, plugin-specific feature workflows (`Percolator.Apps.*`), Protobuf `.proto` compilation, SQLite/SQLCipher drivers, gRPC sockets, and UI state.

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-PAYLOAD-OPACITY` (Application Payload Opacity):** The host pipeline authenticates and decrypts transport envelopes but treats inner plugin payloads as opaque byte buffers tagged by `AppId` (per [`INV-APPID-ISOLATION`](../Percolator.PluginSdk/Design.md#2-architectural-invariants-and-encapsulation)), never inspecting or mutating plugin-specific schemas.
2. **`INV-CQRS-SEPARATION` (CQRS Write vs. Read Separation):** Write commands mutate state exclusively through domain aggregates and repository ports; read queries (`IContactQueryService`, `IOutboxQueryService`, etc.) project flat, immutable read-model records directly from persistence without hydrating domain aggregates. `#cqrs-projection`
3. **`INV-CHANNEL-SERIALIZATION-GATE` (Per-Channel Concurrency Serialization):** Because cryptographic ratchets are sequential state machines, all ingress and egress pipeline steps acquire a key-scoped asynchronous lock (`IChannelConcurrencyGate`) per channel/session while allowing unrelated channels to proceed in parallel.
4. **`INV-PLAINTEXT-ZEROIZATION` (Immediate Plaintext Zeroization & Sanitized Logging):** Transient plaintext and key buffers are zeroized immediately after plugin dispatch or persistence, and logs record only sanitized metadata (identifiers, sequence numbers, byte counts)—never keys, tokens, or plaintext. `#session-lock`

---

## 3. Key Technical Design Choices

1. **`DEC-OUTBOX-FIRST` (Always Outbox First — At-Least-Once Transactional Delivery):**
   - *Design Choice:* Every outbound message, handshake frame, or sender key distribution is atomically persisted to `IOutboxRepository` *before* network transmission is attempted. Active transport streams serve as an opportunistic fast path, with background outbox workers handling exponential-backoff retries. `#offline-delivery`
   - *Rejected Alternative:* Direct socket writes immediately after advancing a ratchet, which permanently loses messages on transient socket failure because a ratchet step cannot be rolled backward.
2. **`DEC-PRECRYPTO-REPLAY-FILTER` (Pre-Crypto Ephemeral Replay Filtering):**
   - Inbound handshake requests are checked against a sliding-window ephemeral key cache (`IReplayFilter`) *before* executing X3DH scalar multiplications or consuming one-time pre-keys, defeating CPU and pre-key exhaustion DoS attacks at the ingress edge.
3. **`DEC-GREETING-CONSENT-STAGING` (Unapproved Contact Greeting Preservation):**
   - Inbound handshakes from unknown peers stage the initial encrypted greeting payload in a pending-consent state rather than discarding it or auto-establishing a full channel, decrypting and dispatching the greeting only after explicit local user approval.
