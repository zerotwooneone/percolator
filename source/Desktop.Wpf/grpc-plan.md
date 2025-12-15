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

## Completed milestones 

- Desktop.Wpf hosts a gRPC server and a global interceptor rejects all RPCs until an active identity exists.
- The application layer defines an identity readiness contract and inbound processing is defense-in-depth gated before decrypt/dispatch.
- Application ingress ports and DTOs exist (`IMessageIngress`, `IngressOpaquePayload`, `IngressResult`) so adapters depend on stable ports.
- Ingress is implemented behind a single choke-point with readiness/validation enforced at the boundary and opaque delivery routed through the existing decrypt/dispatch flow.
- `PercolatorMessageService` is a thin adapter that maps gRPC requests to `IngressOpaquePayload`, calls `IMessageIngress`, and maps `IngressResult` back to a gRPC response/status.
- Tests exist that lock in readiness semantics and adapter mapping so “no decrypt/dispatch before identity” is enforced.

---

## Chunk 1 — Simulator injects through the real ingress choke-point (replaces the old simulator chunk)

**Intent:** Ensure simulator activity exercises the same ingress boundary as production traffic, instead of writing pending handshake state directly.

**Target:**

- `Desktop.Wpf/Features/Simulator/PendingHandshakeSimulatorService.AddSyntheticPendingAsync`

**Steps:**

- **[1.1] Identify the simulator’s current side effects**
  - Inventory which repositories and notifications are written today (e.g., pending sessions, peer identities, MediatR notifications) and treat them as outcomes to be produced by ingress, not by the simulator.

- **[1.2] Choose the injection path and make it explicit**
  - Option A (preferred when feasible): call the gRPC method in-proc (`PercolatorMessageService.DeliverOpaqueMessage`) using a test `ServerCallContext`.
  - Option B (fallback): call the application port (`IMessageIngress.DeliverOpaqueAsync`) directly.
  - Document which path is used and why (context/metadata availability, friction of `ServerCallContext`, etc.).

- **[1.3] Construct a realistic inbound payload**
  - Build bytes in the same “opaque payload” shape that production ingress expects for handshake-related traffic (e.g., a `HandshakeInitiatorHello` payload when that is a supported inbound form).
  - Populate transport metadata consistently (peer string, correlation id) so downstream logging/diagnostics have stable inputs.

- **[1.4] Enforce identity gating semantics in the simulator flow**
  - If no active identity exists, simulator injection must be rejected (or yield `NotUntil`/`Rejected_NotReady`) and must not persist pending handshake state.
  - If an identity is active, simulator injection must flow through the same decrypt/dispatch path used by real traffic.

- **[1.5] Update UI affordances to reflect ingress outcomes**
  - The simulator UI should report whether injection was accepted vs rejected (not-ready/invalid/failed) without relying on “repo write succeeded” as a proxy.

**Exit criteria:**

- `AddSyntheticPendingAsync` no longer calls pending-session persistence APIs directly.
- The simulator produces a synthetic inbound payload and injects it through the same ingress choke-point used by production traffic.
- When identity is inactive, no pending-handshake persistence side effects occur.
