## Chunk A

### Problem statement

When a handshake is **initiated by the simulator** (simulated peer invites Main) and the user clicks **Accept** in the Main window:

- Main creates the session and sends an `InviteHandshakeResponse` back to the simulator (via `ApprovePendingSessionCommand` -> `InviteHandshakeResponseDeliveryService`).
- That response is intercepted on the desktop side (`SimulatorOutboundInterceptor.TryDeliverInviteHandshakeResponse`).
- The interceptor currently forwards the response to `SimulatorStateService.ReceiveInviteHandshakeResponseFromMainAsync`.
- `ReceiveInviteHandshakeResponseFromMainAsync` queues the response into `_pending` and marks the simulated peer as `AwaitingUserAcceptance` (via `MarkInboundPending`).

This behavior conflates **two different concepts**:

- **Inbound invite request** (requires user acceptance before creating a session)
- **Inbound invite response** (is the acceptance/ack; should immediately finalize and create a session for the original inviter)

The observable failure mode is that the simulator does **not** complete its session, so subsequent chat send attempts fail with “No session found …”.

### Desired end state

- The simulator maintains a clear, explicit state machine for invite-based handshakes.
- When the simulator receives an `InviteHandshakeResponse` for a correlation ID that matches an **outbound invite**, it immediately finalizes the session and transitions to `Established` (no extra click required).
- API surface (public methods) reflects intent and direction:
  - Receiving an invite request is different from receiving an invite response.
  - Queuing for UI acceptance is different from completing a handshake.
- Dead/duplicative methods are removed, and remaining methods are clearly named.

---

### Plan

#### A1. Document the two invite flows and enforce invariants

- **Flow 1: Main -> Simulator (Main inviter, simulator acceptor)**
  - Simulator receives `EstablishDirectSessionRequest`.
  - This flow currently has two competing behavioral models in the codebase:
    - **Flow 1a (auto-accept on ingress)**: simulator creates the session immediately and can optionally defer *delivery* of the `InviteHandshakeResponse`.
    - **Flow 1b (persist pending request + user accepts)**: simulator persists the inbound invite request, and only creates the session + delivers `InviteHandshakeResponse` after explicit user acceptance.
  - **Chunk B** defines the target model for Flow 1b and removes the misleading “already accepted but waiting to deliver” behavior.
  - Main-side behavior note (from `PercolatorMessageService.EstablishDirectSession`): the RPC always returns `EstablishDirectSessionResponse.Queued` after enqueuing a pending session on Main; there is no “accepted immediately” response type in the contract.

- **Flow 2: Simulator -> Main (Simulator inviter, main acceptor)**
  - Simulator sends `EstablishDirectSessionRequest`.
  - Main accepts and creates session, then sends `InviteHandshakeResponse` back to simulator.
  - Simulator must finalize session immediately on receipt.

- **Invariants to encode**
  - An `InviteHandshakeResponse` must always have a valid `RequestCorrelationId` (already enforced on simulator side).
  - A response must match either:
    - an outbound invite (inviter finalization path), or
    - a pending inbound “main-initiated” flow (if we support that), but never silently become “pending acceptance”.

Truth table:

| Message Type | Correlation ID Source | State Transition | Storage |
|-------------|---------------------|------------------|---------|
| `InviteHandshakeResponse` | Matches outbound invite (simulator initiated) | `AwaitingUserAcceptance` → `Established` (immediate) | Session created in `model.SessionsMutable`; outbound invite removed; response NOT queued |
| `InviteHandshakeResponse` | No matching outbound invite (unexpected) | No transition; error logged | Response dropped or stored for diagnostics only |
| `EstablishDirectSessionRequest` (Main → Simulator) | N/A (new correlation) | `Ready` → `AwaitingUserAcceptance` (pending user decision) | Request queued in pending inbound invite store (Chunk B) |
| `EstablishDirectSessionRequest` (Simulator → Main) | N/A (new correlation) | `Ready` → `OutboundPending` | Outbound invite stored in `model.OutboundInvitesMutable` |

Deliverable: truth table documented above; invariants encoded in implementation.

#### A2. Split simulator ingress APIs by message semantics (rename + new methods)

Goal: remove ambiguity in method naming and responsibilities.

- Introduce explicit simulator ingress methods (names are suggestions; pick final names during implementation):
  - `HandleInboundInviteRequestFromMainAsync(...)`  (currently: `ReceiveEstablishDirectSessionFromMainAsync` / `AcceptReverseSignalInviteAsync` path)
  - `HandleInboundInviteResponseFromMainAsync(...)` (currently: `ReceiveInviteHandshakeResponseFromMainAsync`)

- Replace the generic “Receive*” naming with verbs that express intent:
  - `Handle...` for deterministic processing
  - `Queue...` only when intentionally deferring to UI/user action

- Rename misleading state mutations:
  - `MarkInboundPending` is currently used for both “I got a request” and “I got a response”.
  - Create separate UI/state helpers:
    - `MarkInviteRequestPendingUserDecision(corr)`
    - `MarkInviteResponseReceived(corr)` (or skip entirely if response finalizes immediately)

Deliverable: compile-time-safe API where the interceptor cannot “accidentally” route a response into a request-pending path.

