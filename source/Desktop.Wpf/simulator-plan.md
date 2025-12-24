
# Desktop.Wpf Simulator Plan — Accept Pending Handshake + Relay Support

Goal: Wire `AcceptHandshakeCommand` (in `PendingHandshakesMenuViewModel`) so accepting a pending handshake actually **sends a handshake response back to the remote peer**, and extend the simulator + UI to support and visualize **relayed handshakes**.

Non-goals:

- Do not introduce no-op implementations, placeholder logic, or “temporary” in-memory repositories.
- Do not leave dead code behind. If something must be deleted, I will explicitly call it out for you to delete.

Key constraints / invariants (from `session-flow.md`):

- Reverse-signal flow: local user invites; remote user accepts and responds.
- Acceptance produces an `InviteHandshakeResponse` that must be delivered to the inviter.
- Relay support: the invite/response may traverse a relay/host; UX should show when a relay path was used.
- Callback endpoint privacy/integrity: any inviter callback endpoint must be carried only inside the signed payload and must not be derived from transport metadata.

Open protocol gap:

- Relay-ness must be derived from the context in which the invite arrives (transport path), not encoded into any handshake message fields.
- Security requirement: on receiving an invite, do not upsert direct endpoints into `PeerRoutingProfile` (avoid routing-profile poisoning / forced-dial). Only upsert routing profile on explicit user acceptance.

Code recon findings (current state):

- There is already an application-level accept path: `ReverseSignalAcceptService.AcceptAsync(PendingSessionId)` and the MediatR entry point `ApprovePendingSessionCommand`.
- However, the current accept implementation does not follow the Reverse-Signal wire model in `session-flow.md` (it uses `HandshakeInitiatorHello` and a pre-encrypted responder message instead of `InviteHandshakeRequest`/`InviteHandshakeResponse`).
- Outbound sending already supports direct-first with relay fallback and exposes which path was used.

---

## Chunk 1 — Domain + Persistence: pending handshake metadata (callback endpoint, relay) + validation policy

Intent: Establish the DDD/TDD foundation so later protocol and orchestration work is mechanical and safe.

Deliverables:

- Extend the persisted pending-handshake record (crypto-domain `PendingSession` persisted via `IPendingSessionRepository`) to support additional metadata required by reverse-signal:
  - callback endpoint (host + port) stored only on the pending record and only when the handshake is direct
  - a boolean/enum indicating the pending handshake arrived via relay
  - inviter identity key material suitable for UI verification (store minimal necessary; avoid logging)
- Introduce an application-layer endpoint validation policy:
  - parse host as either DNS hostname or IP address
  - validate port is in range
  - add an `allow_LAN` configuration flag:
    - when `allow_LAN` is false, reject loopback, link-local, and private-range IP targets
    - when `allow_LAN` is true, allow those targets (still apply port validation and size limits)
- Explicit invariant: do not upsert direct endpoints into `PeerRoutingProfile` on invite receipt.

Required tests:

- Persistence round-trip tests for the new pending metadata.
- Validation tests for host/port parsing and `allow_LAN` behavior.
- Invariant test: relayed pending handshakes cannot have a stored callback endpoint.

---

## Chunk 2 — Wire Mapping + Contracts: reverse-signal request/response representation

Intent: Make the wire model explicit and align contracts with the domain without compromising privacy.

Deliverables:

- Add an explicit mapping section in `session-flow.md` Part 2 describing:
  - the protocol concepts (`InviteHandshakeRequest`, `InviteHandshakeResponse`)
  - the concrete carrier types used by this codebase 
    - EstablishDirectSessionRequest carries InviteHandshakeRequest (outer wrapper + signed payload)
  - EstablishDirectSessionRequest carries InviteHandshakeRequest (outer wrapper + signed payload)
- Update protobuf contracts to represent the reverse-signal request/response in a way compatible with the existing crypto flow.


---

## Chunk 3 — Application orchestration: accept pending handshake → upsert routing profile → send response

Intent: Implement the actual reverse-signal acceptance use case behind a single application entry point.

Deliverables:

