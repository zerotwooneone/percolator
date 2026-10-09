# Percolator.Application

`Percolator.Application` represents the **Application (Use Case & Orchestration) Layer** of the Onion Architecture. It serves as the central coordination engine and microkernel of the Percolator node, translating domain business rules and cryptographic primitives into end-to-end communication workflows while shielding the core domain from external infrastructure concerns.

Dependencies in this layer point strictly inward toward `Percolator.Domain` and across to the plugin contracts defined in `Percolator.PluginSdk`. It maintains zero direct dependencies on concrete infrastructure implementations, database drivers, wire serialization schemas, network sockets, or user interfaces.

---

## 1. Architectural Invariants

### 1.1 Inward Dependency Rule
All dependencies flow inward. The application layer references only the domain and plugin abstraction contracts. Technical capabilities—such as durable database storage, wire-format serialization, network streaming, and operating system services—are consumed strictly through outward-facing ports.

### 1.2 Application Payload Opacity
The application layer acts as an application-agnostic microkernel substrate. It is responsible for authenticating and decrypting transport envelopes, but treats application payloads as opaque byte buffers tagged with a compact application identifier. The core pipeline never inspects, parses, or mutates the internal structure of plugin payloads.

### 1.3 Always Outbox First
To guarantee at-least-once delivery across process crashes and network partitions, all outbound payloads are persisted to transactional storage before network transmission is attempted. Active network streams are utilized as a fast-path optimization, with pending work safely queued for background delivery workers when streams are unavailable or backpressured.

### 1.4 CQRS Separation (Domain Mutation vs. Read-Only Queries)
Workflows are cleanly partitioned by their data access intent:
- **Write Operations (Commands & State Transitions):** Enforce domain invariants and state mutations through domain aggregates, persisting state changes via aggregate repositories.
- **Read Operations (Queries & UI Projections):** Read-only views (such as contact lists, outbox diagnostic monitors, and conversation listings) completely bypass heavy domain aggregates. They project flat, lightweight read models directly from indexed persistence through specialized query ports.

### 1.5 Strict Concurrency Isolation
Sequential cryptographic state machines (such as ratchets and key derivation chains) are susceptible to desynchronization and corruption under concurrent access. The application layer enforces scoped concurrency synchronization across pipeline operations, guaranteeing mutually exclusive access per channel or session while allowing unrelated channels to proceed concurrently.

### 1.6 Ephemeral Replay Filtering & Resource Protection
To protect nodes against resource exhaustion and denial-of-service attacks, incoming handshake requests are evaluated against sliding-window ephemeral key caches. Duplicate or replayed invitations are rejected before initiating expensive cryptographic scalar operations or consuming one-time pre-key material.

### 1.7 Memory Hygiene & Plaintext Zeroization
All transient buffers holding decrypted plaintexts or sensitive key material are cleared and zeroized immediately after dispatching to application plugins or persistent stores, minimizing sensitive data exposure in process memory.

### 1.8 Cryptographic Logging Guardrails
Logging and diagnostics strictly enforce privacy guardrails. Log sinks, traces, and exception messages must never record sensitive cryptographic keys, ratchet states, seeds, or decrypted plaintexts. Only sanitized operational metadata (such as channel identifiers, identity identifiers, device tags, sequence counters, and payload byte counts) is permissible.

### 1.9 Temporal Determinism
System clocks are never accessed directly. All pipelines, timeouts, retries, and expiration policies obtain current time through inverted time abstractions, enabling fully deterministic testing without artificial delays.

---

## 2. What Belongs in the Application Layer (In Scope)

The application layer coordinates the execution of application use cases, governs ingress and egress data pipelines, routes payloads to application plugins, and maintains delivery persistence guarantees:

