
# Ingress Architecture (Aspirational)

This document describes the *application-layer* ingress architecture goals for Percolator.

Scope:

- Application library only (DDD application layer).
- No UI / Desktop host details.
- No assumption of a specific transport (gRPC, HTTP, etc.).

Core framing:

- The **Application** library is the *orchestrator* for the domain libraries.
- It coordinates domain concepts and domain services to achieve use-cases.
- It owns cross-cutting concerns at the system boundary (readiness, validation, ingress security, dispatch).
- It defines the inbound “ports” that adapters call into.

Primary objective:

- **No application-level inbound message is decrypted, persisted, or dispatched unless an active identity has been selected.**

Secondary objectives:

- Clear DDD boundaries and responsibilities.
- Transport-agnostic ingress “ports” with thin transport adapters.
- A single, testable ingress pipeline (a choke point) for security, validation, and observability.
- Defense-in-depth: readiness gating exists at multiple layers.
- TDD-friendly design: deterministic and easily unit-testable.

---

## Mental model

Inbound bytes enter the system via a transport adapter. The adapter calls an application “ingress port”. The port executes a fixed pipeline:

1. Identity readiness gate (must be active)
2. Decode minimal framing
3. Resolve session via strategies (fast-path index, slow-path probing)
4. Decrypt (or reject) — may be attempted against multiple candidates in the slow-path
5. Parse internal envelope (protobuf, etc.)
6. Validate allowlists and invariants
7. Dispatch to application commands
8. Produce an optional response payload

The pipeline is the **only place** where decrypt/parse/dispatch is allowed.

Session resolution note:

- The application must handle both:
  - **Established session ingress** (fast-path mapping from unencrypted header metadata to a known session)
  - **First-message / finalize flows** (slow-path probing across pending candidates until one decrypts)

---

## DDD layering and responsibilities

### Domain layer (pure)

- Domain entities and value objects (e.g., identity ids, peer ids, session ids).
- No knowledge of transport, protobuf, or persistence.

Domain libraries provide:

- The *language* (types) the application uses to reason about identity/session/peers.
- The *rules* (invariants) the application must preserve.
- Potentially, pure domain services (policy/rules), not orchestration.

### Application layer (this doc)

- **Orchestrates use-cases by coordinating domain libraries and infrastructure ports.**
- Owns the system boundary where untrusted inputs become domain-relevant facts.
- Defines **ports** (interfaces) that transports call into.
- Defines **ports** (interfaces) to infrastructure (persistence, cryptography implementations, etc.).
- Contains the ingress pipeline and dispatching to use-case handlers.

In other words: domain libraries should not “reach outward” to interpret inbound bytes; the application should.

### Infrastructure / adapters (out of scope)

- Implement transport servers/clients.
- Implement persistence and key stores.
- Implement cryptographic primitives.

---

## Key application-layer interfaces (ports)

### Identity readiness

The application layer should expose readiness as a small capability that other components can query.

```csharp
public interface IActiveIdentityAccessor
{
    bool IsActive { get; }
}
```

Optionally, for identity selection flows:

```csharp
public interface IActiveIdentityMutator
{
    void SetActiveIdentity(ActiveIdentitySnapshot snapshot);
    void Clear();
}
```

Notes:

- `IActiveIdentityAccessor` must be safe to call from any thread.
- The application ingress pipeline depends only on `IActiveIdentityAccessor`.

---

## Domain-provided fast/slow path strategies (crypto)

The fast-path and slow-path behavior described in the pipeline is not purely an application invention.

The cryptography domain library already models core “resolve + decrypt + update index” behavior via domain-level abstractions such as:

- `Percolator.Cryptography.IRatchetKeyIndex`
  - Fast-path lookup from an unencrypted ratchet header key to a `SessionId`.
  - Upsert on successful decrypt to make subsequent messages hit the fast-path.
- `Percolator.Cryptography.ISessionCatalog`
  - Enumerates candidate sessions for slow-path probing.
- `Percolator.Cryptography.InboundMessageResolver`
  - Implements:
    - Fast-path: index lookup
    - Slow-path: enumerate candidates and attempt decrypt until one succeeds
    - Commit behavior on success: update session state + upsert index

Application responsibility (orchestration):

- The application layer should **compose and invoke** these crypto-domain strategies as part of the ingress pipeline.
- The application layer should not duplicate their core logic; it should:
  - enforce readiness gating before any call into crypto-domain resolution/decrypt
  - constrain enumeration/probing (timeouts, max candidates) as a system boundary concern
  - map domain outcomes to `IngressDisposition`
  - decide when additional, non-crypto “pending finalize” strategies apply (e.g., handshake finalization flows that promote pending state into established sessions)

