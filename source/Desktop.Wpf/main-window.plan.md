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

### Relay Bug 1 — Main window does not update after relayed standard handshake completes

#### Observed symptom

- When initiating a **relayed** standard handshake from Main (Fetch Pre-Key Bundle & Initiate) and then processing relay queue messages + accepting the handshake on the simulated peer, the **Main window does not show a new/active peer connection**.

#### Code path (from Simulator “Relay Queue” tab → back into Main)

- **UI button**
  - `Desktop.Wpf/Features/Simulator/SimulatedRelayQueuePanelViewModel.cs`
    - `NextCommand` → `DeliverNextAsync` → `DeliverItemAsync(...)`

- **Relay message routing decision**
  - If the relay queue item has **no `targetPkh`**, it is treated as **upstream to Main**:
    - `DeliverItemAsync(...)` calls:
      - `ISimulatorStateService.DeliverRelayUpstreamToMainByAckIdAsync(...)`

- **Simulator delivers upstream message to Main’s gRPC handler**
  - `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
    - `DeliverRelayUpstreamToMainByAckIdAsync(...)` wraps the relay payload bytes into:
      - `InternalEnvelope { RelayOpaqueEnvelope { OpaquePayload = ... } }`
    - Encrypts that envelope to the **direct session between RelayHost ↔ Main**
    - Then calls `Percolator.Application.Network.PercolatorMessageService.DeliverOpaqueMessage(...)`

- **Main gRPC entrypoint**
  - `Percolator.Application/Network/PercolatorMessageService.cs`
    - `DeliverOpaqueMessage(...)` → `_messageIngress.DeliverOpaqueAsync(...)`

- **Main internal envelope dispatch**
  - `Percolator.Application/Network/DeliverOpaqueMessageHandler.cs`
    - Decrypts session message, parses `InternalEnvelope`
    - If case is `RelayOpaqueEnvelope`:
      - `_mediator.Send(new ProcessRelayedOpaquePayloadCommand(...))`

- **Relayed payload processing (this is where the standard handshake response is finalized)**
  - `Percolator.Application/Network/Handshake/ProcessRelayedOpaquePayloadCommand.cs`
    - `TryHandleNonSessionPayloadAsync(...)` attempts to parse the relayed bytes as:
      - `EstablishSessionResponse`
    - On success it calls:
      - `IInitiatorFinalizeService.TryFinalizeFromEstablishSessionResponseAsync(...)`

- **Initiator finalization should publish the “session created” notification**
  - `Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`
    - `TryFinalizeFromEstablishSessionResponseAsync(...)`
      - Adds initiator session via `_sessions.AddAsync(...)`
      - Publishes `SecureSessionCreatedNotification(...)`

- **Desktop.Wpf listens to that notification and should refresh UI state**
  - `Desktop.Wpf/Features/Sessions/Handlers/PeerConnectionStateUpdateHandlers.cs`
    - Handles `SecureSessionCreatedNotification` by calling `_coordinator.TriggerReload()`
  - `Desktop.Wpf/Features/Sessions/PeerConnectionReloadCoordinator.cs`
    - Debounces and reloads via `IPeerConnectionQueries`
    - Updates `PeerConnectionStateService.Connections`

#### Where it breaks (most likely)

- `PeerConnectionReloadCoordinator.ReloadCoreAsync(...)` is a no-op unless:
  - `PeerConnectionStateService.ActiveSelfIdentityId.HasValue == true`
- `ActiveSelfIdentityId` is set only when `PeerConnectionStateService.InitializeAsync(...)` is called.
- In `Desktop.Wpf/Features/Shell/ShellViewModel.cs`, initialization currently does:
  - `int.TryParse(domainIdentity.Id.ToString(), out var selfIdentityId)`
- `domainIdentity.Id` is a `SelfId` value object; its `ToString()` is **not guaranteed to be parseable as an `int`**.
  - If parsing fails, `PeerConnectionStateService.InitializeAsync(...)` is never called.
  - Result: the UI state service never loads initial state, and later MediatR notifications trigger reloads that immediately return.

#### Suggested fixes

- **Fix the identity ID plumbing (high confidence fix)**
  - In `Desktop.Wpf/Features/Shell/ShellViewModel.cs`, replace the `int.TryParse(...)` block with direct access to the value:
    - `await _peerConnectionStateService.InitializeAsync(domainIdentity.Id.Value, CancellationToken.None);`
  - This ensures `ActiveSelfIdentityId` is always populated, making all subsequent reload triggers effective.

- **Add a guard + log to make this failure mode obvious**
  - If keeping any conditional, log when initialization is skipped and include `domainIdentity.Id`.

- **(Optional) Improve relayed connection UI fidelity**
  - `PeerConnectionQueries.LoadAllConnectionsAsync` currently sets `RelayHostPeerId: null` even when `status == Relay`.
  - If the Uplink/route inspector is expected to show topology, `RelayHostPeerId` should be populated from routing/profile state.

Chunk B: Persistence Extension for MainUplinkSessionId
Objective: Extend the simulator's disk persistence pipeline to serialize and deserialize the newly added MainUplinkSessionId property, ensuring the established gRPC uplink survives application restarts and state snapshots.

Step 1: Update the Snapshot DTO (PeerStateSnapshot.cs)

Locate the PeerStateSnapshot record definition (likely in Desktop.Wpf.Features.Simulator.Models).

Add a new property to the record signature: Guid? MainUplinkSessionId.

Place this property adjacent to the existing PendingStandardHandshakeToMainTemporarySessionId property to maintain logical grouping.

Step 2: Update the Domain Model Constructor (SimulatedPeerModel.cs)

Modify the SimulatedPeerModel constructor signature.

Add a new optional parameter: SessionId? mainUplinkSessionId = null.

Inside the constructor body, locate the initialization of _mainUplinkSessionId.

Change the initialization from a hardcoded null to use the injected parameter: _mainUplinkSessionId = new ReactiveProperty<SessionId?>(mainUplinkSessionId);.

Step 3: Update Serialization / Freeze Logic (SimulatedPeerModel.cs)

Locate the Freeze() method inside SimulatedPeerModel.

Update the instantiation of the PeerStateSnapshot record to include the new property.

Extract the underlying Guid from the SessionId value object. The assignment should look exactly like this: MainUplinkSessionId: _mainUplinkSessionId.Value?.Value.

Step 4: Update Deserialization / Rehydration (SimulatorStateService.cs)

Locate the CreatePeerFromSnapshot(PeerStateSnapshot snap) method inside SimulatorStateService.cs.

Extract the saved Guid? from the snapshot and convert it back into a SessionId value object.

Add the parsed value to the SimulatedPeerModel instantiation mapping.

Implementation detail: ```csharp
var mainUplinkSessionId = snap.MainUplinkSessionId.HasValue
? new SessionId(snap.MainUplinkSessionId.Value)
: (SessionId?)null;