- Treat `ApprovePendingSessionCommand` (MediatR) as the primary entry point.
- Implement a typed result model for acceptance (instead of `bool`) so UI/simulator can distinguish:
  - `Accepted` (includes send path used: direct vs relay)
  - `RejectedNotReady`
  - `RejectedInvalid`
  - `RejectedExpired`
  - `Failed`
- Add readiness gating: if no active identity, return `RejectedNotReady` and do not send.
- Enforce routing/profile mutation boundary:
  - For direct invites: read callback endpoint from the pending record, re-validate under current `allow_LAN` policy, then upsert as a `GrpcEndPoint` in inviter `PeerRoutingProfile`, then send.
  - For relayed invites: ignore callback endpoint entirely; ensure a `RelayLink` based on transport context; send using normal routing.

Required tests:

- Unit test: not-ready yields `RejectedNotReady` and does not call `IMessageService`.
- Unit test: direct accept upserts endpoint only after validation and sends.
- Unit test: relayed accept does not read/use callback endpoint and uses relay routing.

---

## Chunk 4 — Outbound observability: make sends observable for simulator + diagnostics

Intent: Provide a first-class, app-layer signal for outbound sends so the simulator can correlate and advance state.

Deliverables:

- Introduce an outbound send observer port/event stream.
- Instrument at a choke point:
  - preferred: inside `INetworkSender`
  - fallback: inside `MessageService`
- Observer gating:
  - The observer is enabled only when a debug/simulator config flag is on; it is off by default.
  - When enabled, the observer emits the full outbound message payload so the simulator can emulate other peers.
    - The payload should be provided as raw bytes (or a strongly-typed envelope) without logging.
    - The observer implementation must not assume payloads are safe to render in UI.
  - When disabled, no observer events are emitted.

Required tests:

- Unit test: when the observer is enabled, acceptance triggers exactly one observer notification and includes the outbound payload.
- Unit test: when the observer is disabled, acceptance triggers no observer notifications.

---

## Chunk 5 — WPF UI wiring: `AcceptHandshakeCommand` uses application orchestration + displays results

Intent: Make the UI drive the real accept-and-send use case.

Deliverables:

- Update `PendingHandshakesMenuViewModel.AcceptHandshakeCommand` to call `IMediator.Send(new ApprovePendingSessionCommand(...))`.
- Update the WPF-facing item model to show:
  - accepted/rejected status
  - send path used (`SendResult.Path`)
  - (when available) relay indicator

Required tests:

- ViewModel test: accept calls the app entry point and removes item on `Accepted`.

---

## Chunk 6 — Relay UX + local resolution: show relay details without protocol changes

Intent: Keep relay strictly as a transport-path indicator while making it visible in UX.

Deliverables:

- Ensure the app can resolve relay peer display name and endpoint via local state (`PeerRoutingProfile` / peer store), not via handshake fields.
- Update pending-handshake list/query DTO to include relay metadata.
- Add UI fields and mouse-over details for relay info.

Required tests:

- Query/DTO test: relay metadata rehydrates and display name resolution behaves as expected.

---

## Chunk 7 — Simulator: one-off in-memory session list + relay simulation + outbound correlation

Intent: Dev-only simulator that can generate direct/relayed pending handshakes and observe acceptance sends.

Deliverables:

- Maintain a minimal in-memory simulator session list/state machine.
- Controls to create direct vs relayed pending handshakes.
- Subscribe to outbound send observer and advance simulator state.

Required tests:

- ViewModel test: creating a simulated relayed handshake produces entries with relay metadata.
- ViewModel test: outbound observer event advances the correct simulated session.

---

Exit criteria:

- Accepting a pending handshake produces an outbound send action for `InviteHandshakeResponse` and reports whether it went direct or relay.
- Pending handshake UI shows relay info (mouse-over bubble) when applicable.
- Simulator can create both direct and relayed pending handshakes, accept them, and observe the outbound send.

---

## Potential deletions (you delete; do not leave dead code)

- Delete the current WPF-side TODO path in `PendingHandshakesMenuViewModel` that reads `IPendingSessionRepository` directly for accept.
- If any simulator-only persistence helpers exist that bypass Application ports, delete them.

