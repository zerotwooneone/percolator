# Main Window + Simulator Handshake Enablement Plan

Goal: make the **Main Window** and **Simulator Window** capable of performing **handshakes in both directions** (Main -> Simulator peer, Simulator peer -> Main), while implementing the Main Window UI behaviors described in `design/main-window.md`.

Protocol anchor:

- The handshake UX described here is primarily the **Reverse-Signal** flow from `session-flow.md` Part 2.
  - The “inviter” sends an **invitation** (`EstablishDirectSessionRequest` containing `InviteHandshakeRequestPayload`).
  - The “acceptor” queues the invitation for approval.
  - On acceptance, the acceptor initiates X3DH as the initiator and replies with an **`InviteHandshakeResponse`** (containing the first ratchet message).
- “Direct vs Relayed” is a **transport routing** choice; crypto payloads remain the same.

Routing model (important for chunk correctness):

- **Direct**: invitation/response are delivered via the callback endpoint using `inviter_host` + `inviter_port` embedded in the invite payload.
- **Relayed**: invitation/response are delivered as opaque bytes through the relay queue (relay host peer id).

Constraints / notes:

- Chunks are intentionally large.
- The project does not need to be buildable or testable between chunks.
- Prioritize **handshake plumbing + UI** over full visual polish.
- Replace the current “Add peer” context menu entry points with a **Connection Management** dialog.
- UI iconography assumes the user has NerdFont available (see `Shared/Theme/Icons.xaml`). 

---

## Chunk A 

Outcome:

- Simulator persistence is expressed as a **single snapshot** round-trip:
  - `ISimulatorStateRepository.LoadStateAsync(ct)`
  - `ISimulatorStateRepository.SaveStateAsync(snapshot, ct)`
- `JsonSimulatorStateRepository` becomes a pure serializer/deserializer of the snapshot (no piecemeal peer/relationship/relay calls).
- `SimulatorStateService` owns:
  - Hydrating runtime models from `SimulatorStateSnapshot`.
  - Freezing runtime state into `SimulatorStateSnapshot`.
- Unit tests use an in-memory snapshot repository stub and validate snapshot contents (sessions included).

Motivation:

- Today persistence is split across:
  - `LoadPeersAsync` / `SavePeersAsync`
  - `LoadRelationshipsAsync`
  - `LoadRelayAsync` / `SaveRelayAsync` (separate files)
  - plus repository-internal state (`_groups`, `_selfIdentityIdByPeerId`)
- The split contracts allow mismatched writes/reads and make it harder to reason about “what is the simulator state at time T”.
- A single snapshot makes persistence and tests deterministic and simplifies reasoning about rehydration (especially for sessions/ratchet state).

Non-goals / constraints:

- No migration/version handling for preexisting persistence files.
  - Existing simulator state files can be deleted when this change lands.
- Relays are part of simulator state and are persisted in the same file as peers.
  - There must be no relay persistence directory and no relay files.
- Use a single concurrency gate for all simulator state (peers, relationships, relays).
- Groups are round-tripped but are not domain-mutated in Chunk A.

Work (plan):

### A.1 — Introduce `SimulatorStateSnapshot`

- Add an immutable snapshot type that is the **only** persistence surface:
  - `public sealed record SimulatorStateSnapshot(...)`

Snapshot fields (must be exhaustive enough to replace current repo methods):

- `int Version`
- `IReadOnlyList<PeerStateSnapshot> Peers`
- `IReadOnlyList<PeerRelationshipSnapshot> Relationships`
- `IReadOnlyList<RelayStateSnapshot> Relays`

- `IReadOnlyList<GroupConversationDto> Groups`
  - Groups are persisted as part of the repository snapshot (they are not domain-mutated by the simulator runtime today).

Peer snapshot requirements:

- `PeerStateSnapshot` must carry `SelfIdentityId`.
  - Add `int SelfIdentityId` as a first-class field on `PeerStateSnapshot`.
  - This removes the last repository-owned cross-call cache (`_selfIdentityIdByPeerId`).

Identity id allocation rule:

- `SimulatorStateService` owns allocating new `SelfIdentityId` values.
  - On initialization: set `_nextSelfIdentityId` to `max(snapshot.Peers.Select(p => p.SelfIdentityId))` (default baseline 99000 - 1).
  - On `AddPeerAsync`: allocate `SelfIdentityId = ++_nextSelfIdentityId`.

