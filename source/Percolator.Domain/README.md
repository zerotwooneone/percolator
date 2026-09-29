# Percolator.Domain

`Percolator.Domain` represents the **innermost core** of the Onion Architecture. It encapsulates enterprise business rules, cryptographic invariants, and conversational domain models for the Percolator ecosystem.

As the architectural anchor, this project contains pure domain logic strictly decoupled from frameworks, transport mechanisms, wire serializations, and storage engines.

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

## 2. What Belongs in the Domain (In Scope)

The domain contains only enterprise business logic and cryptographic invariants that are true regardless of network transport, storage engine, or UI presentation:

| Concept | High-Level Scope & Guidelines |
| :--- | :--- |
| **Aggregates & Entities** | Conceptual consistency boundaries that encapsulate state and enforce business invariants. State mutations occur exclusively through domain methods that maintain invariant validity. |
| **Value Objects** | Immutable types identified solely by their attributes and validation rules rather than an entity identifier. Value objects enforce structural validity upon creation, guarantee value equality, and encapsulate domain validation. |
| **Domain Events** | Immutable notifications capturing significant business and cryptographic occurrences within an aggregate boundary. Used to communicate state changes to outer layers without coupling. |
| **Cryptographic Protocol State Machines** | In-memory representations of end-to-end cryptographic state transitions (Double Ratchet symmetric chain advancing, Diffie-Hellman ratchet steps, out-of-order skipped key cache limits, and key rotation lifecycle). |
| **Key Exchange & Agreement Invariants** | Extended Triple Diffie-Hellman (X3DH) mutual authentication state logic, pre-key bundle validation, signature verification, and master secret derivation. |
| **Group Invariants & Governance** | Conversation membership rosters, administrative roles, admin-gated mutations (membership changes, title renames, role promotions/demotions), and the invariant that a group must never be left without an active administrator. |
| **Epoch Advancement & Forward Secrecy** | Epoch advancement rules upon group membership removal, ensuring cryptographic forward secrecy across group state changes. |
| **Relay Ledger & Mailbox Governance** | Relay-hosted pre-key quotas, atomic pre-key consumption with signed fallback, hold-and-forward mailbox queues, and blinded token routing authorization without social graph linkage. |
| **Domain Ports (Interfaces)** | Outward-facing abstractions defining required capabilities (such as cryptographic engines, zero-knowledge proof engines, or aggregate repositories) without leaking external implementation details. |
| **Domain Results & Errors** | Strongly typed, allocation-conscious result representations for expected domain rejections, returning explicit error codes rather than throwing exceptions for ordinary rule violations. |

---

## 3. What Does NOT Belong in the Domain (Out of Scope)

The following concerns are explicitly excluded from `Percolator.Domain` and are handled by outer layers:

| Excluded Concern | Responsible Layer | Architectural Reason |
| :--- | :--- | :--- |
| **Wire & Transport Framing** | Application / Infrastructure | Protobuf DTOs, wire byte framing, packet delimiters, and Associated Data (AD) byte construction belong in the application pipeline before delegating to domain cryptographic ports. |
| **Database & ORM Plumbing** | Infrastructure / Persistence | SQL queries, migrations, table schemas, change tracking, and database engines (SQLCipher/SQLite/EF Core) are storage details. |
| **Transport Delivery & Retries** | Application / Infrastructure | Transactional outbox queues, retry policies, exponential backoff with jitter, socket management, and delivery channel routing (direct P2P vs. relay) belong to application orchestration. |
| **Message Ordering Buffers** | Application | Caching out-of-order messages while awaiting sender key distribution payloads is an application ingress buffering responsibility. |
| **Profile Metadata & Media** | Application | Display profiles (nicknames, avatar images, bio status, profile key symmetric encryption packages) are application-level user metadata, not core cryptographic protocol invariants. |
| **Group Invitation Consent Workflows** | Application | Managing inbound group invitation requests (pending approval, acceptance, rejection) is an application user-consent state machine. |
| **Peer & Network Discovery** | Application / Infrastructure | Kademlia DHT routing tables, UDP broadcast listeners, rendezvous ping/pong protocols, and network reachability probing belong in application plugins and infrastructure network adapters. |
| **OS Security & Secret Storage** | Infrastructure | Platform-specific credential encryption (DPAPI, macOS Keychain, Linux Keyutils) and file system ACLs belong to platform security adapters. |
| **Transport Streaming & Sockets** | Infrastructure | Resilient server-streaming gRPC connections, stream reconnection loops, keep-alives, and TLS certificate thumbprint pinning callbacks are infrastructure concerns. |
| **Presentation & UI State** | Presentation / UI | ViewModels, UI bindings, view formatting, localization, and user input validation must never touch domain models. |

