# Simulator Plan: Reverse‑Signal Pending Handshake Injector (Critically Reviewed)

## Objectives
- Provide a high‑quality, testable path to inject synthetic Reverse‑Signal pending handshakes for local/dev.
- Extend the correct layers with clean interfaces and contracts that can remain in production code (not throwaway helpers).
- Keep strict boundaries: Desktop.Wpf (UI) → Chat (application) → Crypto (domain abstractions) → Infrastructure (EF/SQLite).

## Reference materials
- session-flow.md → Part 2: Reverse‑Signal Flow (Alice invites Bob to initiate)
- Crypto infra: `SqlitePendingSessionRepository` (implements `IPendingSessionRepository`).

---

## Design principles (what we will and won’t do)
- Use application-layer commands/notifications (MediatR) in Percolator.Chat as the single public API for the desktop app to write synthetic pending handshakes.
- Depend on domain interfaces only from the application layer (no UI → domain infra shortcuts).
- Introduce stable interfaces where missing, rather than ad‑hoc helpers, so future non-simulated flows can reuse them.
- UI receives updates via notifications; never polls.

---

## New/extended contracts

### Update (direction change)
- We are using a Desktop service instead of an application-layer command handler to insert synthetic pending handshakes.
- Replacement:
  - PendingHandshakeSimulatorService (Desktop.Wpf) replaces the planned AddSyntheticPendingHandshakeCommand handler.
  - PendingHandshakeAdded notification (Percolator.Chat) remains the event surface consumed by the UI.

### Percolator.Cryptography (domain abstractions)
- Keep `IPendingSessionRepository` as the persistence abstraction.
- `IHandshakeInvitationFactory` provides domain‑friendly creation of invitations for testing/dev tools:
  - `HandshakeInvitation CreateSynthetic(PeerId remotePeer, ProtocolVersion ver, byte[]? payload = null);`
  - Rationale: centralize how invitations are formed (even synthetic) and keep encoding rules out of the application/UI.
- Note: An interface named `IHandshakeInvitationFactory` currently lives under `Desktop.Wpf.Features.Simulator`. The long‑term destination remains the Cryptography/domain boundary, but the simulator can proceed with the current location.
- Optional: `IProtocolVersionProvider` to supply the current supported protocol version.

### Percolator.Chat (application layer)
- Notification: `PendingHandshakeAdded : INotification` (already exists)
  - Payload: `PendingSessionId Id`, `PeerId RemotePeerId`, `DateTimeOffset CreatedAtUtc`, optional `string? DisplayName`.
  - Raised by the Desktop simulator service after persistence to update the UI.

### Desktop.Wpf (UI)
- Service: `PendingHandshakeSimulatorService : IPendingHandshakeSimulatorService` (already exists)
  - Responsibilities: create synthetic invitation via `IHandshakeInvitationFactory`, create/persist `PendingSession`, publish `PendingHandshakeAdded`.
- Event listener: `PendingHandshakeEventListener : INotificationHandler<PendingHandshakeAdded>`
  - Marshals to UI thread via `IDispatcher` abstraction (wraps WPF `Dispatcher`).
  - Updates `SessionsSidebarViewModel.PendingMenu.PendingHandshakes` (maps basic fields; display name falls back to initials).
- Utility window: `HandshakeSimulatorWindow` (+ ViewModel)
  - Inputs: Remote Peer Id (string), optional display name, optional payload; button bound to a command that calls `IPendingHandshakeSimulatorService.AddSyntheticPendingAsync(...)`.
  - Lives under a dev tools menu or diagnostic entry point; no shipping blockers.

---

## Data and state rules
- Invitation bytes are opaque to UI; creation is centralized in `IHandshakeInvitationFactory`.
- Protocol version is provided by `IProtocolVersionProvider` (single source of truth).
- Pending sessions TTL are short (minutes). Cleanup logic already exists via repo enumeration of expired items.
- All DB writes execute under the active identity (via `ActiveIdentityContext`).

---

## Threading and delivery
- Command handler runs on a background thread.
- After persistence, the handler publishes `PendingHandshakeAdded`.
- Desktop listener receives on a MediatR background thread; it must dispatch to the WPF UI thread before mutating observable collections.

---

## DI and composition
- Percolator.Chat
  - Register MediatR for the assembly; ensure `PendingHandshakeAdded` is discoverable.
- Desktop.Wpf
  - Register `IPendingHandshakeSimulatorService` → `PendingHandshakeSimulatorService`.
  - Register `IHandshakeInvitationFactory` (current location in Desktop.Wpf; can move to Crypto later) and `IClock`.
  - Ensure the host scans Percolator.Chat for MediatR notifications.
  - Register `PendingHandshakeEventListener` and `IDispatcher`.
  - Wire the utility window VM factory into DI to resolve the simulator service.