#### A2.1 Target public API surface (simulator invite *responses*)

As part of Chunk A, define (and enforce via naming + types) a minimal public surface for simulator-side processing of invite handshake *responses* from Main:

- `HandleInboundInviteHandshakeResponseFromMainAsync(simulatedPeerId, InviteHandshakeResponse response, ct)`
  - Pure response processing. Must not enqueue UI acceptance.

- `TryFinalizeOutboundInviteFromHandshakeResponseAsync(simulatedPeerId, acceptorPeerId, correlationId, ct)`
  - Optional: internal helper used by the handler above.

The existing method `ReceiveInviteHandshakeResponseFromMainAsync` should either be renamed to the handler above, or deleted once call sites are migrated.

#### A3. Implement deterministic simulator-side finalization on response receipt

Design target:

- `HandleInboundInviteHandshakeResponseFromMainAsync(simulatedPeerId, response)` should:
  - Validate fields.
  - Resolve correlation id.
  - Look up matching **outbound invite** record (signed pre-key private, etc.).
  - Finalize using the existing crypto steps (currently in `TryFinalizeInviteHandshakeResponseFromMainAsync`).
  - Persist session into `model.SessionsMutable`.
  - Clear any transient pending markers for that correlation id.
  - Transition UI state to `Established`.

This implies `TryFinalizeInviteHandshakeResponseFromMainAsync` likely becomes:

- `FinalizeOutboundInviteFromHandshakeResponse(...)` (pure finalization, no UI or pending queue)
  - Optionally internal/private helper

Deliverable: simulator does not require an extra manual “accept” after Main already accepted.

#### A4. Fix routing in `SimulatorOutboundInterceptor`

- Update `TryDeliverInviteHandshakeResponse` path to call the *response* handler (not the request-pending handler).
- Add structured logging around:
  - correlation id
  - simulated peer id
  - whether an outbound invite was found
  - final session id

Deliverable: a single breakpoint in the interceptor shows the correct codepath for simulator-initiated handshakes.

#### A5. Simplify and delete obsolete codepaths

Once the flows are separated:

- Re-evaluate whether the following remain necessary:
  - `_pending.AddInviteHandshakeResponse(...)` for simulator inbound responses
  - UI “Accept” path that currently tries finalization then falls back to deliver-to-main
  - `TryDeliverQueuedInviteHandshakeResponseToMainAsync` usage for simulator-initiated flows

If still needed for a test/debug UI, keep them but rename to emphasize they are **debug controls**, not protocol steps.

Deliverable: fewer methods, each with a single responsibility, and no “magic fallback” behavior.

#### A6. Add tests / harness validations (no quick fix; correctness + clarity)

- Add integration-style tests (or deterministic harness tests in `Desktop.Wpf.Tests`) that assert **public behavior** (black-box), using AAA (Arrange/Act/Assert):
  - Simulator creates outbound invite
  - Main accepts -> main sends `InviteHandshakeResponse`
  - Simulator receives response -> session exists -> sending a chat message succeeds
  - Assertions must be on observable outcomes (e.g., service public APIs succeed, UI state transitions exposed via reactive properties, or session existence via public query methods), not on private fields or internal helper call ordering.

- Add negative behavior tests:
  - Response with unknown correlation id is rejected with a clear error and does not transition the simulator peer into an established/accepted state
  - Response with missing required fields fails deterministically

- Mocking guidelines:
  - Mock external dependencies (disk persistence repository, network delivery abstractions) but use real value objects / protobuf messages.
  - Avoid strict interaction verification unless the side-effect itself is the requirement.

Deliverable: regression coverage proving the simulator-initiated handshake produces a session on the simulator side.

---

### Acceptance criteria

- When simulator initiates handshake and Main accepts:
  - simulator ends in `Established`
  - simulator can perform a public behavior that requires a session (e.g., encrypt/send a chat message) without “No session found”

- Public APIs in simulator state service are semantically clear:
  - requests vs responses are handled by different methods
  - queuing/defer-to-UI is explicit and not used for protocol-required steps

## Chunk B

### Problem statement

For **Main -> Simulator** direct invite requests (`EstablishDirectSessionRequest` intercepted by `SimulatorOutboundInterceptor.TryEstablishDirectSession`), the simulator currently:

- Calls `SimulatorStateService.ReceiveEstablishDirectSessionFromMainAsync(...)`.
- Which immediately calls `AcceptReverseSignalInviteAsync(...)`.
- `AcceptReverseSignalInviteAsync(...)` performs cryptographic acceptance and **creates a real session immediately** (`model.SessionsMutable[sessionId] = session`).
- Only *delivery* of the resulting `InviteHandshakeResponse` is deferred by queuing it via `QueueInviteHandshakeResponseForDeliveryToMainAsync(...)`.

This makes the simulator “Accept” button misleading:

- The user is not accepting a pending request.
- The request has already been accepted (session created); the button merely triggers delivery of an already-generated response.

It also creates API ambiguity because the simulator’s current pending mechanisms are oriented around **pending invite handshake responses**:

- `ISimulatedPeerPendingInbox` stores `InviteHandshakeResponse` keyed by `(PeerId, CorrelationId)`.
- Persistence snapshots (`SimulatorStateDto` / `SimulatedPeerRuntimeStoreDto`) include `PendingInviteHandshakeResponses`.
- There is **no persisted representation** of a pending inbound `EstablishDirectSessionRequest` (invite request) waiting for user decision.

### Desired end state

- A direct invite request from Main can be persisted as a **pending inbound invite request**, without creating a session.
- The simulator UI “Accept” / “Reject” semantics are truthful:
  - **Accept**: create session + generate response + deliver response to Main.
  - **Reject**: discard pending request (no session created).
- APIs clearly separate:
  - “Receive/queue inbound invite request” vs “accept inbound invite request”.
  - “queue outbound response for delivery” remains possible, but is not used to simulate user acceptance.
- Persistence supports process restart / snapshot restore without losing pending inbound requests.

---

### Plan

#### B1. Add a persisted model for pending inbound direct invite requests

Introduce a runtime/persistence record representing a pending invite request from Main:

- Required fields (minimum):
  - `CorrelationId` (from `InviteHandshakeRequestPayload.RequestCorrelationId`)
  - Original request bytes (`EstablishDirectSessionRequest` raw bytes or the payload bytes)
  - `ReceivedAtUtc`
  - `InviterIdentityKeySpki` (optional redundancy; can be derived from request)
  - Optional routing metadata for diagnostics (e.g., direct endpoint / `context.Peer` string if available)

Update persistence DTOs and snapshots:

- Add `PendingInboundDirectInvites` (or similar) to `SimulatedPeerRuntimeStoreDto`.
- Add corresponding DTO(s) in `SimulatorState.cs`.
- Update `JsonSimulatorStateRepository` hydration + save.
- Update `PeerStateSnapshot` (if used by UI) to include the new pending inbound invite requests.

- Domain placement (to reduce ambiguity):
  - Pending inbound direct invite requests should live as a domain collection on `SimulatedPeerModel` (similar to `PendingInviteHandshakeResponsesMutable`), and be included in `SimulatedPeerModel.Freeze()`.
  - `JsonSimulatorStateRepository` must hydrate/save this new collection via `SimulatedPeerRuntimeStoreDto`.

Deliverable: simulator restart does not lose pending inbound direct invites.

#### B2. Split simulator pending inbox responsibilities (requests vs responses)

Current:

- `ISimulatedPeerPendingInbox` is *response-only* (`InviteHandshakeResponse`).

Target:

- Add a new pending store abstraction (or extend with new methods) for inbound **invite requests**.
  - Example shape:
    - `AddInboundDirectInviteRequest(simPeerId, corrId, requestBytes)`
    - `TryGetInboundDirectInviteRequest(...)`
    - `TryTakeInboundDirectInviteRequest(...)`

Avoid overloading “InviteHandshakeResponse” pending structures to store requests.

Deliverable: request-pending state and response-pending state cannot be confused at the type level.

#### B3. Change interception handling: queue request instead of accepting

Update the behavior of `SimulatorStateService.ReceiveEstablishDirectSessionFromMainAsync`:

- Parse correlation id from `InviteHandshakeRequestPayload`.
- Persist/record the inbound request as pending.
- Transition UI state to an explicit “pending inbound invite request” state.
- Return `EstablishDirectSessionResponse.Queued` (or similar) without creating a session.

Contract alignment note:

- This is aligned with the Main implementation: `PercolatorMessageService.EstablishDirectSession` also returns `Queued` after enqueueing a pending session.
- The system is already designed such that “queued” is a valid/expected response; do not add polling or busy-wait behavior.

This makes `ReceiveEstablishDirectSessionFromMainAsync` a pure ingress method, not an acceptor.

Deliverable: receiving a direct invite does not create a session until user accepts.

#### B4. Introduce explicit accept/reject APIs for pending inbound direct invites

Add explicit public methods on `ISimulatorStateService`:

- `AcceptPendingInboundDirectInviteFromMainAsync(simulatedPeerId, correlationId, mainPeerId, ct)`
  - Loads pending request
  - Calls the cryptographic acceptance routine (likely refactor `AcceptReverseSignalInviteAsync` into an internal helper)
  - Creates session
  - Generates `InviteHandshakeResponse`
  - Delivers response to Main
  - Clears pending request
  - Marks peer established

- `RejectPendingInboundDirectInviteFromMainAsync(simulatedPeerId, correlationId, ct)`
  - Clears pending request
  - Updates UI state appropriately

Deliverable: UI accept/reject maps 1:1 to protocol semantics.

#### B4.1 Target public API surface (pending inbound direct invite *requests*)

Define a minimal, intention-revealing public API for inbound direct invite requests from Main:

- `QueueInboundDirectInviteRequestFromMainAsync(simulatedPeerId, mainPeerId, EstablishDirectSessionRequest request, ct)`
  - Ingress-only. Stores request as pending. Does not create session.

- `AcceptPendingInboundDirectInviteRequestFromMainAsync(simulatedPeerId, correlationId, mainPeerId, ct)`
  - Acceptance action. Creates session, generates response, delivers response, clears pending.

- `RejectPendingInboundDirectInviteRequestFromMainAsync(simulatedPeerId, correlationId, ct)`
  - Rejection action. Clears pending.

