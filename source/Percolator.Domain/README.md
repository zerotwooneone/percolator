# Percolator.Domain

`Percolator.Domain` represents the **innermost core** of the Onion Architecture. It encapsulates enterprise business rules, cryptographic invariants, and secure communication channel domain models for the Percolator ecosystem.

As the architectural anchor, this project contains pure domain logic strictly decoupled from frameworks, transport mechanisms, wire serializations, and storage engines.

For formal layer boundaries, aggregate hydration rules, and key technical design decisions (including persistence encapsulation and zero-knowledge verification), see the project [Design Document](Design.md).

---

## 1. Architectural Invariants

### 1.1 Inward Dependency Rule
Dependencies in Onion Architecture point strictly inward:
- **Zero Inbound Dependencies:** `Percolator.Domain` has no dependencies on other solution projects (except compile-time code generators) and zero references to external frameworks.
- **Wire Contract Decoupling:** The domain never references external contracts, Protobuf definitions, or network serialization schemas. Wire models and domain models are distinct concerns separated by application mapping layers.
- **Framework Independence:** The domain is completely agnostic of databases, UI frameworks, cloud providers, and networking libraries.

### 1.2 Strict Temporal Determinism
Aggregates and entities must never access system clocks directly. All temporal logic is passed explicitly or inverted via domain clock abstractions. This guarantees deterministic state transitions, replayable event streams, and repeatable unit testing.

### 1.3 Memory & Secret Hygiene
In end-to-end encrypted messaging, cryptographic state management is a domain invariant. Any domain structure managing sensitive keys, ratchets, or secrets is responsible for its own memory lifecycle and zeroization upon disposal or state progression.

---

## 2. Scope & Layer Boundaries

The domain is strictly partitioned between business logic and infrastructure concerns. For the complete, formal breakdown of in-scope capabilities versus out-of-scope technical concerns, refer to [Design.md](Design.md#1-purpose-and-layer-boundary).

In brief:
- **In Scope**: Aggregates, value objects, domain events, Double Ratchet state machines, X3DH key agreement, group channel governance, epoch forward secrecy, and sovereign relay pre-key ledgers.
- **Out of Scope**: Protobuf serialization, SQL queries/migrations, gRPC streaming, outbox delivery retries, UI ViewModels, and platform credential vaults.

---

## 3. Error Handling Philosophy

- **Result Pattern for Business Rejections:** When an operation violates a domain invariant (such as an unauthorized role, mismatched epoch, exhausted pre-keys, or invalid cryptographic signature), the method returns a typed failure result carrying an explicit error code and description.
- **Exceptions Reserved for System Failures:** Exceptions in the domain are reserved strictly for non-recoverable coding defects (such as null argument contract violations in internal constructors) or severe unrecoverable faults, never for ordinary business flow control.

---

## 4. Testing Philosophy

Because `Percolator.Domain` is completely isolated from I/O and external systems:
- All domain tests are fast, deterministic in-memory unit tests.
- Tests never require network fixtures, databases, containers, or mock frameworks for external services.
- Test doubles are implemented as simple in-memory fakes implementing domain ports.

---

## 5. Functional Capabilities & Protocol Alignment

Percolator models the security guarantees of the **Signal Protocol** (Extended Triple Diffie-Hellman, Double Ratchet, multi-device linking, and Signal Private Groups), extended to support **direct peer-to-peer handshakes**, **decentralized arbitrary relays**, and **opaque byte transport across multi-party communication channels**.

### Core Domain Capabilities

1. **Independent Identity Management:** Supports independent creation and lifecycle management of multiple local cryptographic identities without relying on phone numbers or centralized authority.
2. **Key Rotation & Safety Number Invariants:** Identities rotate long-term identity keys, signed pre-keys, and one-time pre-key pools over time. Trust is automatically downgraded when an identity key changes.
3. **Mutual Key Agreement (X3DH):** Direct or relayed mutual authentication establishes shared secrets using ephemeral keys, identity keys, signed pre-keys, and optional one-time pre-keys.
4. **Relay Pre-Key Hosting & Fine-Grained Boundaries:** Pre-key hosting is scoped per identity and per device, preventing unbounded in-memory registry growth and eliminating lock contention across unrelated peers.
5. **Relay Quota Limits & DoS Resistance:** Relays enforce configurable maximum quotas on stored one-time pre-keys per identity to defend against storage exhaustion attacks.
6. **Atomic Pre-Key Consumption & Fallback:** Relays dispense one-time pre-keys atomically on a FIFO basis, gracefully falling back to the valid signed pre-key when one-time pre-keys are exhausted.
7. **Asynchronous Hold-and-Forward Mailboxes:** Relays store and forward opaque handshake payloads and messages for offline peers using cryptographic blind tokens.
8. **Decentralized Relay Group Genesis:** Identities establish multi-party channels through common relays with initial routing tokens and starting epochs.
9. **Private Channel Governance & Epoch Forward Secrecy:** Channel administration enforces role-based authorization, ensures an active channel is never left without an admin, and advances channel epochs upon member removal to preserve forward secrecy.
10. **Direct P2P & Multi-Relay Architectural Feasibility:** The domain operates strictly on abstract cryptographic state machines and opaque byte spans, remaining completely agnostic to physical network topology.