| Concept | High-Level Scope & Purpose |
| :--- | :--- |
| **Use Case Orchestration** | Directs application workflows by coordinating domain aggregates, entities, and outbound ports without implementing technical infrastructure. |
| **Ingress Pipeline & Verification** | Accepts incoming typed envelopes from transport listeners, enforces payload size constraints, executes edge filtering, coordinates cryptographic ratchet steps via domain models, and authenticates headers. |
| **Egress Framing & Outbox Enqueue** | Encapsulates plugin payloads with application identifiers, coordinates cryptographic ratchet advances, delegates wire framing to serialization ports, and atomically persists outbound jobs. |
| **Plugin Substrate & Dispatch** | Maintains the registry of application plugins and dispatches decrypted payloads to target handlers in constant time using compact application identifiers. |
| **Key Agreement & Handshake Coordination** | Coordinates mutual cryptographic authentication workflows, pre-key bundle retrieval, initial ratchet session bootstrap, and authenticated greeting payload delivery. |
| **Contact Approval & Greeting Preservation** | Manages contact invitation lifecycles, safely persisting initial greeting payloads during unapproved states so they can be decrypted and dispatched upon user consent. |
| **Group Sender Key Distribution** | Coordinates pairwise dissemination of group sender key material across members over one-to-one channels, and processes inbound distributions to initialize group reception state. |
| **Concurrency Synchronization** | Provides scoped synchronization mechanisms to protect sequential channel ratchets from concurrent pipeline race conditions. |
| **Delivery Worker & Retry Coordination** | Manages background outbox delivery, evaluating transport outcomes, transient retry backoffs with jitter, and permanent failure transitions. |
| **Route Resolution & Reachability** | Evaluates open transport streams, direct peer reachability, and home relay mailbox fallbacks to determine optimal message delivery paths. |
| **Deep-Link Invitation Abstractions** | Encapsulates invitation link formats supporting optional port configurations, enabling flexible connection probing across dynamic port ranges. |
| **CQRS Read Query Port Definitions** | Defines query ports returning flat summary read models for UI presentation and diagnostic tooling without hydrating rich domain aggregates. |
| **Architectural Port Specifications** | Outward-facing abstractions defining technical dependencies (persistence, wire packaging, transport dispatchers, edge filters, stream registries, and credential stores). |

---

## 3. What Does NOT Belong in the Application Layer (Out of Scope)

The following concerns are explicitly excluded from the application layer and belong in other architectural layers:

| Excluded Concern | Responsible Layer | Architectural Reason |
| :--- | :--- | :--- |
| **Cryptographic Math & State Machines** | Domain | Double Ratchet key derivations, Diffie-Hellman scalar multiplications, KDF chains, and epoch advancement logic belong exclusively to the domain layer. |
| **Channel Governance & Invariants** | Domain | Rules governing channel membership rosters, administrative roles, and epoch security invariants are pure domain aggregate logic. |
| **Wire Protocol Serialization** | Infrastructure | Generating Protobuf contracts, compiling schema definitions, and packing binary wire bytes belong strictly in technical infrastructure adapters. |
| **Network Sockets & Transport Protocols** | Infrastructure | Low-level gRPC channels, HTTP/2 multiplexing, TCP/UDP sockets, TLS certificate management, and stream reconnection loops are technical transport concerns. |
| **Database Engines & Storage Drivers** | Infrastructure | SQL queries, table schemas, migrations, database engines (SQLite, SQLCipher), and object-relational mappers belong in infrastructure persistence adapters. |
| **Specific Application Plugin Logic** | Application Plugins | Chat message threading, reaction parsing, file manifest assembly, and peer discovery algorithms belong in their respective plugin assemblies, not the central host. |
| **Out-of-Band Bulk Data Streaming** | Application Plugins / Infrastructure | High-volume file chunk transfers and bulk data streaming bypass the Double Ratchet pipeline and are orchestrated out-of-band by dedicated transfer adapters. |
| **Platform-Specific Credential Vaults** | Infrastructure | Operating system keyrings (Windows DPAPI, macOS Keychain, Linux Keyutils) and filesystem access control lists belong in platform security adapters. |
| **Presentation, UI State & Data Bindings** | Presentation / UI | ViewModels, UI navigation, localization, visual controls, and user input validation belong exclusively to presentation projects. |

---

## 4. Error Handling Philosophy

- **Typed Results for Expected Conditions:** Operations that may encounter valid domain or operational rejections (such as unknown application identifiers, unapproved contacts, replayed invitations, or missing sessions) return typed result objects with explicit error codes rather than throwing exceptions.
- **Exceptions Reserved for Defects:** Exceptions are reserved strictly for unrecoverable system faults, severe I/O failures, or invalid argument contracts in internal components.

---

## 5. Testing Philosophy

Because the application layer is decoupled from concrete I/O mechanisms:
- All unit and use-case tests execute deterministically in memory without spinning up real network sockets, web servers, or physical database files.
- Concurrency locks, replay filters, outbox repositories, and transport dispatchers are backed by clean interfaces with lightweight test doubles, ensuring fast, reproducible test execution.