Note: `ReceiveEstablishDirectSessionFromMainAsync` should become either a thin wrapper around `QueueInboundDirectInviteRequestFromMainAsync` or be deleted to avoid duplicated ingress entry points.

#### B5. Refactor/rename `AcceptReverseSignalInviteAsync` to reflect new meaning

After B3/B4, `AcceptReverseSignalInviteAsync` should no longer be callable as a general-purpose public API from multiple directions.

Options:

- Make it `internal` and rename to `CreateSessionAndHandshakeResponseForInboundDirectInvite(...)`.
- Or keep as public but rename to reflect it is the *acceptance action* (not ingress), and ensure ingress never calls it.

Also reconcile the UI command paths:

- `SimulatedPeerItemViewModel.ExecuteMainInviteDirectAsync` currently calls `AcceptReverseSignalInviteAsync` directly (immediate accept+deliver). Decide whether that command should:
  - remain a “debug shortcut” (clearly named/labeled), or
  - be migrated to the same pending/accept mechanism for consistency.

Deliverable: a single authoritative acceptance API, with optional explicit debug shortcuts.

#### B6. Update UI state model to represent pending inbound invite request explicitly

Research validation:

- The simulator already shows a unified “Handshake State Machines” list (`SimulatorHandshakesTabView` + `SimulatorHandshakesTabViewModel`). It renders one `SimulatedHandshakeStateMachineCardViewModel` per peer.
- Each card exposes a single `AcceptHandshakeCommand` button when `UiState == AwaitingUserAcceptance` and `InboundReverseSignalPendingCorrelationId != null`.
- `SimulatedHandshakeStateMachineCardViewModel.ExecuteAcceptHandshakeAsync` currently uses *protocol guessing*:
  - first `TryFinalizeInviteHandshakeResponseFromMainAsync(...)`
  - if that returns `null`, then `TryDeliverQueuedInviteHandshakeResponseToMainAsync(...)`

Target model (keep the unified UI surface, remove guessing, remove implicit selection):

- Keep the unified “Handshake State Machines” surface, **but** make “accept” and “reject” operate on a specific pending item (no implicit selection, no `FirstOrDefault()`).
- Replace the single per-peer Accept button with a per-item list of pending approvals:
  - For **reverse-signal inbound direct invite requests**: source is `SimulatedPeerModel.PendingInboundDirectInvites` (already keyed by `CorrelationId`).
  - For **standard-signal inbound hellos**: source is the existing pending standard-signal collection already shown in the UI.
- Introduce a small UI projection model for the list (ViewModel-only):
  - Example: `PendingApprovalItem(CorrelationId, Kind, ReceivedAtUtc, DisplayText, ...)`
  - `Kind` must be explicit and intention-revealing.
  - Pending approval kinds (explicit list): `InboundDirectInviteRequestFromMain`, `InboundStandardSignalHello`.
- In `SimulatedHandshakeStateMachineCardViewModel`:
  - Expose `PendingApprovals` as a projected read-only list suitable for binding.
  - Add commands that take a parameter:
    - `AcceptPendingApprovalCommand : ReactiveCommand<PendingApprovalItem>`
    - `RejectPendingApprovalCommand : ReactiveCommand<PendingApprovalItem>`
  - Command implementations must dispatch by `PendingApprovalItem.Kind` and must pass the specific `CorrelationId` through to service APIs.

XAML wiring:

- Render `PendingApprovals` via an `ItemsControl`.
- Bind per-row buttons with `CommandParameter="{Binding}"` (or `CorrelationId` if you prefer).

Service routing rules (deterministic):

- `InboundDirectInviteRequestFromMain` -> call `ISimulatorStateService.AcceptPendingInboundDirectInviteAsync(simulatedPeerId, correlationId, ...)`.
- `InboundStandardSignalHello` -> call the existing standard-signal accept API (already parameterized).
- Any unsupported `Kind` -> throw or surface a diagnostic event (do not guess).

Notes:

- `UiState == AwaitingUserAcceptance` becomes a *derived UI concern* (e.g., `PendingApprovals.Count > 0`) and must not be the source of truth.

Deliverable: UI provides explicit per-item accept/reject actions, with correct `CorrelationId` routing and no protocol-guessing fallbacks.

#### B6.0 Revisit Chunk A temporary no-op stubs (must be removed)

Chunk A intentionally replaced some UI acceptance paths with temporary no-op behavior to avoid breaking the UI while the correct Chunk B model is implemented.

These temporary behaviors are **dangerous** if left in place because they can silently mask correctness issues and create “green but wrong” UI flows.

**Required revisit list (explicit):**

- `Desktop.Wpf/Features/Simulator/SimulatedHandshakeStateMachineCardViewModel.cs`
  - `ExecuteAcceptHandshakeAsync`
  - Chunk B requirement: this must be replaced by per-item accept/reject commands that take an explicit `CommandParameter` representing the target pending item (correlation id + kind) and call the real accept/reject APIs (not log-and-return).

- `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs`
  - `ExecuteAcceptInboundPendingAsync`
  - Chunk B requirement: this must either be removed from the UX surface or wired to `AcceptPendingInboundDirectInviteAsync(simulatedPeerId, correlationId, ...)` (not log-and-return).