### A.2 — Change repository interface

- Update `ISimulatorStateRepository` to only:
  - `Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken ct);`
  - `Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken ct);`

Remove old members:

- `LoadPeersAsync`, `SavePeersAsync`, `LoadRelationshipsAsync`, `LoadRelayAsync`, `SaveRelayAsync`

### A.3 — Update `JsonSimulatorStateRepository`

- Implement `LoadStateAsync`:
  - Read `simulator-state.json` into DTO(s).
  - Convert DTO(s) to `SimulatorStateSnapshot`.
  - Return a fully-normalized snapshot:
    - never-null lists
    - version defaults (this is Version 1; no migration is required)
    - normalize peer connection host/port (preserving any explicitly configured values)

- Implement `SaveStateAsync`:
  - Convert `SimulatorStateSnapshot` to DTO(s) and write to `simulator-state.json`.
  - Relays are persisted inside `simulator-state.json` as part of the snapshot.
    - There must be no separate relay persistence.

- DTO update:
  - Add `List<RelayPersistenceDto> Relays` to the root simulator state DTO (`SimulatorStateDto`).
  - `LoadStateAsync` must populate snapshot `Relays` from `SimulatorStateDto.Relays`.
  - `SaveStateAsync` must write snapshot `Relays` into `SimulatorStateDto.Relays`.

Notes:

- Keep normalization logic (`NormalizePeer`) but apply it at snapshot/DTO conversion boundaries.
- Repository must not maintain cross-call mutable caches for identity ids or groups.
  - Everything required to round-trip must be in the snapshot.

### A.4 — Update `SimulatorStateService` to use snapshot contract

- Initialization:
  - Replace `LoadPeersAsync + LoadRelationshipsAsync + InitializeRelaysAsync(LoadRelayAsync...)` with one `LoadStateAsync`.
  - Hydrate:
    - peers from `snapshot.Peers` (construct `SimulatedPeerModel` + hydrate runtime store sessions)
    - relationships from `snapshot.Relationships`
    - relays from `snapshot.Relays`

- Concurrency model:
  - Replace `_peerGate` + `_relayGate` with one `_stateGate`.
  - Refactor gate usage to be idiomatic and hard to misuse:
    - Introduce a single helper that takes the lock and runs a delegate, e.g. `WithStateGateAsync(Func<Task>)` / `WithStateGateAsync<T>(Func<Task<T>>)`.
    - Ensure all public APIs that read/mutate state go through the helper (no direct `WaitAsync` scattered around).
    - Ensure freezing the snapshot for persistence is always performed under the same helper.

- Self identity id allocation:
  - On load, compute `_nextSelfIdentityId` from snapshot.
  - Ensure `SimulatedPeerModel.Freeze()` includes the peer's `SelfIdentityId`.
  - Ensure any peer creation path assigns `SelfIdentityId` exactly once.

- `SimulatedPeerModel` identity storage:
  - `SimulatedPeerModel` must store `SelfIdentityId` as a first-class property.
  - Hydration must set `SelfIdentityId` from snapshot.

- Groups:
  - `SimulatorStateService` does not mutate groups as part of Chunk A.
  - Store the loaded `Groups` list on the service (private field), and pass it back on save.
  - Do not call `LoadStateAsync` during save.

- Persistence pipeline:
  - Replace:
    - `_store.SavePeersAsync(peerSnaps, relSnaps, ...)`
    - `_store.SaveRelayAsync(...)`
    with:
    - Freeze a single `SimulatorStateSnapshot` and call `_store.SaveStateAsync(snapshot, ...)`.
  - Persist relays via the same debounced save trigger.
    - Since relays are part of the snapshot, there is no separate relay persistence pipeline.

### A.5 — Update tests

Impacted test files (from code search):

- `Desktop.Wpf.Tests/SimulatorStateStoreTests.cs`
  - Replace peer/relationship round-trip assertions with snapshot round-trip assertions.

- Repository stubs used by runtime tests:
  - `SimulatorStateServiceInitializationTests.cs` (RepositoryStub)
  - `SimulatedPeerRuntimeFinalizeTests.cs` (InMemoryRepository)
  - `SimulatedPeerRuntimeFinalizeRelayedTests.cs` (InMemoryRepository)
  - `SimulatedPeerRuntimeStandardHandshakeRelayedTests.cs` (InMemoryRepository)
  - `SimulatedPeerRuntimeServiceDecryptFailureDiagnosticsTests.cs` (InMemoryRepository)
  - Update to store a single `SimulatorStateSnapshot` and expose the last saved snapshot for assertions.

