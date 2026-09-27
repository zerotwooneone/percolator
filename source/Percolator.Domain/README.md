# Percolator.Domain

`Percolator.Domain` represents the **innermost core** of the Onion Architecture. It encapsulates the enterprise business rules, cryptographic invariants, and conversational domain models for the Percolator ecosystem. 

As the architectural anchor, this project contains the pure domain logic decoupled from frameworks, transport mechanisms, and storage engines.

---

## 1. Architectural Invariants

### 1.1 Inward Dependency Rule
Dependencies in Onion Architecture point strictly inward:
- **Zero Inbound Dependencies:** `Percolator.Domain` has no dependencies on other solution projects (except source generators) and zero references to external frameworks.
- **Wire Contract Decoupling:** The domain never references `Percolator.Contracts`, Protobuf definitions, or network serialization schemas. Wire models and domain models are distinct concerns separated by application mapping layers.
- **Framework Independence:** The domain is completely agnostic of databases, UI frameworks, cloud providers, and networking libraries.

### 1.2 Strict Temporal Determinism
Aggregates and entities must never access system clocks directly (`DateTime.UtcNow` or `DateTimeOffset.UtcNow`). All temporal logic is passed explicitly or inverted via domain clock ports. This guarantees deterministic state transitions, replayable event streams, and repeatable unit testing.

### 1.3 Memory & Secret Hygiene
In end-to-end encrypted messaging, cryptographic state management is a domain invariant. Any domain structure managing sensitive keys, ratchets, or secrets is responsible for its own memory lifecycle and zeroization upon disposal or state progression.

---

## 2. What Belongs in the Domain

| Concept | Description & Guidelines |
| :--- | :--- |
| **Aggregates & Entities** | Conceptual consistency boundaries that encapsulate state and enforce business invariants. State changes occur exclusively through public domain methods that preserve aggregate validity. |
| **Value Objects** | Immutable types identified solely by their attributes and validation rules rather than an entity identifier. Value objects are zero-allocation where possible, enforce structural validity upon creation, and guarantee value equality. |
| **Domain Events** | Immutable records capturing significant business and cryptographic occurrences within an aggregate boundary. Used to communicate state changes to outer layers without coupling. |
| **Domain Invariants & Rules** | Core policies governing participant roles, cryptographic state transitions, group epoch advancement, member authorisations, and identity states. |
| **Domain Ports (Interfaces)** | Outward-facing abstractions defining required capabilities (such as repositories, cryptographic engines, or zero-knowledge verifiers) without exposing implementation details or technology choices. |
| **Domain Results & Errors** | Strongly typed, allocation-conscious result representations for expected domain rejections. Expected rule violations return domain failures rather than throwing exceptions. |

---

## 3. What Does NOT Belong in the Domain

| Anti-Pattern / Technology | Where It Belongs | Why It Is Excluded |
| :--- | :--- | :--- |
| **Database & ORM Plumbing** | Infrastructure / Persistence | EF Core `DbContext`, SQL queries, migration scripts, column mapping attributes, and change tracking have no place in pure domain models. |
| **Wire & Serialization Schemas** | Contracts / Infrastructure | Protobuf files, gRPC contracts, JSON serialization attributes, and DTOs couple domain logic to external communication protocols. |
| **Transport Delivery Mechanisms** | Application / Infrastructure | Transactional outbox dispatching, polling workers, exponential backoff retries, and network socket management solve distributed systems problems, not business rules. |
| **Network Idempotency (Inbox Pattern)** | Application / Infrastructure | Handling duplicate network packets, HTTP/gRPC stream deduplication, and transport acknowledgments are transport concerns. The domain enforces idempotency naturally via aggregate state and cryptographic counters. |
| **Direct Hardware & OS Access** | Infrastructure | Direct file system access, network socket communication, and platform-specific cryptographic APIs belong behind domain port adapters. |
| **Presentation & UI Concerns** | UI / Presentation Layer | ViewModels, command bindings, formatting strings for human presentation, and UI state machines must never leak into the domain. |

---

## 4. Error Handling Philosophy

- **Result Pattern for Business Rejections:** When an operation violates a domain invariant (e.g., unauthorized role, mismatched epoch, or invalid signature), the method returns a typed failure result carrying an explicit error code and description.
- **Exceptions Reserved for System Failures:** Exceptions in the domain are reserved strictly for non-recoverable coding defects (such as null argument contract violations in internal constructors) or severe cryptographic faults, never for ordinary business flow control.

---

## 5. Testing Philosophy

Because `Percolator.Domain` is completely isolated from I/O and external systems:
- All domain tests are fast, deterministic in-memory unit tests.
- Tests never require network fixtures, databases, containers, or mock frameworks for external services.
- Test doubles are implemented as simple in-memory fakes implementing domain ports.
