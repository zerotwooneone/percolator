
# Desktop.Wpf Simulator Plan — Accept Pending Handshake + Relay Support

Goal: Wire `AcceptHandshakeCommand` (in `PendingHandshakesMenuViewModel`) so accepting a pending handshake actually **sends a handshake response back to the remote peer**, and extend the simulator + UI to support and visualize **relayed handshakes**.

Non-goals:

- Do not introduce no-op implementations, placeholder logic, or “temporary” in-memory repositories.
- Do not leave dead code behind. If something must be deleted, I will explicitly call it out for you to delete.

Key constraints / invariants (from `session-flow.md`):

- Reverse-signal flow: local user invites; remote user accepts and responds.
- Acceptance produces an `InviteHandshakeResponse` that must be delivered to the inviter.
- Relay support: the invite/response may traverse a relay/host; UX should show when a relay path was used.

Open protocol gap:

- `session-flow.md` does not currently specify how the inviter communicates a callback `DnsEndpoint` for the response.
- Relay-ness must be derived from the context in which the invite arrives (transport path), not encoded into any handshake message fields.

Code recon findings (current state):

- There is already an application-level accept path: `ReverseSignalAcceptService.AcceptAsync(PendingSessionId)` and the MediatR entry point `ApprovePendingSessionCommand`.
- However, the current accept implementation does not follow the Reverse-Signal wire model in `session-flow.md` (it uses `HandshakeInitiatorHello` and a pre-encrypted responder message instead of `InviteHandshakeRequest`/`InviteHandshakeResponse`).
- Outbound sending already supports direct-first with relay fallback and exposes which path was used.

---

## Chunk 1 — Update the Reverse-Signal protocol to include inviter callback endpoint (docs + contracts)

Intent: Bring the documented protocol up to the minimum required to route the response back to the inviter, without adding relay-identifying fields to any messages.

Deliverables:

- Update `session-flow.md` Part 2 wire model:
  - Extend `InviteHandshakeRequest` to include a callback `DnsEndpoint` for the inviter.
  - Define how this endpoint is validated/sanitized and stored.
- Update the contracts/protos to match the doc.

Required tests:

- Contract-level test (or serialization test): `InviteHandshakeRequest` round-trips with the callback endpoint.

---

## Chunk 2 — Application orchestration: Accept Pending Handshake → send `InviteHandshakeResponse`

Intent: WPF should call a single application entry point to accept a pending handshake, rather than manipulating pending-session state directly.

Deliverables:

- Treat `ApprovePendingSessionCommand` (MediatR) as the primary application entry point.
- Change acceptance to follow `session-flow.md` Part 2:
  - Pending item stores an `InviteHandshakeRequest` blob.
  - Accept builds an `InviteHandshakeResponse` (echoing `local_invitation_id`, including the documented fields).
  - Deliver that response back to the inviter using the callback endpoint from Chunk 1.
- Add a typed result model for acceptance (instead of `bool`) so UI/simulator can distinguish:
  - `Accepted` (includes the send path used: direct vs relay)
  - `RejectedNotReady`
  - `RejectedInvalid`
  - `RejectedExpired`
  - `Failed`
- Add readiness gating: if no active identity, return `RejectedNotReady` and do not send.

Required tests:

- Unit test: not-ready yields `RejectedNotReady` and does not call `IMessageService`.
- Unit test: success returns `Accepted` and includes the send path.

---

## Chunk 3 — Wire `PendingHandshakesMenuViewModel.AcceptHandshakeCommand` to Application (Option 1)

Intent: accepting from the UI should perform a real accept-and-send using Application logic.

Deliverables:

- Update `PendingHandshakesMenuViewModel.AcceptHandshakeCommand` to call `IMediator.Send(new ApprovePendingSessionCommand(...))` (Option 1), not `IPendingSessionRepository`.
- Update the UI item model (`PendingHandshakeItem`) to display:
  - accepted/rejected status
  - whether the send went direct or relay (`SendResult.Path`)
- Ensure pending list refresh:
  - remove on accepted
  - keep (and show error) on failures

Required tests:

- ViewModel test: accept calls the app entry point and removes item on `Accepted`.

---

## Chunk 4 — Make outbound sends observable (for simulator + diagnostics)

Intent: The simulator needs a first-class way to observe outbound handshake messages so it can catch acceptance responses and advance its local state machine.

