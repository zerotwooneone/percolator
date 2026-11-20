# Percolator.Application

`Percolator.Application` represents the **Application (Use Case & Orchestration) Layer** of the Onion Architecture. It serves as the central coordination engine and microkernel of the Percolator node, translating domain business rules and cryptographic primitives into end-to-end communication workflows while shielding the core domain from external infrastructure concerns.

Dependencies in this layer point strictly inward toward `Percolator.Domain` and across to the plugin contracts defined in `Percolator.PluginSdk`. It maintains zero direct dependencies on concrete infrastructure implementations, database drivers, wire serialization schemas, network sockets, or user interfaces.

For formal layer boundaries, outbound port definitions, and technical design decisions (such as outbox delivery guarantees and per-channel concurrency gating), see the project [Design Document](Design.md).

---

## 1. Architectural Invariants

### 1.1 Inward Dependency Rule
All dependencies flow inward. The application layer references only the domain and plugin abstraction contracts. Technical capabilities—such as durable database storage, wire-format serialization, network streaming, and operating system services—are consumed strictly through outward-facing ports.

### 1.2 Application Payload Opacity
The application layer acts as an application-agnostic microkernel substrate. It is responsible for authenticating and decrypting transport envelopes, but treats application payloads as opaque byte buffers tagged with a compact application identifier (`AppId`). The core pipeline never inspects, parses, or mutates the internal structure of plugin payloads.

### 1.3 Always Outbox First
To guarantee at-least-once delivery across process crashes and network partitions, all outbound payloads are persisted to transactional storage before network transmission is attempted. Active network streams are utilized as a fast-path optimization, with pending work safely queued for background delivery workers when streams are unavailable or backpressured.

### 1.4 CQRS Separation (Domain Mutation vs. Read-Only Queries)
Workflows are cleanly partitioned by their data access intent:
- **Write Operations (Commands & State Transitions):** Enforce domain invariants and state mutations through domain aggregates, persisting state changes via aggregate repositories.
- **Read Operations (Queries & UI Projections):** Read-only views project flat, lightweight read models directly from indexed persistence through specialized query ports, bypassing heavy domain aggregates.

### 1.5 Strict Concurrency Isolation
Sequential cryptographic state machines (such as ratchets and key derivation chains) are susceptible to desynchronization and corruption under concurrent access. The application layer enforces scoped concurrency synchronization across pipeline operations, guaranteeing mutually exclusive access per channel or session while allowing unrelated channels to proceed concurrently.

### 1.6 Ephemeral Replay Filtering & Resource Protection
Incoming handshake requests are evaluated against sliding-window ephemeral key caches. Duplicate or replayed invitations are rejected before initiating expensive cryptographic scalar operations or consuming one-time pre-key material.

### 1.7 Memory Hygiene & Plaintext Zeroization
All transient buffers holding decrypted plaintexts or sensitive key material are cleared and zeroized immediately after dispatching to application plugins or persistent stores.

### 1.8 Cryptographic Logging Guardrails
Logging and diagnostics strictly enforce privacy guardrails. Log sinks, traces, and exception messages must never record sensitive cryptographic keys, ratchet states, seeds, or decrypted plaintexts. Only sanitized operational metadata is permissible.

### 1.9 Temporal Determinism
System clocks are never accessed directly. All pipelines, timeouts, retries, and expiration policies obtain current time through inverted time abstractions (`IDateTimeProvider`).

---

## 2. Scope & Layer Boundaries

The application layer coordinates the execution of application use cases, governs ingress and egress data pipelines, routes payloads to application plugins, and maintains delivery persistence guarantees. For the complete specification of in-scope orchestration versus out-of-scope domain/infrastructure concerns, see [Design.md](Design.md#1-purpose-and-layer-boundary).

In brief:
- **In Scope**: Use-case orchestration, ingress/egress ratchet advancing, microkernel plugin routing, X3DH handshake coordination, contact consent & greeting staging, group sender key fanout, delivery workers & retry policies, and outbound architectural port definitions.
- **Out of Scope**: Cryptographic math algorithms, domain invariants, wire protocol schema compilation, network sockets, SQLite/SQLCipher database drivers, specific application plugin logic, and UI presentation.

---

## 3. Error Handling Philosophy

- **Typed Results for Expected Conditions:** Operations that may encounter valid domain or operational rejections (such as unknown application identifiers, unapproved contacts, replayed invitations, or missing sessions) return typed result objects with explicit error codes rather than throwing exceptions.
- **Exceptions Reserved for Defects:** Exceptions are reserved strictly for unrecoverable system faults, severe I/O failures, or invalid argument contracts in internal components.

---

## 4. Testing Philosophy

Because the application layer is decoupled from concrete I/O mechanisms:
- All unit and use-case tests execute deterministically in memory without spinning up real network sockets, web servers, or physical database files.
- Concurrency locks, replay filters, outbox repositories, and transport dispatchers are backed by clean interfaces with lightweight test doubles, ensuring fast, reproducible test execution.
