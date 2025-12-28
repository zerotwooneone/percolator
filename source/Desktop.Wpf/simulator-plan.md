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

Domain direction note (new):

- One-time pre-keys (OTKs) are modeled as an explicit state machine behind a repository interface.
- Prefer generating OTKs at the time they are needed, but allow persistence for:
  - published OTKs (standard signal flow), and
  - reserved OTKs awaiting reverse-signal acceptance.
- Avoid secret sprawl: invitation records should store correlation and identifiers, not raw private key bytes, unless a dedicated key store is not present.

---

## Chunk 1 — Protocol cutover foundations: reverse-signal pending-handshake model + validation + query surface (COMPLETE)

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

## Chunk 2 — Reverse-signal wire protocol implementation: contracts + ingress mapping (remote peer → local pending) (COMPLETE)

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

## Chunk 3 — Domain: OTK lifecycle model + reservation semantics (NEW)

Intent: Make OTK handling correct and secure before further protocol/UI work.

Deliverables:

- Define an explicit OTK state machine (DDD): a one-time pre-key can only be in a small number of states.
- Repository interface hides implementation details (generate-on-demand vs pre-generated pools).

OTK states (explicit):

- `Available`: exists locally, not published, not reserved.
- `Published`: public portion has been published for the standard Signal flow.
- `Reserved`: bound to a single `request_correlation_id` until an `expires_at_utc`.
- `Consumed`: private material has been used to complete X3DH; cannot be reused.
- `Expired`: reservation/publication window elapsed; key material must be unrecoverable.

Persistence note (initial):

- For now, only `Reserved` and `Published` must be persisted in the database.
- `Available` generation is on-demand.
- `Consumed` and `Expired` can be represented as deletions/tombstones as long as invariants remain testable.

Required capabilities:

- Published OTKs:
  - OTKs that have been published for the standard Signal flow.
  - Private material exists only as long as required by policy and is protected at rest.

- Reserved OTKs awaiting acceptance:
  - When creating a reverse-signal invitation that includes an OTK public key, reserve an OTK by `request_correlation_id` until invite expiry.
  - Reservation must prevent reuse for another invite/handshake.
  - On expiry or explicit burn, the OTK must transition to a terminal state and be unrecoverable.

Invariants:

- An OTK private key must be consumable at most once.
- An OTK cannot be used concurrently for multiple invitations.
- Expiration/purge is mandatory and must be testable.

Required tests:

- Reserving an OTK by `request_correlation_id` prevents re-reservation.
- Consuming a reserved OTK succeeds once and fails thereafter.
- Expired reservations are purged and cannot be consumed.

---

## Chunk 4 — Domain: SentInvitations + inviter-side finalization prerequisites (NEW)

Intent: Treat inviter-side invitation tracking as a first-class domain concern.

Deliverables:

- Persist `SentInvitations` for reverse-signal invites (inviter side) keyed by `request_correlation_id`.
- `SentInvitations` stores only the minimal data required to finalize later:
  - `request_correlation_id`
  - `signed_pre_key_id`
  - optional `one_time_pre_key_id`
  - `created_at`, `expires_at_utc`
  - target peer identity reference (if available)

- Add explicit purge rules:
  - expired invitations are deleted;
  - deletion of an invitation triggers release/expiry of any reserved OTK (if still reserved).

Invariants:

- `request_correlation_id` must be unique until expiry.
- If an OTK is used for an invite, it must be reserved for that invite until acceptance/expiry.

Required tests:

- `SentInvitations` upsert/lookup by `request_correlation_id` round-trips correctly.
- Finalization lookup fails safely when correlation is missing/expired.

- Purge test: expired `SentInvitations` are removed and reserved OTKs are not left in a reservable-but-leaked state.

---

## Chunk 5 — Protocol cutover: pre-key IDs stay inviter-local + contract/doc alignment (NEW)

Intent: Make the privacy/security tradeoff explicit and reflected in the contracts.

Deliverables:

- Decision (locked): pre-key IDs stay inviter-local.

- Update protocol/contracts so `InviteHandshakePreKeyBundle` does NOT include:
  - `inviter_signed_pre_key_id`
  - `inviter_one_time_pre_key_id`
  (the invite still may include the OTK public key bytes when used).

- Update inviter-side domain logic so `SentInvitations` is the sole source of truth for:
  - which signed pre-key id was used
  - which one-time pre-key id was reserved/used (if any)

- Update `session-flow.md` Part 2 to match this design (IDs not transmitted).

- Update protobuf contracts and all associated parsing/validation:
  - ingress must not require ID fields inside the invite payload;
  - approval/orchestration must not assume IDs are present in received payload;
  - inviter-side finalization must rely on `SentInvitations` lookups.

Required tests:

- Contract-level tests that ID fields are absent (and no longer required) in invite payloads.
- Migration/compat note: old records may fail fast and should be purged by developers.

---

## Chunk 6 — Approval orchestration (application): approve pending handshake → upsert routing profile → send `InviteHandshakeResponse` (DOMAIN-FIRST)

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
  - Perform X3DH initiator work for the local user (Bob) using the inviter-provided pre-key material (public keys + signatures).
  - Do not assume any pre-key IDs are present in the invite payload (IDs stay inviter-local).
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

## Chunk 7 — Dev-mode outbound message tap: make outgoing network messages observable to the simulator

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

## Chunk 8 — WPF acceptance UX: approve pending handshake via application orchestration and show results

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

## Chunk 9 — Relay UX + local resolution: show relay path as a transport property (no protocol changes)

Intent: Keep relay strictly as a transport-path indicator while making it visible in UX and simulator.

Deliverables:

- Ensure the app can resolve relay peer display name and endpoint via local state (`PeerRoutingProfile` / peer store), not via handshake fields.
- Update pending-handshake list/query DTO to include relay metadata.
- Add UI fields and mouse-over details for relay info.

Required tests:

- Query/DTO test: relay metadata rehydrates and display name resolution behaves as expected.

---

## Chunk 10 — Simulator: multi-peer state machine + inbound request generation + outbound correlation (dev-mode)

Intent: Dev-only simulator that can simulate any number of peers, generate inbound reverse-signal invites, approve them locally, and observe outbound responses.

Deliverables:

- Maintain an in-memory simulator peer list:
  - Each peer has its own identity keys and pre-keys.
  - Simulator can add/remove peers dynamically.

- Inbound request generation:
  - Simulate a remote peer creating a valid reverse-signal invite.
  - Deliver the request to the local node using the real ingress path (preferred) or a dev-only injection port (fallback).

- Outbound correlation:
  - Subscribe to the dev-mode outbound message tap.
  - Correlate outbound `InviteHandshakeResponse` by `request_correlation_id`.
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

Delete when Chunk 6 + Chunk 8 are complete:

- Delete any remaining reverse-signal acceptance code paths that depend on `HandshakeInitiatorHello` or “pre-encrypted responder hello”.
- Delete the WPF-side TODO accept implementation in `PendingHandshakesMenuViewModel` that reads `IPendingSessionRepository` directly.

Delete when Chunk 2 is complete:

- Delete any legacy/compat request types or branches in `EstablishDirectSessionRequest` that are not `InviteHandshakeRequest`.

