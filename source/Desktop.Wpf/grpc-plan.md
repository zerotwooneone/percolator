# Desktop.Wpf gRPC Hosting Plan — Identity-gated Ingress

Goal: Treat the **Desktop.Wpf application itself as the gRPC peer**. Desktop.Wpf hosts a `PercolatorMessageService` gRPC **server** that other peers can call to:

- Establish secure sessions (X3DH + Double Ratchet bootstrap)
- Deliver **opaque encrypted payloads**

Critical security requirement:

- The gRPC server may listen early, but **must not perform application-level work (decrypt/dispatch/persist side effects) until an ActiveIdentity is selected**.

This plan is broken into **large chunks** that can be implemented one at a time.

Notes:

- Chunks may temporarily break compilation.
- Do not add placeholder/no-op implementations between chunks (avoid dead code and missed requirements).
- The Application library is the orchestrator for domain libraries; transport adapters should call explicit application ports.

---

## Chunk 1 — Desktop.Wpf hosts Kestrel + gRPC server (identity gate at transport boundary)

**Intent:** Start the in-process gRPC server inside Desktop.Wpf’s Generic Host.

**What we have now:** This is already implemented.

**Exit criteria:**

- Desktop.Wpf starts Kestrel HTTP/2 and maps the gRPC service.
- A global interceptor rejects all gRPC calls until `IActiveIdentityAccessor.IsActive` is true.

---

## Chunk 2 — Application-layer readiness and defense-in-depth gating

**Intent:** Make identity readiness a first-class application concern and ensure inbound processing cannot occur before readiness.

**What we have now:**

- `IActiveIdentityAccessor` backed by `ActiveIdentityContext`.
- `IdentityReadinessInterceptor` registered globally.
- Defense-in-depth gating exists in at least one inbound handler.

**What may still be missing:**

- Consistency: all ingress entry points in the application layer should consult `IActiveIdentityAccessor`, not ad-hoc checks.

**Exit criteria:**

- All inbound message processing paths are gated by `IActiveIdentityAccessor` before decrypt/dispatch.

---

## Chunk 3 — Introduce application ingress ports (Option 2 from ingress-architecture)

**Intent:** Create an explicit transport-agnostic ingress API in `Percolator.Application` that transport adapters can call.

**Key point:** MediatR can be used *internally* for orchestration, but the host should call stable application ports.

**Steps:**

- **[3.1] Define the public ingress port** (Application)
  - Add `IMessageIngress` with a method for opaque delivery (unary-style).
  - Define application-owned DTOs:
    - `IngressOpaquePayload` (payload bytes + remote peer context + transport metadata)
    - `IngressResult` (`IngressDisposition` + optional response bytes)

- **[3.2] Define the ingress pipeline contract** (Application)
  - Add `IIngressPipeline` and stage interfaces as needed.
  - The pipeline must express that session resolution is **strategy-driven** (fast-path + slow-path).

- **[3.3] Decide what is “public API surface”**
  - The ingress port (`IMessageIngress`) is host-facing.
  - MediatR requests/notifications are internal implementation details.

**Exit criteria:**

- Application exposes `IMessageIngress` and the minimal types required for adapters.

---

## Chunk 4 — Implement ingress pipeline by composing crypto-domain strategies

**Intent:** Implement the application ingress pipeline by orchestrating domain libraries and infrastructure ports, leveraging existing crypto-domain fast/slow-path strategy code.

**Key domain strategy already available:**

- `Percolator.Cryptography.InboundMessageResolver` (fast-path index + slow-path probe)

**Steps:**

- **[4.1] Implement `MessageIngress` (Application)**
  - `MessageIngress.DeliverOpaqueAsync(...)` performs:
    - readiness gate (`IActiveIdentityAccessor`)
    - size checks / allowlists (as boundary concerns)
    - calls into the crypto-domain decrypt/resolve strategy via an application service (e.g., existing `ISecureMessagingService`)
    - parses internal envelope (protobuf)
    - dispatches to internal application use-cases (MediatR or explicit dispatcher)
    - returns an optional response payload

- **[4.2] Make session resolution explicit at the application boundary**
  - The pipeline must not assume the header key always resolves.
  - The crypto-domain resolver already supports a slow-path; application constrains it (timeouts/max candidates) and maps outcomes.

