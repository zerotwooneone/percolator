# Refinement Lenses Detailed Checklists

This document provides in-depth evaluation criteria for each of the five architectural refinement lenses.

---

## Lens 1: Scope & Feature Slice Framing
- [ ] **Negative Scope Identification**: What is explicitly *out of scope* for this feature? Are there speculative features added by AI that the user never requested?
- [ ] **Vertical Slices**: Are all files, types, and workflows grouped into autonomous feature slices (`Features/<FeatureName>/`) rather than horizontal technical buckets (`Models/`, `Services/`, `Common/`)?
- [ ] **Orchestration vs Monoliths**: Are two-phase or multi-step operations decoupled into distinct workflow steps (e.g., Querying candidates vs. Executing handshakes)?
- [ ] **Minimal API Surface**: Does the public surface of the feature application expose only what external callers require, keeping internal workflow mechanics scoped to the slice?

---

## Lens 2: Onion Architecture & Primitive Obsession

### 1. Boundary & Kernel Purity
- [ ] **Shared Kernel Integrity**: Does this feature propose modifying core domain entities or aggregates (e.g., in `Percolator.Domain`)? All feature-specific state must remain encapsulated within the feature slice's own internal entities, value objects, or reachability adapters.
- [ ] **Zero Infrastructure Leaks**: Do domain aggregates, entities, or port signatures reference transport protocols, sockets, raw network streams, database models, or serialization attributes?
- [ ] **Port Interface Isolation**: Do aggregate domain methods accept any port interfaces or services as arguments (e.g., `aggregate.DoWork(IPort port)`)? Aggregates must accept only pure domain models and clock providers.

### 2. Aggregate Completeness & Encapsulation
- [ ] **Anti-Anemic State Completeness**: Does the aggregate root hold all participants, cryptographic tokens, nonces, and lifecycle state required to validate its own transitions? Orchestration workflows must never hoard state on the aggregate's behalf.
- [ ] **Child Entity Gatekeeping**: Are internal child entities hidden behind `IReadOnlyCollection<T>` or `IReadOnlyDictionary<TKey, TValue>`? All mutations to child entities must be dispatched through domain methods on the root aggregate.
- [ ] **Atomic Aggregate Persistence**: Does the repository persist and load the aggregate root *atomically*? Partial persistence side-doors (e.g., `SaveChildStateAsync`, `SaveBitfieldAsync`) are strictly banned.

### 3. Primitive Obsession & Type Safety
- [ ] **Naked Boolean Elimination**:
  - Are booleans used to model roles, modes, or topologies? (Convert to explicit enums: e.g., `PeerRole`, `RoutingPreference`).
  - Are booleans used to model security or admission policies? (Convert to policy enums: e.g., `WanPolicy.DisabledByDefault`).
  - Are methods using boolean flag arguments for control coupling (`DoAction(bool force)`)? (Split into distinct methods or pass option value objects).
  - Are there any tri-state `bool?` variables? (Convert to explicit domain state enums).
- [ ] **Rank, Priority & Magic Numbers**: Are any priorities, ranks, or sequence types modeled as naked integers (`int Priority`)? (Wrap in bounded enums or value objects like `RoutePriority : byte`).
- [ ] **Zero Cast Operators**: Are value objects strictly constructed via factory methods (`Create`, `FromValue`, `Parse`) with zero `implicit` or `explicit` cast operators?
- [ ] **Immutable Value Objects**: Do all value objects guarantee structural immutability? (Mutations must return new copies via `WithX(...)`; mutable buffers must be classified as internal Entities).
- [ ] **Domain Math Encapsulation**: Do value objects encapsulate arithmetic and offset math (e.g., `offset.Add(slice)`), preventing callers from unboxing to raw primitives and casting?
- [ ] **Discriminated Unions vs. Nullable Doublets**: Are mutually exclusive states modeled via paired nullable properties (`PropA?`, `PropB?`)? (Convert to discriminated unions or polymorphic domain records).

### 4. Port & CQRS Contract Precision
- [ ] **Buffer Wrapping on Ports**: Do port interfaces leak naked byte arrays or `ReadOnlyMemory<byte>`? (Wrap in domain concepts: e.g., `PlaintextChunkData`, `EncryptedPayload`, typed `IStreamFramePayload`).
- [ ] **Explicit Domain Results**: Do command handlers and validation ports return expressive domain results (`ValueTask<DomainResult>`, `DiskSpacePreflightResult`), rather than naked `bool` or `void`?
- [ ] **Read Model Typings**: Do CQRS read models maintain domain enum/value object fidelity rather than reverting to raw primitives (e.g., `TransferRole Role` instead of `bool IsOutgoing`)?

---

## Lens 3: Threat Modeling & Security Posture
- [ ] **Cryptographic Parity**: Does this design violate any zero-knowledge or zero-metadata principles? (e.g., Signal Protocol parity: store-and-forward, zero presence tracking).
- [ ] **Resource Starvation / Exhaustion**: Can an attacker query this endpoint repeatedly to deplete scarce resources (e.g., exhausting One-Time Prekeys, filling disk buffers, exhausting database connections)?
- [ ] **Admission Gating**: Can an unauthenticated or phantom peer register in directories or publish state? Is an active Signed PreKey and completed mutual handshake strictly enforced?
- [ ] **Anti-Impersonation**: Does the ingress pipeline verify that the payload sender matches the cryptographic identity of the transport envelope?
- [ ] **Network & Endpoint Privacy**: Are IP addresses or hostnames exposed over WAN discovery? (Are they masked with relayed delivery routes)?
- [ ] **Temporal Correlation**: Are rendezvous tokens or identifiers linkable across time? (Are they ratcheted across discrete epoch windows like 24-hour UTC days)?

---

## Lens 4: High-Value, Low-Brittle Unit Test Design
- [ ] **Behavioral Matrices**: Are tests structured around input/output contracts, state transitions, and boundary conditions?
- [ ] **Admission & Rejection Cases**: Are positive and negative admission scenarios covered (valid SPK, missing SPK, expired SPK, closed relay policy)?
- [ ] **Deterministic Cryptography**: Are deterministic token calculations tested across identical keys, and verified to produce distinct values across different epoch windows?
- [ ] **Anti-Starvation Verification**: Does the test suite verify that candidate queries do not consume or request One-Time Prekeys?
- [ ] **Fault Tolerance**: Does the test suite verify that when one peer connection fails in a batch, subsequent connections continue unaffected?
- [ ] **Banned Anti-Patterns**:
  - No verification of internal private helper methods.
  - No mock count assertions (`Times.Once`) unless testing genuine idempotency.
  - No string-matching assertions against error messages or serialized payloads.

---

## Lens 5: Cross-Layer Infrastructure Alignment
- [ ] **Port Contracts**: Does every port in `Ports/` have a corresponding concrete adapter planned in `Percolator.Infrastructure`?
- [ ] **Protobuf Schemas**: Are all messages, discriminators, and wire fields in `*.proto` defined with correct field numbers and types?
- [ ] **Database Tables**: Are SQLite table schemas, column types, and indices defined in the persistence section of the infrastructure plan?
- [ ] **Pruning & Cleanup**: Are background retention pruners planned for any table that accumulates transient data?
- [ ] **Excise Obsolete Components**: Were legacy artifacts (e.g., old DHT tables, superseded query services) removed from the infrastructure plan?