Deliverables:

- Introduce an application-layer outbound “telemetry” port (or event stream) for send attempts/outcomes.
  - Example shape: `INetworkSendObserver` (or `IMessageSendObserver`) with `OnSendAttempted(...)` / `OnSendSucceeded(...)` / `OnSendFailed(...)`.
  - Event payload should include:
    - destination peer id
    - `SendStrategy` and chosen `Path`
    - `AttemptedPaths` and attempt count
    - the minimal discriminator needed by the simulator to correlate events to a simulated session (use only fields already present in the documented messages, e.g., invitation id when available)
- Implement by instrumenting at the narrowest choke point:
  - Preferred: inside `INetworkSender` implementation (covers all sends)
  - Fallback: inside `MessageService` methods (`SendPreEncryptedAsync`, `SendMessageAsync`, etc.)
- Provide a Desktop.Wpf implementation that is safe for UI consumption and can be subscribed to by the simulator (channels/observables/event aggregator).

Required tests:

- Unit test: when acceptance triggers an outbound send, the observer is notified exactly once with the expected metadata.

---

## Chunk 5 — Relay UX without protocol changes (derive relay details locally)

Intent: support relayed handshakes as first-class, with UI-visible metadata.

Deliverables:

- Persist a boolean/enum indicator that the pending handshake arrived via a relay/host path.
- Persist the inviter identity key (as described by `session-flow.md`) for UI/verification.
- Ensure the application can resolve relay peer display name and `DnsEndpoint` via local state (routing profile / peer store), not via handshake message fields.
- Update pending-handshake creation path(s) to supply relay metadata when applicable:
  - simulator injection should be able to set this
  - future real relay ingress should set it
- Update `PendingHandshakeItem` (WPF-facing DTO/view model item) to include:
  - `IsRelayed`
  - inviter identity key (or a UI-safe derived representation suitable for verification)
  - relay peer display name and relay `DnsEndpoint` resolved locally when `IsRelayed` is true

Required tests:

- Unit test: creating a pending handshake with relay metadata persists it and it rehydrates in queries.

---

## Chunk 6 — Simulator: one-off in-memory session list + minimal state machine + relay simulation

Intent: the simulator can model realistic flows, including relay selection, and can observe outbound acceptance sends.

Deliverables:

- Use one-off in-memory simulator state (dev-only) to track simulated sessions; do not introduce separate DI containers.
- Extend `HandshakeSimulatorViewModel` to maintain a list of “simulated sessions” with a minimal state machine:
  - `PendingHandshake_Direct`
  - `PendingHandshake_Relayed`
  - (future) `Established`
- Add UI controls to simulate:
  - creating a pending handshake directly
  - creating a pending handshake "via relay" (sets relay metadata stored with the pending)
- Subscribe to the outbound send observer (Chunk 4) and, when a handshake-accept send is observed:
  - match it to a pending simulated session using only documented identifiers (e.g., invitation id)
  - advance state (e.g., mark as `Established` or "AcceptedSent")

Required tests:

- ViewModel test: creating a simulated relayed handshake produces entries with relay metadata.
- ViewModel test: outbound observer event advances the correct simulated session.

---

## Chunk 7 — End-to-end: accept from UI sends `InviteHandshakeResponse` (direct or relay) and simulator observes it

Intent: demonstrate the full reverse-signal accept loop with observability and relay UX.

Deliverables:

- Use `PendingHandshakesMenuViewModel.AcceptHandshakeCommand` to accept.
- Ensure acceptance returns a typed result (Chunk 2) and is shown in UI.
- Ensure send outcome shows whether relay was used (`SendResult.Path`).
- Ensure simulator observes outbound send events (Chunk 4).

Required tests:

- Integration-style test at app layer: accept -> message send -> observer notified.

---

Exit criteria:

- Accepting a pending handshake produces an outbound send action for `InviteHandshakeResponse` and reports whether it went direct or relay.
- Pending handshake UI shows relay info (mouse-over bubble) when applicable.
- Simulator can create both direct and relayed pending handshakes, accept them, and observe the outbound send.

---

## Potential deletions (you delete; do not leave dead code)

- Delete the current WPF-side TODO path in `PendingHandshakesMenuViewModel` that reads `IPendingSessionRepository` directly for accept.
- If any simulator-only persistence helpers exist that bypass Application ports, delete them.