- Update assertions:
  - Tests that currently inspect `SavedPeers` / `SavedRelationships` / `SavedRelay` should now inspect:
    - `SavedSnapshot.Peers`
    - `SavedSnapshot.Relationships`
    - `SavedSnapshot.Relays`

Definition of done:

- The simulator still loads and saves state correctly.
- Sessions persist in the peer runtime store snapshot and are restored on load.
- Build succeeds and `dotnet test` passes.

### A.6 — Remove dead code introduced by the refactor

- Delete relay file persistence code paths in `JsonSimulatorStateRepository`.
- Remove relay-specific repository methods and any remaining call sites.
- Remove `_relayGate` and any relay-only persistence helpers in `SimulatorStateService`.
- Remove repository-owned caches that become redundant under the snapshot contract.

---

## Chunk B — Relay bug 1: “Next” doesn’t decrement queue counter + delivered handshake doesn’t update peer state

Outcome:

- Clicking **Next** on the Relay tab removes exactly one item from the relay queue, and **the queue counter and item list update immediately**.
- Delivering a queued relayed standard handshake (`HandshakeInitiatorHello`) causes the **target peer’s handshake UI to transition reactively** (show “Request Received”), and **does not** auto-accept until the user clicks Accept.


Important observed current behavior (must change in this chunk):

- `SimulatorRelayDeliveryService.DeliverToPeerAsync(...)` currently calls `_state.ReceiveRelayedOpaquePayloadAsync(recipientPeerId, opaqueBytes, ...)`.
- `SimulatorStateService.ReceiveRelayedOpaquePayloadAsync(...)` currently parses `HandshakeInitiatorHello` and immediately calls `ReceiveEstablishSessionFromMainAsync(...)`, creating a session and returning `EstablishSessionResponse`.
- `SimulatorRelayDeliveryService.DeliverToPeerAsync(...)` then immediately enqueues that `EstablishSessionResponse` back to the initiator (either downstream to a simulated peer or upstream to Main).

This means standard handshakes are currently *auto-accepted* on delivery, which is the opposite of the simulator UX we want.

Context / key constraint (R3):

- Domain models can be mutated on background threads.
- ViewModels must marshal reactive streams to the WPF dispatcher before binding.
- Reference: `Desktop.Wpf/r3.readme.md`:
  - For properties: call `ObserveOnCurrentSynchronizationContext()` *before* `.ToBindableReactiveProperty()`.
  - For collections: use `.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher)`.

Work (recipe):

### B.1 — Fix Relay tab queue counter not reacting to dequeue/remove

Symptom:

- Relay queue item removal happens in domain (`SimulatorStateService.DeleteRelayMessageByAckIdAsync`), but the Relay tab’s `QueueCount` can appear stuck.

Root cause:

- `QueueCount` is derived from `synchronizedQueueView.ObserveCountChanged()` but is bound without UI-thread marshalling.

Change:

- `Desktop.Wpf/Features/Simulator/SimulatedRelayQueuePanelViewModel.cs`
  - Update `QueueCount`:
    - `synchronizedQueueView.ObserveCountChanged()`
    - `.ObserveOnCurrentSynchronizationContext()`
    - `.ToBindableReactiveProperty(...)`
  - Do not touch `QueueItems`; it is already marshaled via `.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher)`.

Definition of done:

- With a non-empty relay queue visible, click **Next**.
- Confirm:
  - delivered item disappears from list
  - `QueueCount` decrements immediately
  - no WPF cross-thread exceptions

### B.2 — Make all Simulator ViewModels UI-thread-safe (required for reactive handshake UI updates)

What to fix:

- Any projection from `_model.*` reactive properties into a `BindableReactiveProperty` must include `.ObserveOnCurrentSynchronizationContext()`.

Where (minimum set for this chunk):

- `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedHandshakeStateMachineCardViewModel.cs`

Definition of done:

- When a background service changes `SimulatedPeerModel.UiState` (or any other runtime reactive property), UI updates within the next dispatcher tick.

### B.3 — Track pending inbound Standard Signal hellos inside the Peer Aggregate

