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
- [ ] **Dependency Direction**: Does the feature slice depend only on `Percolator.Domain`, `Percolator.PluginSdk`, or local internal types? (Zero dependencies on EF Core, SQLite, ASP.NET Core, gRPC, or System.Net).
- [ ] **Primitive Obsession Audit**:
  - Are limits, sizes, or counts wrapped in bounded Value Objects (e.g., `DiscoveryResultLimit`)?
  - Are tokens, hashes, and identifiers wrapped in strongly typed Value Objects (e.g., `BlindedLocator`, `DeliveryToken`)?
  - Are time windows or epochs wrapped in dedicated domain models (e.g., `DiscoveryEpoch`)?
- [ ] **Zero Cast Operators**:
  - Verify there are zero `public static implicit operator` declarations.
  - Verify there are zero `public static explicit operator` declarations.
  - Verify construction passes through factory methods (`Create`, `FromValue`, `TryCreate`) with domain validation.
  - Verify access is via named properties (`Value`, `Bytes`, `Id`).

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