---

## 4. Error Handling Philosophy

- **Result Pattern for Business Rejections:** When an operation violates a domain invariant (such as an unauthorized role, mismatched epoch, exhausted pre-keys, or invalid cryptographic signature), the method returns a typed failure result carrying an explicit error code and description.
- **Exceptions Reserved for System Failures:** Exceptions in the domain are reserved strictly for non-recoverable coding defects (such as null argument contract violations in internal constructors) or severe unrecoverable faults, never for ordinary business flow control.

---

## 5. Testing Philosophy

Because `Percolator.Domain` is completely isolated from I/O and external systems:
- All domain tests are fast, deterministic in-memory unit tests.
- Tests never require network fixtures, databases, containers, or mock frameworks for external services.
- Test doubles are implemented as simple in-memory fakes implementing domain ports.

---

## 6. Functional Capabilities & Protocol Alignment

Percolator models the security guarantees of the **Signal Protocol** (Extended Triple Diffie-Hellman, Double Ratchet, multi-device linking, and Signal Private Groups), extended to support **direct peer-to-peer handshakes**, **decentralized arbitrary relays**, and **opaque byte transport**.

### 6.1 Core Domain Capabilities

1. **Independent Identity Management:**
   - Supports independent creation and lifecycle management of multiple local cryptographic identities without relying on phone numbers or centralized authority.
2. **Key Rotation & Safety Number Invariants:**
   - Identities rotate long-term identity keys, signed pre-keys, and one-time pre-key pools over time.
   - When a remote peer's public identity key changes, existing verification is invalidated and trust is automatically downgraded to untrusted, requiring re-verification of the contact's safety number.
3. **Mutual Key Agreement (X3DH):**
   - Direct or relayed mutual authentication establishes shared secrets between identities using ephemeral keys, identity keys, signed pre-keys, and optional one-time pre-keys.
   - Distinct cryptographic key roles separate digital signatures from Diffie-Hellman key exchange.
   - Derived master secrets initialize end-to-end symmetric ratchet sessions.
4. **Relay Pre-Key Hosting & Fine-Grained Boundaries:**
   - Any node acting as a relay can host pre-key material for peers on an opt-in basis.
   - Pre-key hosting is scoped per identity and per device, preventing unbounded in-memory registry growth and eliminating lock contention across unrelated peers.
5. **Relay Quota Limits & DoS Resistance:**
   - Relays enforce configurable maximum quotas on stored one-time pre-keys per identity to defend against storage exhaustion attacks.
6. **Atomic Pre-Key Consumption & Fallback:**
   - Relays dispense one-time pre-keys atomically on a first-in, first-out basis.
   - When one-time pre-keys are exhausted, relays gracefully fall back to serving the valid signed pre-key, preserving forward secrecy while maintaining service continuity.
7. **Asynchronous Hold-and-Forward Mailboxes:**
   - Relays store and forward opaque handshake payloads and messages for offline or unreachable peers.
   - Routing and delivery authorization rely on cryptographic blind tokens, preventing relays from associating senders with recipients.
8. **Decentralized Relay Group Genesis:**
   - Identities that have established secure sessions with a common relay can establish group conversations.
   - The relay manages group genesis, recording conversation identity, initial routing tokens, and starting epoch.
9. **Private Group Governance & Epoch Forward Secrecy:**
   - Group administration enforces role-based authorization for membership changes, group renames, and administrative promotions/demotions.
   - Enforces the core invariant that an active group can never have its last remaining administrator removed or demoted.
   - Removing a group member automatically advances the conversation epoch, discarding previous sender keys to ensure former members cannot decrypt future communications (forward secrecy).
   - Group roster updates on relays are verified anonymously using zero-knowledge proofs, preventing the relay from reconstructing the social graph.
10. **Direct P2P & Multi-Relay Architectural Feasibility:**
    - The domain operates strictly on abstract cryptographic state machines and opaque byte spans, remaining completely agnostic to physical network topology (direct socket, local mesh, or multi-hop relays).