The UI currently exposes a generic `InboundPending` state. We must rename this to `AwaitingUserAcceptance` and track standard Signal hellos separately from reverse-signal invites.

Required renames:

- **Rename UI state**
  - `SimulatorPeerUiState.InboundPending` -> `SimulatorPeerUiState.AwaitingUserAcceptance`
  - Update all references across WPF code, converters, tests, and any persisted snapshots accordingly.

- **Rename reverse-signal tracking field**
  - `SimulatedPeerModel.PendingCorrelationId` -> `SimulatedPeerModel.InboundReverseSignalPendingCorrelationId`

New domain model:

- Create `Desktop.Wpf/Features/Simulator/Models/SimulatedPendingStandardSignalHelloModel.cs` with:
  - `Guid RelayHostPeerId`
  - `byte[] InitiatorIdentityKeySpki`
  - `byte[] InitiatorEphemeralKeySpki`
  - `Guid SignedPreKeyId`
  - `Guid? OneTimePreKeyId`
  - `DateTimeOffset ReceivedUtc`

Add to peer model (authoritative state ownership):

- `SimulatedPeerModel` owns pending inbound standard hellos.
- Add:
  - `ObservableDictionary<string, SimulatedPendingStandardSignalHelloModel> PendingInboundStandardSignalHellosMutable`
  - Expose read-only view:
    - `IReadOnlyObservableDictionary<string, SimulatedPendingStandardSignalHelloModel> PendingInboundStandardSignalHellos`
  - Do not add a "selected initiator" property.
    - The simulator handshake UI will be updated to list each pending handshake and pass `initiatorPkhHex` as a command parameter.

Keying and update semantics:

- `InitiatorPkhHex = Convert.ToHexString(SHA256(hello.InitiatorIdentityKeySpki)).ToLowerInvariant()`.
- One pending per initiator identity key per recipient peer.
- Most recent overrides existing entry for the same initiator PKH.
- No expiration.

Implementation note:

- This data is stored on the peer aggregate (`SimulatedPeerModel.PendingInboundStandardSignalHellosMutable`) and is mutated under the simulator state gate.

### B.4 — Handshakes tab UI: list pending inbound handshakes (per peer) and accept per item

Rationale:

- Today, the Handshakes tab shows one peer card with a single `Accept` button.
- With standard Signal, a single recipient peer can have multiple pending inbound hellos (one per initiator identity PKH).
- Therefore, `Accept` must be **per pending item** (command parameter) rather than relying on a global selection property.

Existing candidate assessment:

- `Desktop.Wpf/Features/Sessions/PendingHandshakesMenuViewModel` exists but is part of the sessions UI and is already in use (sidebar/menu card). It is not suitable to reuse directly for the simulator.
- No existing simulator-specific ViewModel for listing pending inbound handshake items was found.

Plan:

- Update `Desktop.Wpf/Features/Simulator/SimulatorHandshakesTabView.xaml` peer card template to include a "Pending" section.
- Add two visual groupings in each peer card:
  - Reverse-signal invite (at most one): show when `InboundReverseSignalPendingCorrelationId != null`.
  - Standard Signal hellos (0..n): list all entries in `PendingInboundStandardSignalHellos`.
- Add a per-item `Accept` button for each pending standard hello.
  - The command must pass `initiatorPkhHex` (string) as the parameter.
  - Optional: also include a per-item `Reject`/`Dismiss` that removes that pending hello.

ViewModel changes (high-level):

- Extend `SimulatedHandshakeStateMachineCardViewModel` to expose a bindable list of pending standard hello rows.
  - Each row must include:
    - `InitiatorPkhHex`
    - `ReceivedUtc`
    - `AcceptCommand` (or reuse parent command with parameter)
    - Optional `RejectCommand`
- Prefer projecting the model’s dictionary to a UI collection using the same R3/WPF threading rule as elsewhere:
  - marshal to dispatcher before raising collection change notifications.

Definition of done:

- The Handshakes tab displays **multiple** pending standard hellos for a single recipient peer.
- Clicking Accept on a specific pending standard hello accepts **that specific** initiator (no ambiguity).

### B.5 — Delivery path: do not auto-accept standard hellos

Change required:

- In `SimulatorRelayDeliveryService.DeliverToPeerAsync(...)`:
  - If `opaqueBytes` parses as `HandshakeInitiatorHello`:
    - Do **not** call `_state.ReceiveRelayedOpaquePayloadAsync(...)`.
    - Instead call a new state service API:
      - `Task UpsertPendingStandardSignalHelloAsync(Guid recipientPeerId, Guid relayHostPeerId, HandshakeInitiatorHello hello, DateTimeOffset receivedUtc, CancellationToken ct)`
    - That API must (under `_stateGate`):
      - upsert the pending model into the recipient peer’s `PendingInboundStandardSignalHellosMutable`
      - set the recipient peer’s `UiState = AwaitingUserAcceptance`
      - update `PendingInboundStandardSignalHellosMutable[initiatorPkhHex] = <model>`
      - set `InboundReverseSignalPendingCorrelationId = null`
    - Emit diagnostics: `HandshakeStateTransition` with `contextTag = "StandardSignalPending"`.
  - If it is not a hello, continue existing behavior.

### B.6 — Acceptance: finalize standard signal hello and route response

- Create:
  - `Task<bool> TryAcceptPendingStandardSignalHelloAsync(Guid recipientPeerId, string initiatorPkhHex, CancellationToken ct)`

Under `_stateGate`:

- 1) Look up the recipient `SimulatedPeerModel`.
- 2) Look up and remove the pending hello from `SimulatedPeerModel.PendingInboundStandardSignalHellosMutable`.
  - If not found, return `false`.
- 3) Map hello -> `EstablishSessionRequest` and call `ReceiveEstablishSessionFromMainAsync(...)`.
  - Important: do **not** refactor `ReceiveEstablishSessionFromMainAsync` in this chunk.
- 4) Update the recipient peer UI state to Established if (and only if) no other inbound pending items exist.
  - If other pendings remain (standard hellos and/or reverse-signal invite), keep `UiState = AwaitingUserAcceptance`.

Release `_stateGate`.

Outside the lock:

- Route the resulting `EstablishSessionResponse` back to the initiator using `TryGetPeerIdByIdentityPkhAsync`.
  - If local, call `EnqueueRelayDownstreamToPeerAsync`.
  - If remote, call `EnqueueRelayUpstreamToMainAsync`.

ViewModel wiring requirement (no discriminator enum):

- Reverse-signal invite acceptance remains the peer-card Accept behavior (single correlation id).
- Standard Signal acceptance is per pending item in the list UI (B.4) and must call:
  - `TryAcceptPendingStandardSignalHelloAsync(recipientPeerId, initiatorPkhHex, ct)`

### B.7 — Tests

Update/add tests to cover:

- **Relay queue UI**
  - Next decrements count (reactive marshalling fix).
- **Standard hello pending + accept**
  - Delivering `HandshakeInitiatorHello` via relay:
    - does not enqueue an immediate response
    - sets recipient peer `UiState=AwaitingUserAcceptance`
    - adds/overwrites `SimulatedPeerModel.PendingInboundStandardSignalHellos[initiatorPkhHex]`
  - Accepting standard hello:
    - enqueues an `EstablishSessionResponse` back to initiator (upstream or downstream)
    - marks recipient established

- **Handshakes tab list UI (smoke)**
  - When multiple pending standard hellos exist for a peer, the tab renders multiple rows.
  - Clicking Accept on a specific row calls accept for that row’s `initiatorPkhHex`.

---

---
## Chunk I — Notification badge + default focus behavior for Connection Management button

Outcome:

- Header button shows count badge and pulses when inbound requests exist.
- Opening dialog defaults to Tab 1 if inbound exists else Tab 2.

Work (recipe):

- Badge count
  - Ensure the header “Connection Management / Add peer” button displays the inbound pending count.
  - The count must be sourced from shared state (store-level), not recomputed ad-hoc in the view.
  - It’s acceptable to *project* the store count into a ViewModel property for binding.

- Pulse / attention behavior
  - When the inbound pending count transitions:
    - `0 -> 1` (first pending arrives)
    - or `n -> n+1` (more pendings arrive)
  - the header badge should pulse to draw attention.
  - Keep this purely UI behavior (no domain logic).

- Default tab selection when opening Connection Management
  - At dialog open time, choose the initial tab based on whether inbound pending exists:
    - inbound pending exists: default to the Incoming Signals tab
    - otherwise: default to the Network Search tab
  - This decision should be made once when the dialog is created/opened (not continuously).

