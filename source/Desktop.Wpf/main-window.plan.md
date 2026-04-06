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

#### What we have checked / verified

- Publish to relay host works; the pre-key bundle is stored in the relay host’s PublishedBundles collection.
- Fetch-by-PKH from relay host works end-to-end:
  - The request reaches the correct simulated peer runtime (simulatedPeerId matches the relay host peer id).
  - The request PKH matches the stored bundle key (byte-for-byte).
  - The relay host responds with a response payload containing the pre-key bundle.

Concrete debug values (from a known-good run):

- relayHostPeerId:
  - `a8d433c8-7741-472b-ab52-c7ebebd920f4`
- logicalOwnerPeerId (Target simulated peer id that published bundle):
  - `64d5433b-7a86-4195-b106-ec98f1d2c1ad`
- recipientPublicKeyHash / requested PKH (hex):
  - `C147ED93237B62FFADAEF2480C47B1D9CFEAD154556F3F8BAA851EFCE098DEA5`
- Relay host runtime ingress confirmation:
  - `simulatedPeerId` for GetPreKeyBundleRequest == `a8d433c8-7741-472b-ab52-c7ebebd920f4`
  - `getReq.PublicKeyHash` hex == `C147ED...DEA5`

### Next verification steps

The remaining suspected failure is in the **post-bundle enqueue** path (Main -> Relay EnqueueOpaqueMessageRequest -> Relay queue persistence/UI).

- Verify Main actually sends the enqueue message:
  - Breakpoint: `ConnectionManagementDialogViewModel.ExecuteNetworkSearchAsync` at the second `_transport.SendMessageAsync(...)` that sends `EnqueueOpaqueMessageRequest`.
  - Capture: relayHostPeerId, direct session id, targetPkh hex, and size of `cipherMq`.

- Verify transport routing hits the simulator interceptor for the enqueue send:
  - Breakpoint: `SimulatorOutboundInterceptor.TryDeliverOpaqueMessage(...)`.
  - Confirm: `TryResolveSimulatedPeerId(...) == true` and resolved peer id == relayHostPeerId.

- Verify relay peer runtime parses the enqueue envelope and calls state enqueue:
  - Breakpoint: `SimulatedPeerRuntime.ReceiveOpaqueMessageFromMainAsync` inside the `EnqueueOpaqueMessageRequest` branch.
  - Confirm: `RecipientPublicKeyHash` == targetPkh and `MessageBlob` length > 0.
  - Breakpoint: `SimulatorStateService.EnqueueRelayOpaqueAsync(...)` and confirm queue count increments.

- If queue count increments but UI remains unchanged:
  - Investigate `SimulatorRelayTabViewModel` / relay panel refresh behavior and dispatcher affinity.