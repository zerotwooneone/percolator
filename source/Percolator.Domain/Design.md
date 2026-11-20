# Percolator.Domain

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Domain` is the core enterprise and cryptographic domain deployable. It encapsulates pure, deterministic state machines for end-to-end encrypted communication (X3DH mutual key agreement, Double Ratchet, Sender Keys), sovereign identity lifecycles, private channel governance, and blind relay pre-key/mailbox ledgers.

- **In Scope:** Aggregate consistency boundaries, protocol state transitions, channel role/epoch invariants, relay quota and blind-token rules, and outbound domain ports for cryptographic math (`ISignalCryptoEngine`, `IZkGroupEngine`) and aggregate repositories.
- **Out of Scope:** Binary wire serialization (Protobuf), Associated Data (AD) byte framing, transactional outbox queues, payload ordering buffers, application plugin payloads, database schemas, and network sockets.

---

## 2. Architectural Invariants and Encapsulation

Inherits [`INV-DOMAIN-VALUE-TYPES`](../Percolator.Domain.Kernel/Design.md#2-architectural-invariants-and-encapsulation), [`INV-TIME-INVERSION`](../Percolator.Domain.Kernel/Design.md#2-architectural-invariants-and-encapsulation), [`INV-SECRET-ZEROIZATION`](../Percolator.Domain.Kernel/Design.md#2-architectural-invariants-and-encapsulation), and [`DEC-STRUCT-DOMAIN-RESULT`](../Percolator.Domain.Kernel/Design.md#3-key-technical-design-choices) from `Percolator.Domain.Kernel`, plus:

1. **`INV-AGGREGATE-ENCAPSULATION` (Strict Aggregate Mutation & Collection Encapsulation):** All aggregate state changes occur through intention-revealing domain command methods that validate invariants before mutating state. Internal collections are exposed exclusively as read-only views (`IReadOnlyCollection<T>`, `IReadOnlyDictionary<TKey, TValue>`). `#group-membership`
2. **`INV-INTERNAL-REHYDRATION` (Internal-Only Persistence Reconstitution):** Domain aggregates expose **only** valid business creation factories on their `public` surface. Reconstitution from storage (`Rehydrate` factories and persistence-only state getters) is marked `internal` and exposed exclusively to persistence adapters via `[InternalsVisibleTo]`.
3. **`INV-PURE-DOMAIN-PORTS` (Pure Domain Ports):** Outbound repository and crypto engine ports accept and return strictly domain aggregates and value objects—never database entities or wire DTOs.

---

## 3. Key Technical Design Choices

1. **`DEC-INTERNAL-REHYDRATE` (`internal` Rehydration over Public State Setters or Memento DTOs):**
   - *Design Choice:* Keeping `Rehydrate` factories and raw state getters `internal` prevents outer layers (`Percolator.Application`, `Percolator.Desktop`) from bypassing domain invariants or constructing arbitrary aggregate states, without paying the allocation and maintenance cost of duplicating every aggregate into a public Memento class.
   - *Rejected Alternative:* `public static Rehydrate(...)` methods on aggregates, which leak persistence plumbing into the public ubiquitous language.
2. **`DEC-INVERTED-CRYPTO-PORTS` (Inverted Cryptographic & Zero-Knowledge Primitives):**
   - Protocol state rules (when a DH ratchet steps, how skipped message keys are bounded, when a group epoch advances, and when trust downgrades on key change) live in pure C# inside `Percolator.Domain`, while elliptic-curve math and zero-knowledge proof verification are inverted behind `ISignalCryptoEngine` and `IZkGroupEngine` for fast, deterministic in-memory unit testing. `#group-membership`
3. **`DEC-PARTITIONED-RELAY-LEDGER` (Fine-Grained Per-Identity Relay Ledgers):**
   - Relay pre-key storage is partitioned per `(PublicIdentityId, DeviceId)` aggregate rather than a monolithic relay-wide aggregate, bounding memory hydration size and eliminating cross-peer lock contention when dispensing one-time pre-keys. `#offline-delivery`