Definition of done:

- Badge count matches `ISecureChannelsStore.PendingInboundCount`.
- Badge is visible and pulses whenever inbound pending exists.
- Opening Connection Management defaults to Incoming Signals tab when pending exists, otherwise defaults to Network Search.

Minimal tests:

- Unit test that a VM projection of `PendingInboundCount` updates when store pending inbound changes.

Implementation note:

- This chunk may already be complete:
  - `MatButton.NotificationCount` has built-in pulsing behavior when the count increases.
  - The sidebar button already binds its `NotificationCount` to a pending-handshake count.
  - `ConnectionManagementDialogViewModel.InitializeAsync` already selects the Incoming tab when pending invitations exist.
- If you want stricter alignment to the definition of done, the remaining delta would be to bind the badge count *directly* (or via a projection) to `ISecureChannelsStore.PendingInboundCount` and to base the default tab decision on the same store count rather than any dialog-local enumeration.

---

## Chunk J — Uplink Inspector side panel (Direct vs Relay topology)

Outcome:

- “UPLINK” button toggles a side panel showing connection topology:
  - Direct: Main <-> Peer
  - Relayed: Main -> Relay -> Peer

Work (recipe):

- Authoritative requirements
  - Follow `design/main-window.md` “Uplink Inspector UI” section.
  - Uplink is only meaningful for an active channel header; it is disabled for Pending.

- State ownership
  - Topology/route provenance is shared, notification-driven state:
    - Source: fields on `SecureChannelModel` populated by projection (Chunk H).
    - Do not store route provenance inside a scoped VM.
  - Inspector visibility/toggle is ephemeral UI state:
    - OK to keep in the right pane VM as `BindableReactiveProperty<bool> IsUplinkOpen`.
    - Alternatively keep in `SelectedSecureChannelStateModel` if you want it preserved per-channel.

- Model shape needed for UI
  - Ensure `SecureChannelModel` exposes enough to render topology:
    - Minimal for Chunk J:
      - Route kind: Direct vs Relay (and optionally Group)
      - Relay identifier/name when relayed
    - If `SecureChannelKind` already distinguishes `Relay`, use it.
    - Otherwise add explicit route fields as described in Chunk H (e.g., `RouteText`, `RelayPeerId`).

- ViewModel projection
  - Implement an inspector VM that projects from the selected channel (store) + the per-channel ephemeral state:
    - File: `Desktop.Wpf/Features/Sessions/UplinkInspectorViewModel.cs`
    - Inputs:
      - `SelectedChannelModel`
      - `ISecureChannelsStore`
      - Optional: `SelectedSecureChannelStateCache` if preserving open/closed per channel.
    - Outputs:
      - `BindableReactiveProperty<bool> IsOpen`
      - `BindableReactiveProperty<string> TopologyText` (or structured nodes)
      - `BindableReactiveProperty<bool> IsEnabled` (false when Pending/Failed)

- UI implementation
  - Add a header “UPLINK” button to the active channel header in the right pane.
    - If the right pane is a `ContentControl` with templates, add button within the Active template.
  - Add the sliding side panel:
    - Use a `Grid` column or overlay `Border` with animation when `IsOpen` changes.
    - Keep visuals simple for the first pass:
      - Direct: `[Operator Node] <====> [Peer Name]`
      - Relayed: `[Operator Node] ----> [Relay] ----> [Peer Name]`

Definition of done:

- Active direct channel shows direct topology.
- Active relayed channel shows relay topology with relay identifier.
- Pending channel disables uplink.

Minimal tests:

- Unit test for inspector VM mapping from shared model route fields to output text/state.

---

## Chunk K — Session reset / recovery action

Outcome:

- Kebab menu includes “Reset Secure Session”.
- Triggers a new outbound **Reverse-Signal invitation** behind the scenes while keeping channel history.

Work (recipe):

- Authoritative requirements
  - Follow `design/main-window.md` “Session Reset / Recovery” section.
  - Goal is to heal a desynchronized ratchet without losing the channel row/history.