---

## Testing strategy
- Chat command handler
  - Arrange: strict mocks for `IPendingSessionRepository`, `IHandshakeInvitationFactory`, `IProtocolVersionProvider`, `IClock`, and `IMediator` (verify Publish called).
  - Assert: repository `AddAsync` invoked with expected `PendingSession` (protocol, invitation, TTL), notification published once.
- Desktop listener
  - Use a fake dispatcher to capture posted actions; assert that collection updates are marshaled to UI.
- Integration happy‑path
  - Configure in‑memory EF DbContext for `SqlitePendingSessionRepository` and run handler end‑to‑end, then assert DB row and received notification.

---

## Deliverables checklist
- Chat
  - `PendingHandshakeAdded` (notification) — exists
- Crypto
  - `IHandshakeInvitationFactory` (default impl acceptable to live temporarily in Desktop.Wpf) — interface exists
  - `IProtocolVersionProvider` (optional)
- Desktop.Wpf
  - `PendingHandshakeSimulatorService` (service) — exists
  - `PendingHandshakeEventListener` (+ dispatcher)
  - `HandshakeSimulatorWindow` (+ VM, command to call simulator service)
  - Menu badge already binds to `PendingMenu.PendingHandshakes.Count`
- Tests
  - Simulator service unit tests (publishes notification, persists via repo)
  - Listener unit tests (dispatcher)
  - Optional integration (in‑memory DB)

---

## Open questions / future extensions
- Should Chat expose a read model (query) for pending handshakes to hydrate the menu on startup? If yes, add a `GetPendingHandshakesQuery` in Chat and map to UI DTOs.
- Add Accept/Burn flows: commands + notifications to keep the UI consistent with persistence.
- Telemetry hooks (structured logs) for simulator actions to ease demo scenarios.

## Next actions
- Scaffold the Chat command/notification and Crypto factories/providers.
- Add Desktop listener and utility window.
- Wire DI and add tests.

---

## TDD execution plan (large ordered chunks)

Chunk 1 — Simulator service + notification API (updated)
- Red:
  - Unit test: `PendingHandshakeSimulatorService` publishes `PendingHandshakeAdded` and calls `IPendingSessionRepository.AddAsync` with TTL and protocol.
- Green:
  - Implement service using `IHandshakeInvitationFactory`, `IClock`, `IPendingSessionRepository`, `IMediator`.
  - Use `ProtocolVersion(1)` or provider when available.
- Refactor:
  - Extract small helpers if duplication appears; ensure null/guard clauses covered.
- Acceptance:
  - Tests pass with strict mocks. No domain or UI leaks.

Chunk 2 — Crypto factories/providers
- Red:
  - Unit test: `IHandshakeInvitationFactory` default impl returns deterministic bytes for given inputs; `IProtocolVersionProvider` returns configured current version.
- Green:
  - Implement default factory/provider; wire via DI.
- Refactor:
  - Move constants to options if needed.
- Acceptance:
  - Tests pass; handler test from Chunk 1 can use real factory/provider.

Chunk 3 — Desktop event listener
- Red:
  - Unit test: on `PendingHandshakeAdded`, listener posts to dispatcher and adds an item to `PendingMenu.PendingHandshakes`.
- Green:
  - Implement `PendingHandshakeEventListener` with `IDispatcher` abstraction (fakeable).
- Refactor:
  - Ensure minimal coupling to view models (bridge service if necessary).
- Acceptance:
  - Test verifies dispatcher usage and collection updated.

Chunk 4 — Utility window + service wiring (updated)
- Red:
  - UI VM test: button command invokes `IPendingHandshakeSimulatorService.AddSyntheticPendingAsync` with parsed inputs.
- Green:
  - Implement `HandshakeSimulatorWindow` + VM; DI resolves the simulator service and binds the command.
- Refactor:
  - Polish bindings and validation for PeerId input.
- Acceptance:
  - Manual: clicking button inserts a pending session; dot increments; card lists item.

Chunk 5 — Integration path (in-memory DB)
- Red:
  - Integration test: send command → repository persists row → notification received by listener → collection updated.
- Green:
  - Wire test host with in-memory EF DbContext, real factory/provider, and mediator pipeline.
- Refactor:
  - Tighten lifetimes; ensure ActiveIdentityContext is seeded.
- Acceptance:
  - Test passes and logs show single publish and single UI update.
