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

Target model (keep the unified UI surface, remove guessing):

- Keep the unified “Handshake State Machines” list and a single Accept button per peer, **but** make the Accept path deterministic.
- Replace the current “one slot” (`UiState` + `InboundReverseSignalPendingCorrelationId`) with explicit pending handshake metadata keyed by correlation id:
  - e.g., `PendingHandshakeKind` = `InboundInviteRequestFromMain` | `InboundInviteResponseFromMain` | `OutboundInviteAwaitingResponse` (names can be refined)
  - stored in domain (`SimulatedPeerModel`) and included in `Freeze()` / persistence.
- Update the card VM to map `ShowAccept*` from the presence of a pending handshake item (by kind), not from overloaded `UiState` alone.
- Update `AcceptHandshakeCommand` to dispatch by kind:
  - `InboundInviteResponseFromMain` -> finalize session (Chunk A)
  - `InboundInviteRequestFromMain` -> accept pending inbound direct invite request (Chunk B)
  - any other kind -> no-op / diagnostic event

This preserves your “single list / single accept button” UX while making the behavior semantically correct.

Deliverable: deterministic UI logic with no protocol-guessing fallbacks.

#### B6.0 Revisit Chunk A temporary no-op stubs (must be removed)

Chunk A intentionally replaced some UI acceptance paths with temporary no-op behavior to avoid breaking the UI while the correct Chunk B model is implemented.

These temporary behaviors are **dangerous** if left in place because they can silently mask correctness issues and create “green but wrong” UI flows.

**Required revisit list (explicit):**

- `Desktop.Wpf/Features/Simulator/SimulatedHandshakeStateMachineCardViewModel.cs`
  - `ExecuteAcceptHandshakeAsync`
  - Chunk B requirement: this must become deterministic dispatch based on explicit pending handshake kind, and must call the real accept/finalize APIs (not log-and-return).

- `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs`
  - `ExecuteAcceptInboundPendingAsync`
  - Chunk B requirement: this must either be removed from the UX surface or wired to the new pending inbound invite request acceptance API (not log-and-return).

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
- If the UX remains “one Accept button per peer”, the ViewModel must select a single `PendingApprovalItem` deterministically (documented ordering rule) and expose that as the “active” item.

Approval relevance inventory (what appears in the merged approval list):

- **Approval-relevant** (require simulator user acceptance):
  - Simulator **inbound-from-main** direct/reverse-signal invite requests (`ReverseSignalStore.InboundDirectInviteRequestsFromMain`).
  - Simulator **inbound-from-main** standard-signal requests (whatever store represents this flow after cleanup; if represented as “pending inbound standard-signal request from main”, it is approval-relevant).

- **Not approval-relevant** (no simulator confirmation needed; initiated by simulator button clicks):
  - Simulator outbound-to-main reverse-signal invites (`ReverseSignalStore.OutboundInvitesToMain`).
  - Simulator outbound-to-main standard-signal handshakes (the `StandardSignalStore.PendingHandshakeToMain` tracking state).

Deterministic ordering rule (only needed if you keep a single Accept button per peer):

- Highest priority active approval item: `ReverseSignal.InboundDirectInviteRequestFromMain` (needs user acceptance).
- If there are multiple inbound-from-main approval items, prefer the oldest `ReceivedUtc` first (FIFO), with a stable tie-breaker of `CorrelationId`.
- Preferred long-term UX: render the merged list and provide Accept/Reject per item.

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
  - In `SimulatedHandshakeStateMachineCardViewModel.ExecuteAcceptHandshakeAsync`, remove the fallback branch:
    - The “try finalize; if null then deliver queued response” guessing logic should be deleted.
    - Replace it with deterministic dispatch based on `PendingHandshakeKind`.
  - Keep a unified Accept command, but make it deterministic.

- Ambiguous naming that encourages misuse:
  - If `AcceptReverseSignalInviteAsync` is kept public, ensure there is no longer any ingress method that calls it.
  - Prefer making the crypto/session creation routine internal and reachable only from explicit “AcceptPending…” methods.

Deliverable: no remaining code that uses “response queueing” to emulate user acceptance, and no “try X then fallback to Y” acceptance logic.

#### B8. Tests / verification (unit-testing.md compliant)

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
  - Given a pending handshake item of kind `InboundInviteResponseFromMain`, Accept finalizes and does not attempt delivery-to-main.
  - Given a pending handshake item of kind `InboundInviteRequestFromMain`, Accept generates+delivers `InviteHandshakeResponse` and does not attempt “finalize response from main”.

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