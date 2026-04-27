# Simulator (Desktop.Wpf)

## Purpose

The **Simulator** is an in-process, in-memory runtime used by the Desktop/WPF application to model multiple “simulated peers” and relay topologies without requiring real network processes.

It exists to:

- Provide a fast feedback loop for networking/session/relay behaviors.
- Exercise the same *public networking seams* as real peers (transport/session/message boundaries), while replacing the “real network” with an in-memory delivery path when the destination is simulator-owned.

## Core design idea

The main window should behave as though simulated peers are *real* peers.

- Route planning uses normal persisted routing profiles.
- Outbound sends use the normal transport services.
- Only at the **transport boundary** do we decide whether the send is “real network” or “simulator-owned”.

## Authoritative simulator address space

The simulator reserves the loopback range:

- **`127.77.*`** (IPv4, literal dotted-quad)

Rules:

- Any outbound send whose destination host is a **literal dotted-quad** in `127.77.*` is treated as **simulator-owned**.
- This is not DNS-based detection. We do not resolve hostnames to determine simulator ownership.
- For simulator-owned destinations, **there is no fallback to real networking**.

## Boundary and access rules (non-negotiable)

### Main window vs simulator runtime state

- The simulator maintains **its own runtime state** (peers, keys, sessions, relay queues, inbox buffers).
- **Simulator-owned runtime state must never be written to the main window SQLite database**.
- The main window **must not directly access simulator runtime state**.

The *only* allowed “direct access” to simulator runtime state is from the simulator boundary components themselves (e.g., simulator services and outbound interceptor implementation).

### Persistence

- The main window may persist **simulated peers as normal peers** (routing profiles, endpoints, etc.) in the main SQLite database.
- What must not be persisted to the main SQLite database includes (non-exhaustive):
  - simulator internal peer registry
  - identity keys / prekeys used by simulator
  - sessions managed by simulator
  - relay downstream queues / inbox buffers

### Interception must live at the transport boundary

Simulator behavior is implemented by intercepting outbound transport sends, not by adding simulator-specific routing logic higher up the stack.

In particular:

- `MessageService` must not contain simulator-specific logic for “route via simulator relay”.
- Simulator interception is authoritative at `GrpcMessageTransportService` for opaque message sends.

## Outbound opaque message interception (authoritative)

Outbound opaque messages are intercepted through:

- `ISimulatorOutboundInterceptor.InterceptDeliverOpaqueMessageAsync(DnsEndPoint endpoint, DeliverOpaqueMessageRequest request, CancellationToken ct)`

Result semantics:

- **`NotForSimulator`**
  - Destination is not simulator-owned; proceed with normal gRPC send.
- **`DeliveredToSimulator`**
  - Destination is simulator-owned and matches a simulated peer by exact `(Host, Port)`.
  - The message is delivered in-memory to the simulator runtime.
  - Transport returns a minimal valid `DeliverOpaqueMessageResponse` (Version=1) without creating an HTTP client/channel.
- **`Undeliverable`**
  - Destination is simulator-owned (`127.77.*`) but **no simulated peer exists** at that `(Host, Port)`.
  - Transport **fails fast** (throws) without creating an HTTP client/channel.

### Simulated peer identity

A simulated peer is identified for interception purposes by:

- Exact endpoint match: **`(Host, Port)`**

Do not use `PeerId` (GUID) as an interop detection mechanism.

## Relay semantics

Relayed sends are still “normal” outbound sends from the main window’s perspective:

- Route planning remains normal (persisted routing profiles select a relay host endpoint).
- The outbound send targets the **relay host endpoint**.

If the relay host endpoint is simulator-owned (`127.77.*`), the transport interception delivers the opaque payload into the simulator runtime.

Within the simulator runtime, relay envelopes are processed and downstream relay queue entries are created for the intended target.

## Unit testing guidance (Simulator boundaries)

Prefer assertions on *observable behavior* and external side-effects:

- It is acceptable to assert that **no** `IHttpClientFactory.CreateClient(...)` call occurs for simulator-delivered / undeliverable sends.
- Avoid brittle tests that assert internal method invocations inside simulator state services.
- For relay behaviors, prefer asserting observable queue/state changes (when integration tests are introduced).

## Quick “do / don’t” checklist

### Do

- Treat `127.77.*` literal IPv4 hosts as simulator-owned.
- Fail fast when a simulator-owned destination has no matching simulated peer.
- Keep simulator interception at the transport boundary.
- Keep simulated-peer routing profiles/endpoints persisted like normal peers.

### Don’t

- Don’t write simulator runtime state (peers/keys/sessions/queues) to main SQLite.
- Don’t allow main window code to reach into simulator runtime state directly.
- Don’t add simulator-specific relay routing logic to `MessageService`.
- Don’t use `PeerId` as a simulator detection mechanism across boundaries.
