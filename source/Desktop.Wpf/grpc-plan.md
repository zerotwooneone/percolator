# Desktop.Wpf gRPC Hosting Plan — PercolatorMessageService

Goal: Treat the **Desktop.Wpf application itself as the grpc x3dh peer**. The WPF app hosts the `PercolatorMessageService` gRPC **server** and exposes an endpoint peers can use to deliver opaque, encrypted messages. All higher‑level chat behavior runs in‑proc over X3DH/Double Ratchet.

The gRPC host and transport run as long‑lived background services inside the WPF Generic Host, but **message decryption and handling must be gated on ActiveIdentity selection** so we only process traffic when an identity context exists.

We break this into large chunks that can be implemented one at a time.

---

## Chunk 1 — gRPC server hosting model and DI in Desktop.Wpf

**Intent:** Host the `PercolatorMessageService` gRPC **server** directly inside Desktop.Wpf using the .NET Generic Host, while keeping the UI viewmodel‑first. The WPF app becomes the chat node that other peers connect to.

**Steps:**

- **[1.1] Clarify roles and boundaries**
  - Desktop.Wpf is  both UI and gRPC server.
  - The gRPC server exposes only the minimal public contract:
    - Establish secure sessions (X3DH + Double Ratchet bootstrap).
    - Exchange opaque, encrypted application payloads.
  - All chat/DHT/file‑sharing logic remains internal and decoupled from the public service.

- **[1.2] Integrate gRPC server into the .NET Generic Host**
  - Confirm `App.xaml.cs` already uses `Host.CreateDefaultBuilder`.
  - Inside Desktop.Wpf startup:
    - Configure Kestrel with HTTP/2 and the desired endpoint (loopback + configurable port).
    - Call `AddGrpc()` and map `PercolatorMessageService` in `Configure`/`MapGrpcService` equivalent for the WPF host.
  - Treat the gRPC server as a **singleton** infrastructure component whose lifetime is tied to the WPF process.

- **[1.3] Define application‑layer transport abstractions**
  - In `Percolator.Application.Network`, ensure we have:
    - `IMessageIngress` for receiving opaque inbound frames from the gRPC service implementation.
    - `IMessageEgress` for sending outbound opaque frames (the server side of a bidirectional relationship, if needed).
  - The gRPC service (`PercolatorMessageService` class) should depend only on these abstractions, not on WPF types.

- **[1.4] Decide on lifetimes**
  - gRPC server and `PercolatorMessageService` implementation: **singleton**.
  - `IMessageIngress`/`IMessageEgress` infrastructure services: singleton.
  - Identity/session‑scoped services (crypto, session directories, etc.) remain scoped per ActiveIdentity.
  - ViewModels stay transient or identity‑scoped; they only see decrypted application‑level events.

**Exit criteria for Chunk 1:**

- Desktop.Wpf:
  - Starts a gRPC server endpoint as part of its Generic Host.
  - Registers `PercolatorMessageService` as a singleton service implementation.
  - Wires `PercolatorMessageService` to `IMessageIngress`/`IMessageEgress`, with no UI or identity assumptions.

---

## Chunk 2 — Identity‑gated inbound processing contract

**Intent:** Introduce an **identity‑aware** inbound processing surface in the application layer and gate usage from the gRPC service on ActiveIdentity being present. The gRPC endpoint may begin accepting connections early, but it **must not decrypt or dispatch** until identity context exists.

**Steps:**

- **[2.1] Express ActiveIdentity in application layer**
  - Confirm existing `IIdentityScopeAccessor` / `ActiveIdentityContext` pattern (per previous DI work).
  - Define in `Percolator.Application` an interface like:
    - `IActiveIdentityAccessor` (read‑only): exposes `IsActive`, `ActiveIdentityId`, and possibly `DirectSessionDirectory` for that identity.
  - This accessor should be backed by the same identity scope Desktop.Wpf already uses for Shell + session VMs.

- **[2.2] Define an inbound dispatcher interface**
  - In `Percolator.Application` define:
    - `IInboundEnvelopeHandler` with a method like:
      - `Task HandleAsync(OpaqueInboundMessage message, CancellationToken ct);`
  - Implementation will:
    - Require an active identity context (throws or no‑op if none).
    - Use Double Ratchet session directory + crypto to decrypt envelopes.
    - Inspect decrypted `InternalEnvelope` and route via MediatR / internal bus.

- **[2.3] Identity gating semantics**
  - `IInboundEnvelopeHandler` must:
    - Short‑circuit when `IActiveIdentityAccessor.IsActive == false`:
      - Either drop with a diagnostic log or buffer (Chunk 4 can add buffering if desired).
    - Never attempt decryption without an identity context; this enforces security and correct DI lifetimes.

- **[2.4] Wire WPF identity selection to ActiveIdentity**
  - Ensure that when the user selects an identity in WPF (Shell startup flow), the app:
    - Creates/disposes an **identity scope** containing:
      - Crypto/session services.
      - Any MediatR handlers needed for decrypted envelopes.
    - Updates `IActiveIdentityAccessor` to reflect the newly active identity.

**Exit criteria for Chunk 2:**

- `IActiveIdentityAccessor` (or equivalent) APIs exist and are wired to Desktop.Wpf’s identity scope.
- `IInboundEnvelopeHandler` interface + default app‑layer implementation exist.
- Implementation guards on `IsActive` before decryption/dispatch, even though the gRPC server is live.

---

## Chunk 3 — PercolatorMessageService server implementation and ingress pipeline

**Intent:** Implement `PercolatorMessageService` as a gRPC server endpoint hosted inside Desktop.Wpf. Its implementation should forward inbound opaque frames into the application‑layer ingress pipeline and respect identity gating.

**Steps:**