---

### Inbound ingress port (transport-agnostic)

This is the primary adapter entry point.

```csharp
public interface IMessageIngress
{
    Task<IngressResult> DeliverOpaqueAsync(
        IngressOpaquePayload payload,
        CancellationToken ct);
}

public sealed record IngressOpaquePayload(
    byte[] PayloadBytes,
    IngressPeerContext Peer,
    IngressTransportContext Transport);

public sealed record IngressPeerContext(
    PeerId RemotePeerId,
    string? RemoteEndpoint);

public sealed record IngressTransportContext(
    string TransportName,
    string? CorrelationId,
    DateTimeOffset ReceivedAtUtc);

public sealed record IngressResult(
    byte[]? ResponsePayloadBytes,
    IngressDisposition Disposition);

public enum IngressDisposition
{
    Accepted,
    Rejected_NotReady,
    Rejected_Invalid,
    Rejected_Unauthorized,
    Rejected_Expired,
    Failed_Transient,
    Failed_Permanent
}
```

Design notes:

- The port takes a high-level `IngressOpaquePayload`, not transport-specific request objects.
- The port returns `IngressResult` rather than throwing for expected rejection paths.
- Exceptions should be reserved for programming errors or truly exceptional conditions.

---

### Ingress pipeline (single choke point)

Ingress should be modeled as a pipeline with explicit stages.

```csharp
public interface IIngressPipeline
{
    Task<IngressResult> ExecuteAsync(IngressOpaquePayload payload, CancellationToken ct);
}
```

Pipeline stages (conceptual):

- `IIngressReadinessGate`
- `IOpaqueFrameDecoder`
- `ISessionResolutionStrategy` (fast-path + slow-path)
- `IInboundDecryptor` (supports candidate-based decrypt)
- `IInternalEnvelopeParser`
- `IEnvelopeValidator`
- `IEnvelopeDispatcher`

Each stage can be unit-tested independently.

---

### Dispatching to use cases

Application dispatch should be a thin layer that translates validated envelopes into use-case commands.

```csharp
public interface IEnvelopeDispatcher
{
    Task<InternalEnvelopeResponse?> DispatchAsync(
        ValidatedInternalEnvelope envelope,
        SessionContext sessionContext,
        CancellationToken ct);
}
```

This can internally use a mediator pattern, but the mediator is an implementation detail.

---

## Readiness gating (defense in depth)

### Principle

Readiness gating is an invariant:

- If `IActiveIdentityAccessor.IsActive == false` then **no** inbound message may be decrypted, parsed into an internal envelope, persisted, or dispatched.

### Where gating lives

1. **Primary gate (pipeline stage):** `IIngressReadinessGate` is the first stage.
2. **Secondary gates (use-case handlers):** critical handlers that could cause side-effects may also check readiness.
3. **Transport-level gate (optional, in adapters):** transport servers may reject calls early, but this is *not* the only protection.

The application layer should treat transport-level gating as a performance optimization, not as security.

---

## Proposed pipeline stages (aspirational)

### Stage 1: Readiness gate

- Input: `IngressOpaquePayload`
- Output: continue or `Rejected_NotReady`

### Stage 2: Opaque frame decode

- Extract minimal header metadata required for routing/session resolution.
- Must be safe to run before identity is active (but it won’t run if Stage 1 rejects).

### Stage 3: Session resolution (fast-path + slow-path)

This stage answers the question: “what session context (if any) should be used to interpret this message?”

It is explicitly *not* a guarantee that metadata always maps to a session. Session resolution is strategy-driven:

- **Fast-path (indexed)**
  - Use unencrypted header metadata (e.g., remote ratchet public key / pre-key) to lookup a known session id.
  - If found, return a single `SessionContextCandidate`.

- **Slow-path (probe pending candidates)**
  - If the fast-path misses, enumerate a bounded set of pending candidates for the active identity.
  - The output is an ordered list of candidates to try decrypting against.

- **Correlation-based short-circuit (when available)**
  - If the decoded frame carries a correlation id (e.g., invitation id), prefer a direct lookup of the matching pending candidate.

Side-effect boundary:

- This stage may consult repositories, but it must remain **read-only**.
- No persistence changes (upserts/deletes) occur in Stage 3.

