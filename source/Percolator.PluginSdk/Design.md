# Percolator.PluginSdk

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.PluginSdk` is the pure contract boundary of the Percolator microkernel. Segregated from `Percolator.Application` so modular application plugins (`Percolator.Apps.*`) can be developed and tested independently of host orchestration internals, it defines the interfaces and descriptors connecting plugins to the encrypted communication fabric and presentation timeline.

- **In Scope:** Plugin registration and payload demultiplexing contracts (`IPercolatorAppPlugin`, `AppId`), wire serialization abstraction (`IPayloadSerializer`), out-of-band encrypted blob contracts (`IMediaBlobService`, `BlobReference`), and UI-agnostic timeline notification sinks (`ITimelinePostSink`, `IPluginActionHandler`).
- **Out of Scope:** Host ratchet orchestration, outbox queues, concrete plugin feature protocols, wire schema compilers (Protobuf), database storage, network sockets, and UI framework types.

---

## 2. Architectural Invariants and Encapsulation

Inherits the value-object and result conventions from [`Percolator.Domain`](../Percolator.Domain/Design.md), plus:

1. **Strict Tenant Isolation by `AppId`:** Every plugin registers under a unique application identifier; payloads are routed exclusively to their owning plugin and never cross-contaminate across application streams.
2. **Cryptographic Agnosticism for Plugins:** Plugins exchange structured domain payloads and delivery routes through SDK ports while remaining completely decoupled from underlying X3DH, Double Ratchet, Sender Key, and blind-token state machines.
3. **Log Redaction on Capability Descriptors:** Out-of-band descriptors carrying ephemeral symmetric keys or nonces (`BlobReference`) override `ToString()` to redact secret material from diagnostic logs.

---

## 3. Key Technical Design Choices

1. **Bounded In-Band Envelope vs. Out-of-Band Encrypted Blob Reference:**
   - *Design Choice:* To prevent head-of-line blocking in message ratchets and bound transactional outbox row sizes, in-band channel envelopes enforce a strict size ceiling. Attachments or manifests exceeding the inline threshold are encrypted into content-addressed chunks via `IMediaBlobService`, while the in-band envelope carries only a compact `BlobReference` (digest, ephemeral key, nonce, unpadded size).
   - *Rejected Alternative:* Streaming large attachments directly inside ratcheted message envelopes, which stalls real-time messaging and bloats SQLite outbox tables.
2. **Topology-Explicit Delivery Contracts (`DeliveryRoute`):**
   - SDK contracts explicitly surface whether a session is direct peer-to-peer (requiring simultaneous online presence for media streaming) or relay-mediated (supporting asynchronous store-and-forward blob caching).