- **[3.1] Review gRPC contract**
  - `Percolator.Contracts` contains the `.proto` for `PercolatorMessageService`.
  - Confirm the service exposes operations for:
    - Establishing sessions (handshake RPCs).
    - Delivering opaque encrypted payloads (e.g., bidirectional streaming or unary + queueing).

- **[3.2] Implement PercolatorMessageService in `Percolator.Application.Network`**
  - Implement the generated gRPC base class (e.g., `PercolatorMessageService.PercolatorMessageServiceBase`).
  - This implementation should:
    - Validate and normalize incoming requests.
    - For opaque message delivery calls, construct an `OpaqueInboundMessage` DTO and pass it to `IMessageIngress`.
    - Never directly inspect decrypted contents; it only deals with opaque bytes and routing metadata.

- **[3.3] Connect service to identity‑gated handler**
  - `IMessageIngress` implementation in `Percolator.Application.Network` should:
    - Use `IInboundEnvelopeHandler` to actually decrypt and dispatch.
    - Ensure `IActiveIdentityAccessor` gating is honored.
  - This preserves:
    - **Public boundary** (gRPC service) -> **Transport ingress** -> **Identity‑aware handler**.

- **[3.4] Shutdown semantics**
  - On WPF host shutdown:
    - The Generic Host stops Kestrel and the gRPC server.
    - Identity scopes and crypto/session resources are disposed.

**Exit criteria for Chunk 3:**

- `PercolatorMessageService` is fully implemented as a gRPC server in‑process with Desktop.Wpf.
- Inbound opaque messages from remote peers reach `IInboundEnvelopeHandler` via `IMessageIngress`.
- Identity gating still prevents decryption/dispatch when no ActiveIdentity is selected.

---

## Chunk 4 — UI and reactive integration (R3 + ViewModels)

**Intent:** Surface decrypted, routed application messages (e.g., chat envelopes, system notifications) into WPF ViewModels using R3, while keeping concerns separated.

**Steps:**

- **[4.1] Application‑level inbound bus**
  - In `Percolator.Application`, ensure decrypted `InternalEnvelope` payloads are published on an internal bus:
    - Likely via MediatR notifications (`INotification`) or a dedicated R3 `Observable<T>`.
    - E.g., `IInboundApplicationEventBus` with `IObservable<ApplicationEvent>`.

- **[4.2] Bridge to WPF ViewModels**
  - In Desktop.Wpf feature VMs (Chat, Sessions, etc.):
    - Inject the app‑layer subscription surface (`IInboundApplicationEventBus`).
    - Use R3 subscription patterns:
      - `ObserveOnCurrentSynchronizationContext()` for UI updates.
      - Update `BindableReactiveProperty<T>` or reactive collections.
    - Dispose subscriptions with `DisposableBag` in each VM.

- **[4.3] Identity switching behavior**
  - When identity changes:
    - The old identity scope is disposed; its event handlers/bus subscriptions go away.
    - ViewModels either:
      - Are recreated for the new identity scope, or
      - Rebind to a new `IInboundApplicationEventBus` for the new identity.
    - Ensure no messages are delivered to VMs once their identity scope is disposed.

- **[4.4] UX considerations**
  - On app startup before identity selection:
    - Hosted service may already be connected, but messages either:
      - Are ignored, or
      - Optionally buffered in memory (advanced, can be deferred); default is ignore for simplicity.
  - Once an identity is selected:
    - New inbound messages are decrypted and routed to the corresponding conversation/session lists.

**Exit criteria for Chunk 4:**

- VMs receive decrypted messages when an identity is active.
- No messages affect UI when no identity is active.
- Identity switches correctly reset visible conversations and inbound subscriptions.

---

## Chunk 5 — Diagnostics, resilience, and TDD

**Intent:** Add the tests and hardening required to trust this pipeline in a security‑sensitive app.

**Steps:**

- **[5.1] Unit tests (Application & Desktop.Wpf)**
  - App layer:
    - `IInboundEnvelopeHandler` tests:
      - Drops/ignores when `IsActive == false`.
      - Decrypts and routes correctly when active.
      - Propagates or wraps crypto failures per security rules (exceptions from domain, guarded by Application).
  - Desktop.Wpf:
    - Hosted service tests using fake `GrpcMessageTransportService`:
      - Verifies inbound messages are forwarded to handler.
      - Verifies cancellation stops the loop.
      - Uses `IClock`/`TimeProvider` for deterministic retry behavior where applicable.

- **[5.2] Integration tests (optional)**
  - End‑to‑end tests in a test host that spins up:
    - A fake Percolator node gRPC server.
    - The WPF host (without real UI) hosting the client + application.
    - Assert that sending an opaque message results in a VM observing an updated `BindableReactiveProperty` when identity is active.

- **[5.3] Observability and error handling**
  - Log pipeline errors from the hosted service (connection failures, retries) via `ILogger`.
  - Ensure domain libraries continue to throw rather than log; Application/desktop layers handle logging.
  - Hook R3 `ObservableSystem.RegisterUnhandledExceptionHandler` to route background stream errors to logging.

**Exit criteria for Chunk 5:**

- High‑value behavioral tests cover:
  - Identity gating.
  - Inbound message forwarding from gRPC service to handler.
  - Graceful gRPC server shutdown and error handling.
- Logs and diagnostics make it easy to trace gRPC connectivity and message flow without leaking sensitive data.

---

## Summary

- **Transport hosting** lives in Desktop.Wpf via a long‑running `BackgroundService` that owns the Percolator gRPC client.
- **Message decryption and routing** live in `Percolator.Application`, gated by an explicit ActiveIdentity abstraction.
- **ViewModels** only see already‑decrypted, domain‑level events via R3/observable buses.
- All chunks are large but self‑contained, and can be implemented one at a time with TDD.
