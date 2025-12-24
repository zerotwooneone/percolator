# Desktop.Wpf Simulator Plan — Reverse-Signal Handshake Simulation + Local Approval

Goal: Make the WPF application capable of:

- Simulating any number of remote peers sending **reverse-signal** handshake requests into the local node.
- Displaying those requests as **pending handshakes** requiring explicit user approval (TOFU).
- Approving a pending handshake which then **sends an `InviteHandshakeResponse`** back to the inviter.
- Observing outbound network messages (for simulator correlation) **only when a development flag is enabled**.

Non-goals:

- Do not introduce placeholder logic or “temporary” in-memory repositories in production paths.
- Do not leave dead code behind.

Authoritative protocol model:

- The correct reverse-signal model is `session-flow.md` Part 2.
- Canonical correlation key: `request_correlation_id`.

Key constraints / invariants:

- Reverse-signal flow: remote peer sends `InviteHandshakeRequest`; local user approves; local user sends `InviteHandshakeResponse`.
- Callback endpoint integrity: inviter callback endpoint MUST be carried only inside the **signed payload** and MUST NOT be derived from transport metadata.
- Replay/DoS control: `request_correlation_id` must be unpredictable; receiver stores seen IDs until expiry and rejects duplicates.
- Routing-profile poisoning mitigation: on receiving an invite, do not upsert direct endpoints into `PeerRoutingProfile`. Only upsert routing profile on explicit user acceptance.
- Relay-ness is a transport-path property and must not be encoded in the handshake message fields.

Unit testing standards:

- All unit tests must follow `unit-testing.md`.
- Tests must be behavior-focused (AAA pattern, black-box), avoiding brittle internal interaction assertions.

Compilation constraint:

- Chunks do not need to compile between chunks. Each chunk is intentionally large.

---

## Chunk 1 — Protocol cutover foundations: reverse-signal pending-handshake model + validation + query surface

Intent: Establish the domain/application foundations required by `session-flow.md` reverse-signal:

- Persist and query pending handshakes with the metadata needed for user verification and later acceptance.
- Validate callback endpoints under an application policy.
- Define and enforce the routing-profile mutation boundary.

Deliverables:

- Extend the persisted pending-handshake record (crypto-domain `PendingSession`) to support reverse-signal requirements:
  - **`request_correlation_id`** (string) for replay protection and correlation.
  - **inviter identity key (SPKI)** (bytes) stored for TOFU UI display (fingerprint); keep minimal; never log.
  - **invitation blob** stored as the full serialized `InviteHandshakeRequest` (outer wrapper).
  - **expiry**: surface `expires_at_utc` so expiry can be enforced on receipt and acceptance.
  - **relay indicator** (bool/enum) indicating whether the invite arrived via relay.
  - **callback endpoint** (host + port) stored only when the invite is direct; MUST be absent for relayed invites.

- Introduce an application-layer endpoint validation policy:
  - parse host as either DNS hostname or IP address.
  - validate port range.
  - `allow_LAN` flag controlling loopback/link-local/private ranges.

- use IPendingHandshakeQueries:
  - must return `PendingSessionId`, inviter display name (or best-effort placeholder), and TOFU identity fingerprint material.
  - must include `request_correlation_id` and expiry for UI display and debugging.

- Explicit invariant: do not upsert direct endpoints into `PeerRoutingProfile` on invite receipt.

Required tests (must follow `unit-testing.md`):

- Persistence round-trip tests for pending metadata.
- Validation tests for host/port parsing and `allow_LAN` behavior.
- Invariant test: relayed pending handshakes cannot have a stored callback endpoint.
- Expiry behavior test: expired pending handshakes are not returned by `EnumerateOpenAsync`.

---

## Chunk 2 — Reverse-signal wire protocol implementation: contracts + ingress mapping (remote peer → local pending)

Intent: Make the wire model explicit and implement the *ingress* side of reverse-signal:

- A simulated (or real) remote peer can send `InviteHandshakeRequest` to the local node.
- The local node verifies and enqueues a pending handshake consistent with `session-flow.md`.

This chunk is where `EstablishDirectSessionRequest` becomes the reverse-signal carrier.

Deliverables:

- Update protobuf contracts (no legacy compatibility):
  - `EstablishDirectSessionRequest` has a single field that carries the reverse-signal invite, e.g. `bytes invite_handshake_request = ...` containing the serialized `InviteHandshakeRequest` (outer wrapper + signed payload). **No `oneof`. No legacy fields.**
  - Remove any legacy `PreKeyBundle` or `HandshakeInitiatorHello` paths from this RPC.
  - `EstablishDirectSessionResponse` acknowledges invite receipt as **queued for approval** (not a crypto handshake response), and must be sufficient for the sender to correlate via `request_correlation_id`.