- Identify existing reverse-signal primitives to reuse
  - Invite construction:
    - File: `Percolator.Application/Network/MainReverseSignalInviteFactory.cs`
    - API: `IMainReverseSignalInviteFactory.CreateInvite()`
    - Side effects:
      - Persists a `SentInvitation` keyed by `request_correlation_id`.
      - Persists a per-invite signed pre-key (needed later for inviter finalization).
  - Inbound invite queueing:
    - File: `Percolator.Application/Network/EstablishDirectSessionService.cs`
    - Publishes `PendingSessionCreatedNotification`.
  - Accepting invite:
    - File: `Percolator.Application/Network/ApprovePendingSessionCommand.cs`
    - Publishes `SecureSessionCreatedNotification` when acceptor establishes.
    - Delivers `InviteHandshakeResponse` via `IInviteHandshakeResponseDeliveryService`.
  - Inviter finalization:
    - File: `Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`
    - Correlates response by `request_correlation_id` to `SentInvitation` and publishes `SecureSessionCreatedNotification`.

- State ownership
  - “Reset in progress” is primarily an ephemeral UI concern scoped to a channel:
    - Store it in `SelectedSecureChannelStateModel` as a reactive field:
      - Example: `ReactiveProperty<bool> IsReestablishing`
      - Example: `ReactiveProperty<string?> ReestablishingText`
    - This keeps the main shared store focused on notification-driven channel facts.
  - The channel row itself MUST remain the authoritative `SecureChannelModel` from the store.
    - Do not delete and recreate the channel just to reset.

- Command surface (ViewModel)
  - Add a kebab menu affordance in the Active channel header UI:
    - “Reset Secure Session” item.
  - Implement a VM command that triggers reset:
    - File: right-pane/header VM (from Chunk G)
    - Behavior:
      - Sets `SelectedSecureChannelStateModel.IsReestablishing = true`.
      - Sends an application command via `IMediator.Send(...)` (or invokes an existing service) to initiate a new reverse-signal invite.
      - Does not directly mutate `SecureChannelsStore`.

- Application-layer behavior to trigger new invite
  - Prefer an explicit application command (so it is testable and does not couple UI to services):
    - Example: `ResetSecureSessionCommand(SecureChannelKey channelKey | PeerId remotePeerId, RouteChoice route)`.
  - Implementation must:
    - Build a fresh reverse-signal invite via `IMainReverseSignalInviteFactory.CreateInvite()`.
    - Deliver it to the remote peer using the same routing primitives as outbound initiation (Chunk H).
    - Ensure an outbound pending row exists and will migrate to active via Chunk F.6 correlation.

- UI behavior
  - While reset is pending:
    - Show a temporary banner/system message in the right pane: “Re-establishing secure connection…”.
    - Disable sending or mark messages as queued (placeholder acceptable).
  - When the new session is established:
    - Clear `IsReestablishing`.
    - Channel remains selected; UI returns to Active.

Definition of done:

- “Reset Secure Session” is available for an active 1:1 channel.
- Triggering reset creates a new outbound pending action and shows “Re-establishing…” immediately.
- When the new session completes, the channel returns to Active without losing the channel row.

Minimal tests:

- Unit test: triggering reset sets `IsReestablishing` and invokes the application command.
- Integration smoke: completing the reverse-signal flow clears `IsReestablishing` and results in an active session.

---

## Chunk L — Visual fidelity pass (match screenshots)

Outcome:

- Style and layout improvements to match `design/*.png`:
  - tab strip styling
  - button states
  - badge colors
  - list item layout

Work (recipe):

- Scope/constraints
  - This chunk is UI-only; do not change application logic or store/projection semantics.
  - Prefer consolidating WPF styles/templates over adding per-view ad-hoc styling.

- Authoritative references
  - Visual targets: `Desktop.Wpf/design/*.png`
  - Behavior targets (do not regress): `Desktop.Wpf/design/main-window.md`

- Primary files to touch
  - List visuals:
    - `Desktop.Wpf/Features/Sessions/SessionsSidebarView.xaml`
    - `Desktop.Wpf/Features/Sessions/SessionsSidebarViewModel.cs` (only if binding surface needs minor extensions)
    - `Desktop.Wpf/Features/Sessions/SecureChannelListItemViewModel.cs` (only if additional bindable display props are needed)
  - Shared styles/resources:
    - `Desktop.Wpf/Shared/Theme/Styles.xaml`
    - `Desktop.Wpf/Shared/Theme/Typography.xaml`
    - `Desktop.Wpf/Shared/Theme/Colors.xaml` (if present)
    - `Desktop.Wpf/Shared/Theme/Icons.xaml`
  - Shared controls:
    - `Desktop.Wpf/Shared/Controls/MatButton.xaml`
    - `Desktop.Wpf/Shared/Controls/MatChip.xaml`
    - `Desktop.Wpf/Shared/Controls/InitialsAvatar.xaml`