Pass mainUplinkSessionId into the SimulatedPeerModel constructor call.

## Chunk C — Clean Architecture: Fix Relay Routing via Topology Inference

**Context & AI Instructions:**
Currently, when a simulated peer accepts a relayed handshake, the response fails to route back to Main because the simulator is searching for a hardcoded `MainNodeSentinelPeerId`. Main uses ephemeral IDs, so this fails.
Instead of adding new state to the domain models to track this, we will use Topology Inference: Main is the only external node that connects to the Simulator. Therefore, any session whose `RemotePeerId` is NOT in the Simulator's known peer list is the uplink to Main.

You will also clean up technical debt by deleting an unused property that was abandoned during architectural review.

Execute the following steps exactly.

#### Step 1: Rip out unused technical debt from the Peer Model
**File:** `Desktop.Wpf/Features/Simulator/SimulatedPeerModel.cs`

We are abandoning the `InboundMainUplinkSessionId` property. You must delete it completely.
1. Delete the field: `private readonly ReactiveProperty<SessionId?> _inboundMainUplinkSessionId;`
2. Delete it from the constructor parameters.
3. Delete the constructor assignment: `_inboundMainUplinkSessionId = new ReactiveProperty<SessionId?>(inboundMainUplinkSessionId);`
4. Delete the public property: `public ReadOnlyReactiveProperty<SessionId?> InboundMainUplinkSessionId => _inboundMainUplinkSessionId;`
5. Delete its cleanup from `ClearRuntimeState()` and `Dispose()`.
6. Remove `InboundMainUplinkSessionId: _inboundMainUplinkSessionId.Value?.Value` from the `Freeze()` method return object.

*(Note: If your compiler complains about `PeerStateSnapshot` missing a parameter in `Freeze()`, simply remove `Guid? InboundMainUplinkSessionId` from the `PeerStateSnapshot` record definition in `PeerStateSnapshot.cs` as well).*

#### Step 2: Implement Topology Inference for Relay Routing
**File:** `Desktop.Wpf/Features/Simulator/SimulatorRelayTabViewModel.cs`

1. Delete the sentinel ID constant entirely:
```csharp
private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");
```

2. Rewrite the `GetRelayHostToMainSessionIdAsync` method to dynamically resolve the uplink by querying for the most recent external session:
```csharp
    private async Task<SessionId?> GetRelayHostToMainSessionIdAsync(Guid relayHostPeerId)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return null;

        // Snapshot the known simulated peers to prevent InvalidOperationException during enumeration
        var knownSimulatedPeerIds = _state.Peers.Select(p => p.PeerId).ToHashSet();

        // Topology Inference: Main connects with an ephemeral ID. Therefore, any session 
        // where the RemotePeerId is NOT in the simulator's sandbox list is the uplink to Main.
        // We order by CreatedAt descending to ensure we get the active session if Main reconnects.
        var uplinkSession = peer.Sessions
            .Select(kv => kv.Value)
            .Where(s => !knownSimulatedPeerIds.Contains(s.RemotePeerId.Value))
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefault();

        return uplinkSession?.Id;
    }
```

#### Definition of Done:
1. `SimulatedPeerModel` no longer contains the phrase `InboundMainUplinkSessionId`.
2. `SimulatorRelayTabViewModel` no longer references `MainNodeSentinelPeerId`.
3. `GetRelayHostToMainSessionIdAsync` successfully uses the topology inference query to return the session.

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
- In the Simulator window:
  - on the relay tab, there should be a new message in the relay queue. the queue count should drop to zero
  - press "next" to process the message in the queue
  - on the handshakes tab, there should be a new pending handshake - click "accept"
  - on the relay tab there should be a new message in the relay queue
  - press "next" to process the message in the queue

#### Expected behavior

- Main - when **Fetch Pre-Key Bundle & Initiate** is clicked to initiate a session with a relayed peer, a new peer connection should appear in the main window - but instead nothing appears
- Main - when the relayed peer accepts the pending handshake, the peer connection should become active