- **[4.3] Side-effect boundary**
  - No persistence side effects when identity is inactive.
  - When decrypt succeeds, domain/application may update session state and indexes.

**Exit criteria:**

- There is exactly one application ingress choke-point (`IMessageIngress`) that adapters call.
- All decrypt/parse/dispatch occurs behind readiness gating.

---

## Chunk 5 — Adapt gRPC service to call application ingress port (thin adapter)

**Intent:** Make `PercolatorMessageService` a thin transport adapter that does not perform application logic.

**Steps:**

- **[5.1] gRPC method mapping**
  - For “deliver opaque” RPCs:
    - Map request to `IngressOpaquePayload`.
    - Call `IMessageIngress.DeliverOpaqueAsync(...)`.
    - Map `IngressResult` back to gRPC response.

- **[5.2] Keep the global interceptor**
  - The interceptor remains a fast-fail safety net.
  - The application ingress still gates (defense in depth).

- **[5.3] Lifetimes**
  - Prefer gRPC service implementation to be stateless and per-call safe.
  - Avoid forcing singleton gRPC handlers if they need scoped dependencies.
  - The stable boundary is the application port; DI lifetimes can be chosen pragmatically.

**Exit criteria:**

- gRPC service depends on `IMessageIngress` (and other application ports for handshake RPCs).
- gRPC service never decrypts or dispatches directly.

---

## Chunk 6 — Tests and hardening (application + transport)

**Intent:** Add tests proving readiness gating, ingress invariants, and correct adapter wiring.

**Steps:**

- **[6.1] Application unit tests**
  - `IMessageIngress` readiness contract:
    - When inactive, returns `Rejected_NotReady` and does not invoke decrypt/dispatch.
  - Envelope validation tests:
    - unknown/unsupported envelope types are rejected.

- **[6.2] Transport adapter tests**
  - gRPC adapter maps request -> `IngressOpaquePayload` correctly.
  - gRPC interceptor rejects when identity inactive.

**Exit criteria:**

- Tests lock in: “no decrypt/dispatch before identity”.

---

## Summary (north-star)

- Desktop.Wpf hosts the gRPC server.
- gRPC is a thin adapter over application ports.
- The Application library orchestrates domain libraries (crypto/session) and defines the ingress choke-point.
- Identity readiness is enforced at multiple layers (interceptor + application ingress + critical handlers).

---

## Chunk 7 — Simulator: inject a synthetic gRPC message into the real ingress path

**Intent:** Update the WPF simulator so it exercises the same code path as real network traffic by constructing a synthetic gRPC request and feeding it through the gRPC server implementation (or directly into the application ingress port used by that server).

Target:

- `Desktop.Wpf/Features/Simulator/PendingHandshakeSimulatorService.AddSyntheticPendingAsync`

**Steps:**

- **[7.1] Stop writing pending handshake state directly**
  - The simulator should not call repositories or “pending session” persistence APIs directly.
  - It should simulate *ingress*.

- **[7.2] Construct a synthetic inbound gRPC request**
  - Build the appropriate protobuf message that represents a “pending handshake request” arriving from a remote peer.
  - Wrap it in the gRPC request type used by the server method (opaque bytes in the same shape as production).
  - Include remote peer metadata as the gRPC layer would provide (peer id, endpoint, correlation id) if applicable.

- **[7.3] Inject through the real handler**
  - Preferred: invoke the gRPC service method on the *in-proc server implementation* (as a thin adapter), so the simulator validates:
    - request mapping -> `IngressOpaquePayload`
    - readiness gating semantics
    - envelope parsing/dispatch behavior
  - If calling the gRPC method directly is too awkward due to `ServerCallContext`, call the application port (`IMessageIngress`) that the gRPC service uses.
    - The critical requirement is: **the simulator must use the same ingress choke-point as real gRPC traffic**.

- **[7.4] Respect identity gating**
  - The simulator must behave like the real system:
    - if no active identity, the injected message is rejected (or returns `Rejected_NotReady`).
    - if an identity is active, the injected message flows through decrypt/dispatch.

**Exit criteria:**

- `AddSyntheticPendingAsync` produces a realistic gRPC-shaped message and injects it into the same ingress path used by production.
- The simulator no longer bypasses ingress by manipulating state directly.