- Implement ingress mapping (server-side):
  - Parse incoming `InviteHandshakeRequest`.
  - Verify `payload_signature` over raw `payload` bytes using `alice_identity_key`.
  - Parse `payload` bytes to `InviteHandshakeRequestPayload` only after signature verification.
  - Verify `pre_key_signature` using the same `alice_identity_key` over `alice_signed_pre_key`.
  - Validate `alice_host`/`alice_port` under policy.
  - Enforce expiry and replay protection (`request_correlation_id` dedup).
  - Enqueue a `PendingSession` with:
    - `InvitationBlob` stored as the full serialized `InviteHandshakeRequest` (outer wrapper), not only the inner `payload`.
    - `InviterIdentityKey` stored for TOFU UI display.
    - `request_correlation_id` and `expires_at_utc` persisted for dedup + expiry enforcement.

  - WPF notification bridge (required for UI refresh):
    - When an invite is successfully queued, publish the exact notification type that WPF listens for: `PendingHandshakeAdded`.
    - If the application layer already emits a different pending-session notification, add a bridging handler that translates it into `PendingHandshakeAdded`.

- Document (in this plan and/or in `session-flow.md`) the relay handling rule:
  - direct vs relayed is determined by the transport path used to deliver the invite; it is **not** a handshake field.
  - For relayed invites: persist `IsRelayed = true` and do not store any callback endpoint from the invite (even if present).
  - For direct invites: persist `IsRelayed = false` and store callback endpoint from the signed payload after validation.

Required tests (must follow `unit-testing.md`):

- Valid invite is queued and produces a pending handshake.
- Valid invite queues pending handshake and publishes `PendingHandshakeAdded`.
- Invalid signature rejects without side effects.
- Duplicate `request_correlation_id` rejects without side effects.
- Expired invite rejects without side effects.

---

## Chunk 3 — Approval orchestration (application): approve pending handshake → upsert routing profile → send `InviteHandshakeResponse`

Intent: Implement the acceptance behavior described in `session-flow.md` and make it the only supported acceptance path.

Deliverables:

- Treat `ApprovePendingSessionCommand` (MediatR) as the primary entry point.

- Replace `bool` with a typed result model so WPF and simulator can observe behavior:
  - `Accepted` (includes send path used: direct vs relay, and echoed `request_correlation_id`)
  - `RejectedNotReady`
  - `RejectedInvalid`
  - `RejectedExpired`
  - `Failed`

- Acceptance implementation:
  - Load pending by id.
  - Enforce expiry.
  - Parse stored invitation blob as `InviteHandshakeRequest`.
  - Perform X3DH initiator work for the local user (Bob) using Alice’s provided pre-key bundle.
  - Construct `InviteHandshakeResponse { request_correlation_id, bob_identity_key, bob_x3dh_ephemeral_key, initial_ratchet_message }`.
  - Enforce routing/profile mutation boundary:
    - direct invites: re-validate callback endpoint and then upsert endpoint to inviter `PeerRoutingProfile` before sending.
    - relayed invites: do not use callback endpoint; use relay routing.
  - Send response to inviter.
  - Only on successful send: delete pending record.

Deletion point (required):

- Once the new acceptance path is implemented and covered by tests, **DELETE** the non-compliant acceptance flow:
  - `Percolator.Application.ReverseSignal.ReverseSignalAcceptService.AcceptAsync(...)` logic that parses `HandshakeInitiatorHello` and calls `HandleHandshakeInitiatorHelloCommand`.
  - Any usage of `SendPreEncryptedAsync` for reverse-signal acceptance.
  - Any remaining protocol mapping that treats reverse-signal acceptance as “responder hello pre-encrypted”.

Required tests:

- Unit test: not-ready yields `RejectedNotReady` and does not send.
- Unit test: direct accept upserts endpoint only after validation and sends response.
- Unit test: relayed accept does not read/use callback endpoint.
- Unit test: successful accept deletes pending.
- Unit test: send failure keeps pending.

---

## Chunk 4 — Dev-mode outbound message tap: make outgoing network messages observable to the simulator

Intent: Allow the simulator to observe outbound messages so it can emulate other peers and validate protocol behavior, while guaranteeing that production builds do not expose sensitive payloads.

Deliverables:

- Define a single configuration switch for the entire feature, e.g. `SimulatorWireTapOptions.Enabled` (default `false`).
- The wire tap must be enabled only when the development/simulator flag is on, and must be disabled by default.

- Introduce a dev-only outbound wire tap port/event stream (application-level), exposed as an interface such as `IOutboundMessageWireTap`.