#### B6.1 Cleanup: split persisted pending handshake stores by handshake family + direction

Motivation (from current `SimulatedPeerModel` + JSON DTOs): there are already multiple “pending” concepts stored in different shapes, some persisted as runtime store lists and some persisted as ad-hoc UI fields:

- Reverse-signal (direct) related:
  - `SimulatedPeerModel.OutboundInvites` (persisted in `RuntimeStore.OutboundInvites`)
  - `SimulatedPeerModel.PendingInviteHandshakeResponses` (persisted in `RuntimeStore.PendingInviteHandshakeResponses`)
  - `SimulatedPeerModel.UiState` + `InboundReverseSignalPendingCorrelationId` (persisted today via `SimulatedPeerDto.UiState` + `SimulatedPeerDto.PendingCorrelationId`)

- Standard-signal related:
  - `SimulatedPeerModel.PendingInboundStandardSignalHellos` (currently runtime-only; cleared on `MarkEstablished()` / `ClearRuntimeState()` and not represented in JSON DTOs)
  - `SimulatedPeerModel.PendingStandardHandshakeToMainResponderPublicKeyHash` + `PendingStandardHandshakeToMainTemporarySessionId` (persisted today as top-level fields on `SimulatedPeerDto`)

Target outcome:

- Persistence is cleanly separated by:
  - handshake family (**reverse-signal** vs **standard-signal**)
  - direction (**inbound-from-main**, **outbound-to-main**, and optionally “inbound-from-peers” for standard-signal hellos)
- The ViewModel layer is responsible for merging “awaiting user approval” items into a single UI projection (consistent with MVVM/R3 rules).

Proposed restructuring (DTO + model):

1) **Move ad-hoc pending handshake persistence out of `SimulatedPeerDto` UI fields**

- Stop persisting pending correlation id via `SimulatedPeerDto.PendingCorrelationId`.
- `SimulatedPeerDto.UiState` becomes **derived only** (not persisted). The persisted truth for handshake state is the set of explicit pending stores + sessions.
- Persist pending handshakes exclusively in explicit runtime store collections.

Decision (no backwards compatibility):

- There is no need to load/translate legacy fields. Assume new JSON files only.
- It is acceptable to remove `PendingCorrelationId` persistence immediately and rely on the new explicit stores.

2) **Split `SimulatedPeerRuntimeStoreDto` into sub-stores**

- Add nested DTOs under `SimulatedPeerRuntimeStoreDto` (or adjacent properties) such as:
  - `ReverseSignalStore`:
    - `OutboundInvitesToMain` (existing `OutboundInvites`)
    - `InboundInviteHandshakeResponsesFromMain` (existing `PendingInviteHandshakeResponses` *until Chunk A finalization becomes immediate and this list is no longer needed*)
    - `InboundDirectInviteRequestsFromMain` (new for Chunk B; this is the real “pending acceptance” store for Main->Simulator direct invites)
  - `StandardSignalStore`:
    - `PendingInboundHellos` (**runtime-only; do not persist**). These are transient inbound discovery/hello items and can be cleared on restart.
    - `PendingHandshakeToMain` (re-home the existing responder PKH + temporary session id into this store)

This keeps reverse-signal and standard-signal state from being conflated.

3) **Update `SimulatedPeerModel` to match the split stores**

- Keep existing collections, but rename and group them so their purpose is explicit:
  - reverse-signal outbound-to-main
  - reverse-signal inbound-from-main
  - standard-signal pending-to-main
  - standard-signal pending-from-peers

4) **ViewModel merges “awaiting approval” items**

- `SimulatedHandshakeStateMachineCardViewModel` (or a new child VM) creates a single, merged read-only projection:
  - `IReadOnlyList<PendingApprovalItem>` derived from the per-family stores.

Define `PendingApprovalItem` as a pure UI projection (not persisted) that includes:

- `CorrelationId`
- `Family` (`ReverseSignal` | `StandardSignal`)
- `Direction` (`InboundFromMain` | `OutboundToMain` | `InboundFromPeers`)
- `Kind` (fine-grained, e.g. `DirectInviteRequestFromMain`, `InviteHandshakeResponseFromMain`, etc.)
- `ReceivedUtc` (if applicable)

Cardinality/invariants:

- The model may contain multiple pending items across families.
- The UI **must not** rely on a single correlation id slot to decide which item is “current”.

Preferred UX invariant:

- Render the merged list and provide Accept/Reject per item. Do not implement a per-peer implicit selection fallback.

Approval relevance inventory (what appears in the merged approval list):

- **Approval-relevant** (require simulator user acceptance):
  - Simulator **inbound-from-main** direct/reverse-signal invite requests (`ReverseSignalStore.InboundDirectInviteRequestsFromMain`).
  - Simulator **inbound-from-peers** standard-signal hellos (current UI already treats these as per-item accept actions).

- **Not approval-relevant** (no simulator confirmation needed; initiated by simulator button clicks):
  - Simulator outbound-to-main reverse-signal invites (`ReverseSignalStore.OutboundInvitesToMain`).
  - Simulator outbound-to-main standard-signal handshakes (the `StandardSignalStore.PendingHandshakeToMain` tracking state).

Deterministic ordering rule (only needed if you keep a single Accept button per peer):

