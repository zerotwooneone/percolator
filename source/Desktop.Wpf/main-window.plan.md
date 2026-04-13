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

## Chunk A - Architectural Fix: Introduce Application-Level Query Service and Peer Connection State Service

### The Problem (Concurrency Exception & Guideline Violations)
The `SecureChannelsProjection` was built as a `Scoped` MediatR event handler. This means it shares the exact same `PercolatorDbContext` as the domain command that triggers it (e.g., `HandleHandshakeResponderHelloCommand`). When the projection drops an event onto a background Rx `Subject` and returns, the domain command continues executing while the background Rx pipeline concurrently queries the DB, causing EF Core to crash with a concurrency exception.

Beyond the bug, `SecureChannelsProjection` violates the application's Domain-Driven Design and R3 guidelines. It directly injects **six** disparate domain-level repositories (`ISessionRepository`, `IPeerIdentityRepository`, `IDirectSessionRepository`, `IPendingHandshakeQueries`, `IPreHandshakeSessionStore`, `ISentInvitationRepository`) to manually stitch together UI models. This leaks heavy domain complexity into the UI layer and breaks the "Pure Service / Stateless Repository" boundary. Furthermore, the concept of a "Secure Channel Repository" violates DDD, because "Secure Channel" is not an aggregate root in the system—it's a synthetic UI construct.

### The Architectural Shift: "Peer Connection State"
The concept we are modeling for the UI is the **state of a connection/handshake with a peer**. The primary purpose of an X3DH session is establishing a secure pipeline for chat, peer discovery, or file transfer. 

We will shift terminology from "Secure Channel" to **Peer Connection**. 

### Important AI Implementation Rules for this Chunk:
1. **Documentation Context:** When implementing these steps, you MUST adhere to the patterns and constraints defined in:
   - `@source/Desktop.Wpf/r3.readme.md` (Domain services, TimeProvider, ObservableList, thread safety)
   - `@source/Desktop.Wpf/wpf.readme.md` (WPF thread bridging, CreateView, ViewModel disposal)
   - `@source/unit-testing.md` (NUnit, Moq, FluentAssertions, FakeTimeProvider)
2. **File Deletion/Renaming Constraint:** If a step requires renaming or deleting an existing class, file, or interface, **the AI MUST NOT perform the deletion/renaming itself**. Instead, the AI must explicitly **ASK THE USER** to perform the rename/delete action, providing the exact file paths and names. You may create *new* files alongside the old ones to transition bindings, but the user must delete the old ones.
3. **Unit Testing:** Every new class containing logic (Queries, Services, ViewModels) MUST have an accompanying minimal unit test file as outlined in `@source/unit-testing.md`.

---

### Sub-Chunk A.1: Revert `RatchetKeyIndexAdapter` DbContext Hack
**Goal:** Restore the domain repository to its proper scoped state.
- **Task:** Edit `RatchetKeyIndexAdapter.cs`. Remove `IServiceScopeFactory`. Inject `PercolatorDbContext` directly via the constructor and use it directly for `TryResolveAsync` and `UpsertAsync`.
- **Validation:** Ensure existing unit tests for session indexing still compile and pass.

### Sub-Chunk A.2: Define Query Models & `IPeerConnectionQueries` Interface
**Goal:** Create the read-only CQRS boundary for the UI.
- **Task:** Create a new file `PeerConnectionStateSnapshot.cs` in `Desktop.Wpf`. It should be an immutable `record` representing the connection state (ConnectionId, PeerId, DisplayName, Status, RelayHostPeerId, LastActivityUtc).
- **Task:** Create an immutable `PendingInboundSnapshot.cs` record for incoming unaccepted handshakes.
- **Task:** Create `IPeerConnectionQueries.cs` defining:
  - `Task<IReadOnlyList<PeerConnectionStateSnapshot>> LoadAllConnectionsAsync(int selfIdentityId, CancellationToken ct)`
  - `Task<IReadOnlyList<PendingInboundSnapshot>> LoadPendingInboundAsync(CancellationToken ct)`
- **Docs Ref:** Aligns with the "Domain Snapshot Pattern" in `@source/Desktop.Wpf/r3.readme.md`.

### Sub-Chunk A.3: Implement `PeerConnectionQueries` (CQRS)
**Goal:** Move complex DB aggregation out of the background projection.
- **Task:** Create `PeerConnectionQueries.cs` implementing `IPeerConnectionQueries`. 
- **Task:** Migrate the complex data-stitching logic currently found inside `SecureChannelsProjection.ReloadAsync` into these new query methods. You may inject the 6 domain repositories (`ISessionRepository`, `IPeerIdentityRepository`, etc.) into this class, as it will be safely resolved within its own short-lived scope.
- **Testing:** Create `PeerConnectionQueriesTests.cs` using Moq to mock the underlying repositories and verify that the correct snapshots are returned (see `@source/unit-testing.md`).

### Sub-Chunk A.4: Create `PeerConnectionStateService` (R3 Orchestrator)
**Goal:** Replace the faulty projection with a thread-safe R3 Singleton Service.
- **Task:** Create `PeerConnectionStateService.cs` as a Singleton.
- **Task:** It must own `ObservableList<PeerConnectionModel>` and `ObservableList<PendingInvitationModel>`. Expose them as `IReadOnlyObservableList` (Reference: `@source/Desktop.Wpf/r3.readme.md`).
- **Task:** It must implement MediatR `INotificationHandler` for `SecureSessionCreatedNotification`, `PendingSessionCreatedNotification`, `PendingSessionRemovedNotification`, and `SentInvitationUpsertedNotification`.
- **Task:** Handle events by dropping a signal on a `Subject<Unit>`. Use `.Debounce(TimeSpan.FromMilliseconds(150), TimeProvider.System)` to trigger a private `ReloadAsync` method. 
- **Task:** Inside `ReloadAsync`, wrap the work in `using var scope = _scopeFactory.CreateScope();`, resolve `IPeerConnectionQueries`, await the snapshots, and safely mutate the `ObservableList`s inside a `SemaphoreSlim` lock.
- **Testing:** Create `PeerConnectionStateServiceTests.cs`. Inject a `FakeTimeProvider`, trigger notifications, advance time by 150ms, and verify the `ObservableList` updates correctly (Reference: `@source/Desktop.Wpf/r3.readme.md` TimeProvider section).

### Sub-Chunk A.5: UI Binding Updates & Cleanup Prompt
**Goal:** Wire the UI to the new service and remove the old architecture.
- **Task:** Update `App.xaml.cs` to register `PeerConnectionQueries` (Scoped), `PeerConnectionStateService` (Singleton), and map the MediatR notifications to the new service.
- **Task:** Update ViewModels (`SelectedChannelPaneViewModel`, `ConnectionManagementDialogViewModel`, etc.) to inject `PeerConnectionStateService` instead of `SecureChannelsStore`. Update their observable bindings using `.CreateView()` and `.ToNotifyCollectionChanged()` (Reference: `@source/Desktop.Wpf/wpf.readme.md`).
- **Task:** **STOP AND ASK THE USER** to physically delete `SecureChannelsProjection.cs`, `SecureChannelsStore.cs`, `SecureChannelModel.cs`, and any corresponding test files. Wait for user confirmation before proceeding to the next chunks.

### Definition of Done for Chunk A
- Domain complexity is hidden behind the `IPeerConnectionQueries` CQRS boundary.
- `PeerConnectionStateService` acts as a true R3 Singleton orchestrator managing in-memory collections without DB concurrency crashes.
- All dependencies are properly scoped and thoroughly unit-tested. 


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