- Instrument exactly once per outbound message at the point where:
  - the destination `PeerId` is known,
  - the chosen route (`SendPath`: direct vs relay) is known,
  - and the final outbound payload bytes are available.
  Do not emit multiple wire-tap events for internal retries unless you include an explicit attempt counter.

- Wire-tap event contract:
  - When enabled, emit an `OutboundWireMessage` containing:
    - destination peer id
    - transport path used (direct vs relay)
    - optional `request_correlation_id` (set only when the payload is an `InviteHandshakeResponse` and can be extracted without decrypting anything else)
    - message type label (e.g., `InviteHandshakeResponse`, `EncryptedEnvelope`)
    - raw bytes payload (no logging)
    - payload length

- Security/UX constraints:
  - The wire tap must not log payload bytes.
  - The simulator UI must not assume payload bytes are safe to render.
  - UI should display metadata only (type, correlation id when present, byte length, destination, path).

- Simulator usage requirement:
  - The simulator must be able to subscribe and treat outbound events as “wire traffic” to other simulated peers.

Required tests:

- Unit test: wire tap disabled emits nothing.
- Unit test: wire tap enabled emits an event containing the expected payload bytes and destination peer id.

---

## Chunk 5 — WPF acceptance UX: approve pending handshake via application orchestration and show results

Intent: Ensure the WPF UI does not bypass application orchestration and that acceptance behavior is testable.

Deliverables:

- Update `PendingHandshakesMenuViewModel.AcceptHandshakeCommand` to call `IMediator.Send(new ApprovePendingSessionCommand(...))`.

- Update the WPF-facing item model to show:
  - accepted/rejected status
  - send path used (direct vs relay)
  - `request_correlation_id`
  - expiry status

Deletion point (required):

- After the new orchestration call is wired, **DELETE** the WPF-side acceptance TODO path that reads `IPendingSessionRepository` directly and locally removes items without sending.

Required tests:

- ViewModel test: approve sends command and updates item state based on typed result.

---

## Chunk 6 — Relay UX + local resolution: show relay path as a transport property (no protocol changes)

Intent: Keep relay strictly as a transport-path indicator while making it visible in UX and simulator.

Deliverables:

- Ensure the app can resolve relay peer display name and endpoint via local state (`PeerRoutingProfile` / peer store), not via handshake fields.
- Update pending-handshake list/query DTO to include relay metadata.
- Add UI fields and mouse-over details for relay info.

Required tests:

- Query/DTO test: relay metadata rehydrates and display name resolution behaves as expected.

---

## Chunk 7 — Simulator: multi-peer state machine + inbound request generation + outbound correlation (dev-mode)

Intent: Dev-only simulator that can simulate any number of peers, generate inbound reverse-signal invites, approve them locally, and observe outbound responses.

Deliverables:

- Maintain an in-memory simulator peer list:
  - Each peer has its own identity keys and pre-keys.
  - Simulator can add/remove peers dynamically.

- Inbound request generation:
  - Simulate a remote peer creating a valid `InviteHandshakeRequest` (including:
    - `alice_identity_key`
    - signed `payload`
    - `request_correlation_id`
    - expiry
    - endpoint)
  - Deliver the request to the local node using the real ingress path (preferred) or a dev-only injection port (fallback).

- Outbound correlation:
  - Subscribe to the dev-mode outbound message tap.
  - Route outbound `InviteHandshakeResponse` to the matching simulated peer by `request_correlation_id`.
  - Update simulator state and display “handshake completed” vs failures.

Required tests:

- ViewModel test: creating N simulated peers can enqueue N pending handshakes.
- ViewModel test: outbound observer event is correlated by `request_correlation_id` and advances the correct simulated peer state.

---

Exit criteria:

- Accepting a pending handshake produces an outbound send action for `InviteHandshakeResponse` and reports whether it went direct or relay.
- Pending handshake UI shows relay info (mouse-over bubble) when applicable.
- Simulator can create both direct and relayed pending handshakes, accept them, and observe the outbound send.

---

## Potential deletions (you delete; do not leave dead code)

Delete when Chunk 3 + Chunk 5 are complete:

- Delete `ReverseSignalAcceptService.AcceptAsync(...)` handshake-initiator-hello parsing + `SendPreEncryptedAsync` flow.
- Delete any remaining reverse-signal acceptance code paths that depend on `HandshakeInitiatorHello` or “pre-encrypted responder hello”.
- Delete the WPF-side TODO accept implementation in `PendingHandshakesMenuViewModel` that reads `IPendingSessionRepository` directly.

Delete when Chunk 2 is complete:

- Delete any legacy/compat request types or branches in `EstablishDirectSessionRequest` that are not `InviteHandshakeRequest`.