- Not applicable: the plan uses per-item actions and does not keep a per-peer implicit selection.

5) **DTO versioning (forward-only)**

- Keep DTO `Version` bumps localized (e.g., bump `SimulatedPeerRuntimeStoreDto.Version`).
- Only support reading the new version(s).

Deliverable: JSON clearly shows separate stores for reverse-signal vs standard-signal, and inbound vs outbound to/from Main; UI derives pending-approval list by merging stores; no protocol guessing.

#### B7. Delete dead code / obsolete paths once Chunk B is complete

After Chunk B is fully implemented and the simulator has a first-class persisted pending inbound direct invite request model, the following code paths should become unnecessary and should be deleted (or converted into explicitly labeled debug-only utilities):

- `SimulatorStateService.ReceiveEstablishDirectSessionFromMainAsync` calling `AcceptReverseSignalInviteAsync`.
  - Replace with “queue inbound invite request” behavior; remove the auto-accept call.

- “Queued response as pending acceptance” mechanism for Main->Simulator direct invites:
  - `QueueInviteHandshakeResponseForDeliveryToMainAsync` usage from `ReceiveEstablishDirectSessionFromMainAsync`.
  - `TryDeliverQueuedInviteHandshakeResponseToMainAsync` usage as part of the simulator accepting an inbound direct invite.

- UI fallback logic that guesses which protocol direction is happening:
  - In `SimulatedHandshakeStateMachineCardViewModel`, delete `ExecuteAcceptHandshakeAsync` and the “try finalize; if null then deliver queued response” guessing logic.
  - Replace it with per-item accept/reject commands bound from an `ItemsControl`, with routing driven by the pending item `Kind`.

- Ambiguous naming that encourages misuse:
  - If `AcceptReverseSignalInviteAsync` is kept public, ensure there is no longer any ingress method that calls it.
  - Prefer making the crypto/session creation routine internal and reachable only from explicit “AcceptPending…” methods.

Deliverable: no remaining code that uses “response queueing” to emulate user acceptance, and no “try X then fallback to Y” acceptance logic.

#### B8. Chunk B correctness + determinism hardening (recommended follow-ups)

The initial Chunk B implementation can be made semantically correct and deterministic by addressing the following gaps.

- **Persist the inviter peer id (Main) for pending inbound direct invites**
  - Extend the persisted pending inbound direct invite model/DTO to store the inviter `PeerId` (or store `mainPeerId`).
  - When `ReceiveEstablishDirectSessionFromMainAsync(simulatedPeerId, mainPeerId, request)` queues the inbound invite request, persist `mainPeerId` alongside the request bytes.
  - In `AcceptPendingInboundDirectInviteAsync`, use the persisted inviter peer id instead of generating a new `Guid`.

- **Tighten request validation on ingress**
  - In `ReceiveEstablishDirectSessionFromMainAsync`, validate `request.HasPayload`/`Payload.Length > 0` and `request.HasInviterIdentityKey`/`InviterIdentityKey.Length > 0` before parsing.
  - Validate presence of `payload.RequestCorrelationId` before using it.

- **Render pending inbound invites as an explicit list (no selection slot)**
  - Ensure the UI uses per-item accept/reject with `CommandParameter` set to the target item.
  - Do not implement or persist any “selected pending correlation id” pointer for inbound invites.

- **Introduce explicit pending handshake kind (to remove accept-guessing)**
  - Update the ViewModel accept/reject actions to dispatch based on an explicit pending item kind, rather than using fallback/guessing behavior.

- **API surface cleanup (clarity)**
  - Keep `AcceptInboundDirectInviteAsync` as a crypto/session-building primitive.
  - Ensure the primary public workflow for inbound direct invites is:
    - receive/queue (ingress)
    - accept/reject (explicit user action)
    - deliver response (explicit user action, if required)

Deliverable: accepting a pending inbound invite creates a session with the correct remote peer id, ordering is deterministic with multiple pending items, and there is no protocol-direction guessing.

#### B9. Tests / verification (unit-testing.md compliant)

Add deterministic tests (Desktop.Wpf.Tests or integration harness) that follow AAA and the black-box rule:

- **Inbound direct invite is pending**
  - Main sends `EstablishDirectSessionRequest`
  - Simulator does not create a session yet
  - Assert pending-ness via **publicly exposed domain/service state** (read-only observable collection of pending inbound direct invites), not via UI implementation details
  - Assert attempting to perform a session-required action fails deterministically

- **Accept creates session + delivers response**
  - User accepts pending invite
  - Session exists and simulator can now perform a public behavior that requires a session (e.g., encrypt/send a message)
  - Response delivery to Main is observed via the public network abstraction/harness result, not by inspecting private queues

- **Accept routing is deterministic (no guessing)**
  - Given a pending approval item of kind `InboundDirectInviteRequestFromMain`, Accept generates+delivers `InviteHandshakeResponse` and does not attempt “finalize response from main”.
  - Given a pending approval item of kind `InboundStandardSignalHello`, Accept uses the standard-signal accept API and does not attempt any reverse-signal delivery.

- **Reject does not create session**
  - Pending cleared
  - Simulator still cannot perform session-required actions for that peer
  - Assert pending collection no longer contains that correlation id (via public read-only collection)