Suggested port shape (conceptual):

```csharp
public interface ISessionResolutionStrategy
{
    Task<IReadOnlyList<SessionContextCandidate>> ResolveCandidatesAsync(
        DecodedOpaqueFrame frame,
        CancellationToken ct);
}

public sealed record SessionContextCandidate(
    SessionResolutionKind Kind,
    SessionContext? Established,
    PendingSessionCandidate? Pending);

public enum SessionResolutionKind
{
    Established,
    PendingProbe
}
```

The output of Stage 3 feeds Stage 4.

### Stage 4: Candidate-based decrypt (and commit)

Decrypt is responsible for turning an opaque payload into plaintext plus cryptographic context.

Key property:

- In the slow-path, decrypt may be attempted against multiple `PendingProbe` candidates until one succeeds.

Side-effect boundary (critical):

- **No state mutation may occur for failed candidates.**
- When a pending candidate successfully decrypts:
  - it is allowed to perform an atomic “commit”:
    - persist the finalized session (using responder-provided session id)
    - upsert ratchet-key index for future fast-path lookups
    - delete the consumed pending record

Suggested port split (conceptual):

```csharp
public interface IInboundDecryptor
{
    Task<DecryptResult> TryDecryptAsync(
        DecodedOpaqueFrame frame,
        IReadOnlyList<SessionContextCandidate> candidates,
        CancellationToken ct);
}

public sealed record DecryptResult(
    IngressDisposition Disposition,
    DecryptedInternalPayload? Payload,
    SessionContext? SessionContext);
```

### Stage 5: Parse internal envelope

- Parse protobuf (or equivalent) into `InternalEnvelope`.

### Stage 6: Validate invariants and allowlists

- Only allow expected message cases.
- Enforce max sizes.
- Validate required fields.
- Validate replay/monotonic counters if applicable.

### Stage 7: Dispatch

- Translate envelope to use-case commands.
- Dispatch must be side-effect controlled and fully cancellable.

### Stage 8: Optional response

- Some envelopes produce response payloads (e.g., prekey bundle response).
- Response generation should be explicit and typed.

---

## Error handling model

### Goals

- Predictable mapping of failures to dispositions.
- Avoid exceptions for expected invalid inputs.
- Avoid leaking sensitive information to the transport layer.

### Suggested approach

- Use `IngressDisposition` as the public “result classification”.
- Capture structured diagnostics internally (logs/metrics), but return minimal information.

---

## Observability

The ingress pipeline should emit:

- Correlation id propagation (from `IngressTransportContext`).
- Structured logging at stage boundaries (at least debug/trace).
- Metrics counters for dispositions (accepted/rejected/not-ready/etc.).

No stage should log plaintext payloads.

---

## Concurrency and cancellation

- All ingress APIs are async and accept `CancellationToken`.
- Pipeline stages must be cancellable and avoid blocking.
- Identity readiness access must be lock-free or low contention.

---

## TDD strategy

### Contract tests for readiness

Write tests that prove the invariant:

- When identity is inactive:
  - `IMessageIngress.DeliverOpaqueAsync` returns `Rejected_NotReady`.
  - The decryptor is never invoked.
  - No repositories are called.
  - No dispatcher calls occur.

### Stage unit tests

- `IIngressReadinessGateTests`: verifies behavior for active/inactive.
- `IOpaqueFrameDecoderTests`: invalid frames return `Rejected_Invalid`.
- `ISessionResolutionStrategyTests`: covers fast-path hit, fast-path miss, and bounded slow-path candidate enumeration.
- `IInboundDecryptorTests`: decrypt failure mapping, and slow-path “first candidate fails, second succeeds”.
- `IEnvelopeValidatorTests`: allowlist enforcement.

### Pipeline integration tests (application-only)

- Build a test pipeline with fake/stub ports.
- Verify end-to-end from opaque payload to dispatched command.

### Adapter tests (out of scope)

- Transport adapter tests should be thin: “adapter calls ingress port with correct mapping”.

---

## Evolution and extension points

- Adding a new transport should require implementing an adapter that calls `IMessageIngress`.
- Adding a new envelope type should require:
  - Updating the validator allowlist.
  - Adding a dispatcher mapping.
  - Adding tests proving readiness + validation behavior.

---

## Non-goals

- Embedding host lifecycle, UI concerns, or transport server configuration in the application layer.
- Making the application layer depend on gRPC-specific request/response types.
- Using exceptions as the primary control flow for invalid remote input.