- Concrete UI checklist
  - Tab strip styling (Connection Management and any right-pane tab usage)
  - Button states:
    - hover/pressed/disabled visuals match screenshots
  - Badge colors and shapes:
    - unread badge
    - channel tech badge (DIRECT/RELAY/GROUP/PENDING/FAILED)
    - add-peer notification badge
  - List item layout:
    - avatar alignment + online dot
    - name/snippet typography
    - timestamp alignment

Definition of done:

- Screens match `design/*.png` within reasonable tolerance.
- No behavioral regressions in filtering, selection, pending badge, and store-driven updates.

---

## Chunk M — Cleanup / remove legacy handshake UI and orphaned code

Outcome:

- The codebase has a single, clear handshake entry point: **Connection Management**.
- Legacy UI and mappings that are no longer used are removed to avoid confusion and bit-rot.

Work (recipe):

- Inventory and delete legacy handshake UI
  - Remove legacy NewHandshake dialog artifacts if no longer used:
    - `Desktop.Wpf/Features/Sessions/NewHandshakeDialogWindow.xaml`
    - `Desktop.Wpf/Features/Sessions/NewHandshakeDialogWindow.xaml.cs`
    - `Desktop.Wpf/Features/Sessions/NewHandshakeDialogViewModel.cs`
  - Remove any related tests that only exist for the deleted UI.

- Remove DI registrations and view mappings
  - DI:
    - File: `Desktop.Wpf/App.xaml.cs`
    - Remove any `services.Add...<NewHandshakeDialogViewModel>()` style registrations.
  - Window/view mappings:
    - File: `Desktop.Wpf/Shared/Theme/ViewMappings.xaml` and/or `Desktop.Wpf/Shared/Windowing/ViewMappings.xaml` (remove from the one actually used)
    - Remove mappings for legacy dialog.

- Remove call sites
  - Search for and remove remaining invocations:
    - `ShowFor<NewHandshakeDialogViewModel>`
    - `ShowFor<NewHandshakeDialogWindow>`
  - Ensure they route to:
    - `ConnectionManagementDialogViewModel`

- Remove legacy event listeners/invalidation shims
  - Search for MediatR listeners or event buses that exist only to refresh the legacy handshake UI.
  - Candidate based on current code:
    - `Desktop.Wpf/Features/Sessions/ConnectionManagementInboxEventListener.cs` (calls `IMainInvitationInboxEvents.NotifyChanged()` on `PendingSessionCreatedNotification`)
  - Replace/retire them in favor of store-driven state (Chunk F) where applicable.

Definition of done:

- Only one handshake entry point remains in the UI: Connection Management.
- No references remain to `NewHandshakeDialog*` types.
- Build passes.

---

## Risks / tricky areas

- Debounced filtering and dispatcher scheduling: avoid hard UI-thread dependencies in core services.
- Preserving route provenance across handshake lifecycle (so UI can display “Arriving via …” accurately).
- Avoid mixing “pending inbound” with the Secure Channels list (anti-spam requirement).

---

## Relay Bug 1

### relay-setup-1 (repro)

- Start from a brand new DB file (no peers).
- In the Simulator window:
  - Create 2 simulated peers.
  - Mark exactly 1 of them as relay-capable (call it **Relay**).
  - Add the non-relay peer (call it **Target**) as an active session on the Relay.
  - From Target, publish a standard pre-key bundle to Relay.
- In the Main window:
  - Establish a direct connection/session to Relay.
  - Open Connection Management -> Network Search.
  - Choose route mode: **relay**.
  - Select Relay as relay host.
  - Enter Target PKH and click **Fetch Pre-Key Bundle & Initiate**.

#### Expected behavior

- Main requests Target pre-key bundle from Relay (GetPreKeyBundleRequest).
- Relay responds with GetPreKeyBundleResponse (bundle present).
- Main then creates a HandshakeInitiatorHello and sends an EnqueueOpaqueMessageRequest to Relay.
- Relay enqueues the hello in its relay queue under routing key == Target PKH.
- Simulator UI (Relay tab) shows a new queued relay message on the Relay peer.