- **Persistence**
  - Pending invite requests survive snapshot save/restore

- Mocking guidelines:
  - Mock only external dependencies (repository/network). Use real DTOs/protobuf messages and real crypto keys as needed.
  - Avoid asserting internal collection contents directly unless it’s part of the public contract.

---

### Reactive MVVM / R3 architecture constraints (must hold for Chunk A + B)

- **Service/UI separation**:
  - `SimulatorStateService` remains UI-agnostic (no `IUiDispatcher`, no WPF types). It owns canonical domain state (`ObservableList`, `ObservableDictionary`, `ReactiveProperty`) protected by its gates.
  - ViewModels are responsible for marshaling to UI thread using `CreateView(...).ToNotifyCollectionChanged(_ui.CollectionEventDispatcher)` and `ObserveOnCurrentSynchronizationContext()` for scalar projections.

- **No sorting/filtering in services**:
  - Services should not sort/filter; use XAML `CollectionViewSource` and view filters.

- **Collection projection rules**:
  - Do not replace list instances in `BindableReactiveProperty<IReadOnlyList<T>>` for UI lists.
  - Project domain collections via `CreateView` and bind to the notify adapter.
  - Dispose child ViewModels explicitly on removals to avoid leaks.

---

### Acceptance criteria

- Receiving a direct invite request from Main does not create a session until simulator user accepts.
- After simulator accepts, simulator can perform a public behavior that requires a session, and Main receives `InviteHandshakeResponse`.
- Pending inbound direct invites are persisted and restored correctly.
- Public simulator APIs clearly separate ingress (receive/queue) from actions (accept/reject).

## Chunk C

### Problem statement

The simulator currently persists a single correlation id slot (`SimulatedPeerDto.PendingCorrelationId` / `SimulatedPeerModel.InboundReverseSignalPendingCorrelationId`) and uses it as an implicit “current handshake attempt” pointer.

This is incompatible with realistic simulator behavior where multiple items can be active concurrently:

- multiple outbound invite attempts to Main
- multiple inbound approval items from Main
- multiple pending standard-signal hellos

It also encourages UI logic that overwrites global state, loses context, and mis-associates errors/phases with the wrong attempt.

### Desired end state

- The simulator models handshake/approval items explicitly as a collection (multiple may be active at once).
- The UI renders these items and provides explicit per-item actions.
- No persisted field represents a “selected” item.
- All state updates (phase/error/not-until) target a specific item by correlation id.
- Persistence round-trips all pending/attempt items and their state.

---

### Plan

#### C1. Create a first-class model for pending/attempt items

Use the existing persisted attempt model as the “first-class handshake/approval item”:

- `Desktop.Wpf/Features/Simulator/SimulatorHandshakeAttemptState.cs` (`SimulatorHandshakeAttemptState`)
- `Desktop.Wpf/Features/Simulator/SimulatedPeerModel.cs` (`HandshakeAttempts`, `UpsertAttempt`, `SetAttemptPhase/Error/NotUntil`)
- Persisted today via `Desktop.Wpf/Features/Simulator/SimulatorState.cs` (`SimulatedPeerDto.HandshakeAttempts`)

Extend `SimulatorHandshakeAttemptState` to be intention-revealing and UI-friendly (so we can render a list and drive per-item actions) by adding:

- `CorrelationId`
- `Family` enum (at minimum: `ReverseSignal`, `StandardSignal`)
- `Direction` enum (at minimum: `OutboundToMain`, `InboundFromMain`, `InboundFromPeers`)
- `Kind` enum (at minimum: `OutboundDirectInviteToMain`, `InboundDirectInviteFromMain`, `InboundStandardSignalHello`)
- `CreatedAtUtc` (already present)
- Optional `ReceivedAtUtc` (for inbound items)

Attach per-item state:

- `Phase` (string or enum)
- `LastError` (string?)
- `NotUntilUtc` (DateTimeOffset?)
- Optional route metadata (`SelectedRouteMode`, `RelayHostPeerId`, `DirectEndpoint`) where applicable

Scope decision for Chunk C:

- Keep `PendingInboundDirectInvites` as the authoritative inbound-approval queue (`SimulatedPeerModel.PendingInboundDirectInvites` persisted under `SimulatedPeerRuntimeStoreDto.PendingInboundDirectInvites`).
- Ensure an attempt entry exists for the same `CorrelationId` when:
  - an inbound direct invite is received (kind=`InboundDirectInviteFromMain`)
  - an outbound direct invite is sent/enqueued (kind=`OutboundDirectInviteToMain`)
  - a standard-signal hello arrives (kind=`InboundStandardSignalHello`)

Invariants:

- Multiple items may exist simultaneously.
- No single global “current correlation id” is authoritative.

#### C2. Deprecate and remove single-slot semantics

Eliminate dependence on these fields as a pointer to the “current” attempt:

- `SimulatedPeerDto.PendingCorrelationId`
- `PeerStateSnapshot.InboundReverseSignalPendingCorrelationId`
- `_model.InboundReverseSignalPendingCorrelationId`

Concrete code locations:

- `Desktop.Wpf/Features/Simulator/SimulatorState.cs` (`SimulatedPeerDto.PendingCorrelationId`)
- `Desktop.Wpf/Features/Simulator/PeerStateSnapshot.cs` (`InboundReverseSignalPendingCorrelationId`)
- `Desktop.Wpf/Features/Simulator/JsonSimulatorStateRepository.cs` mapping:
  - hydration: `CreatePeerSnapshot(... pendingCorrelationId: dto.PendingCorrelationId, ...)`
  - persistence: `dto.PendingCorrelationId = model.InboundReverseSignalPendingCorrelationId`

Migration strategy (pick one explicitly during implementation):

- Option A (breaking): remove these fields and stop loading legacy JSON.
- Option B (non-breaking): keep fields for load only, but never use for behavior and stop writing on save.

Transitional rule (applies to both options):

- If the slot is kept temporarily for back-compat, it must not be used for routing, acceptance, or attempt association.

#### C3. Service changes: all updates target explicit items

Rules:

- All phase/error/not-until updates must target a specific item by `CorrelationId`.
- Ingress creates/updates attempts explicitly (using correlation id from payload).
- Accept/reject/finalize methods remove or transition the specific item.

Known problematic call sites to fix (currently associates failures with the wrong item):

- `Desktop.Wpf/Features/Simulator/SimulatedHandshakeStateMachineCardViewModel.cs`
  - In outbound send/enqueue error handling, code currently reads `var corr = _model.InboundReverseSignalPendingCorrelationId.CurrentValue;`.
  - Replace this with “the correlation id for this outbound operation” (the one parsed from payload / generated fallback) and update attempt state using that id.

Implementation approach:

- Anytime we parse or generate a correlation id for an operation, store it in a local variable and use it consistently for:
  - `_model.MarkOutboundPending(corr)`
  - `_model.SetAttemptPhase(corr, ...)`
  - `_model.SetAttemptError(corr, ...)`
  - `_model.SetAttemptNotUntil(corr, ...)`

Deliverable:

- No code path reads a global “current corr” to decide what to update.

#### C4. UI changes: render a list and provide per-item actions

Update simulator UI to render a list of active items per peer. Prefer using the existing "Handshake State Machines" surface and make it show the list of attempts.

Concrete data source:

- `SimulatedPeerModel.HandshakeAttempts` + `SimulatedPeerModel.HandshakeAttemptsVersion`

Concrete ViewModel work:

- In `Desktop.Wpf/Features/Simulator/SimulatedHandshakeStateMachineCardViewModel.cs`, add a projected property like:
  - `BindableReactiveProperty<IReadOnlyList<SimulatorHandshakeAttemptState>> HandshakeAttempts`
  - project from `_model.HandshakeAttempts` and refresh on `_model.HandshakeAttemptsVersion`
  - sort by `CreatedAtUtc` (descending) for display only

Per-item actions:

- Inbound approval items: `Accept`, `Reject`
- Outbound attempts: `Retry` / `Cancel` / `Clear` (exact set based on what operations exist)

Minimum viable actions for Chunk C:

- Keep existing accept/reject for inbound direct invites via `ISimulatorStateService.AcceptPendingInboundDirectInviteAsync(simulatedPeerId, correlationId)` and `RejectPendingInboundDirectInviteAsync(...)`.
- Drive actions from explicit item correlation id (button passes corr), never from a global slot.

Remove reliance on:

- `UiState == AwaitingUserAcceptance` + a single “Accept” button that implicitly targets one correlation id

If keeping a single Accept button temporarily:

- The ViewModel selects an active item by a documented rule.
- Selection is derived from the collection (not persisted).

#### C5. Persistence

Persistence already exists for attempts:

- `SimulatedPeerDto.HandshakeAttempts` (JSON)
- Hydrated via `JsonSimulatorStateRepository.CreatePeerSnapshot(... handshakeAttempts: dto.HandshakeAttempts, ...)`

Update persistence for new attempt fields:

- Extend `SimulatorHandshakeAttemptState` with the new fields; the serializer will include them.
- Add strict hydration defaults where required (e.g., missing enum -> safe default or throw, choose explicitly).

Stop writing legacy slot fields once migrated (see C2 strategy):

- Remove/ignore `SimulatedPeerDto.PendingCorrelationId`.
- Remove/ignore `PeerStateSnapshot.InboundReverseSignalPendingCorrelationId`.

#### C6. Tests

Add tests validating multi-item behavior:

- Multiple outbound attempts can be active concurrently; updates apply to the correct correlation id.
- Multiple inbound approval items can be queued concurrently; accept/reject applies to the chosen item.
- Persistence round-trip retains the full collection and per-item state.

Concrete test placement:

- Persistence: extend `Desktop.Wpf.Tests/SimulatorStateStoreTests.cs` to add multiple `HandshakeAttempts` entries with distinct ids and assert round-trip.
- Behavior: add/extend tests in `Desktop.Wpf.Tests` validating that per-correlation updates do not depend on `InboundReverseSignalPendingCorrelationId`.

---

### Acceptance criteria

- Simulator UI can display multiple active handshake/approval items per peer.
- Each item can be acted on explicitly (no hidden global “current attempt” semantics).
- Errors/phases are associated with the correct correlation id.
- Persistence round-trips the collection.