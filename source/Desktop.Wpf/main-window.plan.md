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

## Chunk A — Establish new Main Window interaction surfaces (shell wiring)

Outcome:

- A **Connection Management** button in the Main Window header (UserPlus icon / similar).
- A new dialog window (modal) can be opened and closed.
- The old “Add peer” context menu entry point is removed/disabled and replaced by opening the dialog.

Work:

- Add a `ConnectionManagementDialog` window + view model.
- Add a command on the Main shell/session screen to open the dialog.
- Add a minimal dialog layout with a tab strip:
  - Incoming Signals
  - Network Search
  - Import Token
- Reference layout/styling targets:
  - `design/connection-management.incoming.png` (tab strip + empty state)
  - `design/connection-management.addPeer.png` (Network Search tab layout)
  - `design/connection-management.import.png` (Import Token tab layout)
- Hook up default-tab selection logic:
  - If inbound requests exist -> default to Incoming Signals
  - Else -> default to Network Search

Definition of done:

- You can open the dialog from the header button.
- The old UI entry point no longer exists.

---

## Chunk B — Define shared handshake “inbox/outbox” contracts between Main and Simulator

Outcome:

- A clear API surface that Main UI can use to:
  - list **pending inbound invitations** (Reverse-Signal invites) targeting Main
  - accept/reject a pending inbound invitation
  - initiate an outbound invitation (direct or via relay)
  - initiate an outbound invitation from an OOB token (base64 serialized invite)

Work:

- Introduce application-layer interfaces (names flexible, but keep responsibilities split):
  - `IMainInvitationInbox` (query pending inbound **Reverse-Signal invitations**)
  - `IMainInvitationOutbox` (track sent invites that are awaiting response / expiry)
  - `IMainInvitationActions` (accept/reject/initiate/import)
- Introduce DTO(s) that are **UI-ready**:
  - `PendingInvitationDto` (for Incoming Signals tab)
    - `RequestCorrelationId`
    - `InviterIdentity` (PKH and/or display name)
    - `ArrivedVia` (Direct vs Relay + relay display name/id)
    - `ExpiresAtUtc`
    - `InvitationBlobBase64` (optional, for diagnostics/export)
  - `SentInvitationDto` (for Pending channels)
    - `RequestCorrelationId`
    - `TargetIdentity` (PKH/display name)
    - `Route` (Direct or RelayHostPeerId)
    - `CreatedAtUtc` / `ExpiresAtUtc`
- Route provenance:
  - Persist/attach the chosen/observed route alongside both pending inbound + sent outbound, so UI can display:
    - “Arriving via: Direct P2P”
    - “Arriving via: <RelayName>”
- Reverse-signal anti-replay:
  - Pending inbound invitations must be keyed by `request_correlation_id` and deduplicated until `expires_at_utc` (reject duplicates).
  - Inbox items must capture enough ingress metadata to deliver the response correctly:
    - direct: callback to `inviter_host` + `inviter_port`
    - relayed: relay host peer id for opaque response delivery
- Map these abstractions onto existing application services/commands where possible:
  - Pending invitation storage (acceptor-side) analogous to `PendingInvitations` in `session-flow.md`.
  - Accept action should call the existing “approve pending session/invite” command/service (e.g., `ApprovePendingSessionCommand`) that generates `InviteHandshakeResponse`.
  - Reject action should delete/purge the pending invitation record.

Definition of done:

- There is a single place in production code where:
  - pending inbound invitations are stored and can be acted upon
  - sent outbound invitations are tracked until accepted/expired

---

## Chunk C — Implement Connection Management: Tab 1 (Incoming Signals)

Outcome:

- The dialog’s **Incoming Signals** tab shows pending inbound **Reverse-Signal invitations**.
- Pending inbound invitations do **not** appear in the Secure Channels list yet.
- User can `ACCEPT` or `BURN/REJECT`.

Work:

- Implement the tab UI:
  - Empty state (“No pending handshake requests.”)
  - List state: each item shows peer name + “Arriving via: …” route
  - Actions: Accept / Reject
- Reference screenshot:
  - `design/connection-management.incoming.png`
- Implement tab VM behavior:
  - Live-updating list (ObservableCollection)
  - Accept triggers the acceptor-side approval flow:
    - validate + approve pending invitation
    - acceptor persists the full session immediately (per `session-flow.md` Step 2.3) before sending the response
    - generate and deliver `InviteHandshakeResponse` back to inviter (direct to inviter_host/port, or relayed depending on route)
    - remove the pending invitation
  - Reject removes the pending invitation (burn) and optionally emits diagnostics

Definition of done:

- Simulator can enqueue inbound invitations to Main, and they appear here.
- Accepting causes handshake finalization to begin (inviter finalizes once response arrives).

---

## Chunk C.1 — Accept-path routing for relayed Incoming Signals (no auto-send)

Outcome:

- Clicking **ACCEPT** on an inbound Reverse-Signal invitation that arrived **via relay** sends the `InviteHandshakeResponse` back to the inviter **through the same relay host**.
- The response is **not** sent automatically on receipt of the invite; it is only sent on explicit user acceptance.
- The response arrives at the inviter through the existing relayed-opaque processing path and triggers initiator finalization.

Work:

- Ensure pending inbound invitation records preserve relay provenance needed at accept time:
  - `IsRelayed`
  - `RelayHostPeerId` (identity peer id of relay host)
  - `InviterIdentityKeySpki` (already present in the invite; used to derive the inviter PKH addressing key)
  - (optional UI) relay display name / endpoint
- Update the accept code path (the command/service used by the Incoming Signals Accept button, e.g. `ApprovePendingSessionCommand`) so that:
  - **Direct invites** continue to deliver `InviteHandshakeResponse` via `inviter_host` + `inviter_port`.
  - **Relayed invites** deliver `InviteHandshakeResponse` via the relay transport path, targeting the inviter by **public key hash (PKH)** derived from the inviter identity SPKI (`SHA256(inviter_identity_key_spki)`) and routed through the chosen relay host.
- Relay transport precondition:
  - The acceptor must have a usable route to the relay host (typically an established secure transport session to the relay host) at the time the user clicks **ACCEPT**.
  - If the relay host route is not available, Accept should fail with a UI-visible error (and the Pending invitation should remain).
- Confirm the relayed response wire format matches existing ingress:
  - The inviter must receive the serialized `InviteHandshakeResponse` bytes as the decrypted relay “opaque payload” such that `ProcessRelayedOpaquePayloadCommand.TryHandleNonSessionPayloadAsync` can parse and dispatch it.
  - The relayed response payload must not be wrapped in an `InternalEnvelope` and must not be a `SessionRatchetMessage`.
- Add/extend tests:
  - Accepting a relayed pending session uses the relay send path and does not attempt direct callback.
  - Inviter-side processing via `ProcessRelayedOpaquePayloadCommand` successfully ingresses an `InviteHandshakeResponse` and finalizes initiator session state.

Definition of done:

- A relayed inbound invitation can be accepted from the Incoming Signals UI, and the inviter finalizes once the relayed `InviteHandshakeResponse` is delivered.
- No response is sent unless the user clicks **ACCEPT**.

---

## Chunk D — Implement Connection Management: Tab 2 (Network Search / outbound initiation)

Outcome:

- User can initiate a **regular Signal** (pre-key + X3DH) handshake from Main to a simulator peer by entering a target PKH and selecting a route.
- Route selection supports:
  - `Direct` (explicit `host:port` `DnsEndPoint`)
  - `Via Relay Host` (select a relay host peer from Main’s known peers with a direct session)
- This tab is responsible for:
  - requesting a **pre-key bundle** for the target PKH using the selected route
  - initiating the standard handshake init message using the same selected route

Work breakdown:

### Chunk D.1 — Connection Management Tab 2 UI + VM wiring (Signal initiation surface)

Outcome:

- “Network Search” becomes “Add Peer by PKH” (naming flexible) for regular Signal.
- User can enter:
  - target PKH
  - optional display name
  - route mode (Direct vs Via Relay Host)
  - direct endpoint (for Direct) or relay host peer selection (for Via Relay Host)
- UI shows phase/error and disables retry when a NotUntil backoff is active.

Work:

- Update the tab UI:
  - PKH input
  - DisplayName input (optional)
  - RouteMode selector
  - Direct endpoint input (DnsEndPoint)
  - Relay host dropdown sourced from `IDirectSessionRepository` (all peers with an active/known direct session, display by name, identify by peer id)
  - Primary action: “Fetch Pre-Key Bundle & Initiate”
  - Phase + Error status region
- Update VM bindings to drive a state machine (phases are not final, but must be explicit):
  - Idle
  - RequestingPreKeyBundle
  - PreKeyBundleNotUntil
  - PreKeyBundleNever
  - PreKeyBundleReceived
  - InitiatingHandshake
  - HandshakeSent
  - Failed

Definition of done:

- User can select direct/relay routes and click the primary action.
- Relay host dropdown lists peers derived from `IDirectSessionRepository`.

### Chunk D.2 — Pre-key bundle request via selected route (direct or relay-host)

Outcome:

- Main can request a pre-key bundle by PKH:
  - Direct: from the target endpoint.
  - Relayed: from the relay host (direct request/response to the relay host; relay host acts as the pre-key bundle holder for the target).

Work:

- Implement request logic:
  - Build `InternalEnvelope.PrekeyEnvelope.GetPreKeyBundleRequest { public_key_hash = targetPkh }`
  - Deliver request and interpret response:
    - If response contains bundle -> proceed
    - If no response payload -> show error

Definition of done:

- Pre-key bundle request succeeds/fails deterministically and returns a clear state to the UI.

### Chunk D.2.1 — Finish `RequestPreKeyBundleByPkhHandler.PerformHandshake` (standard initiator X3DH)

Outcome:

- The pre-key bundle CLI flow can complete the initiator-side Signal bootstrap:
  - build the initiator X3DH shared secret from the remote pre-key bundle
  - send `EstablishSessionRequest` to the remote endpoint
  - persist the created initiator session and direct session mapping

Work:

- Implement `PerformHandshake(...)` by following the existing standard handshake initiation pattern:
  - validate and construct a `Percolator.Cryptography.PreKeyBundle` from:
    - remote identity signing SPKI
    - remote signed pre-key (and signature)
    - optional one-time pre-key
  - call `ISessionCrypto.X3DH_Initiate(...)` using the local identity private key and the remote bundle
  - build and send `EstablishSessionRequest` (identity SPKI, ephemeral public key, signed prekey id, optional one-time prekey id)
  - parse `EstablishSessionResponse` and extract the negotiated `session_id`
  - create/persist the initiator `SecureSession` via `RatchetBootstrap.CreateInitiatorSession(...)`
  - upsert `IDirectSessionRepository` mapping for the remote peer
- Keep `RequestPreKeyBundleByPkhHandler` focused on the CLI path; Chunk D.4 remains the app/UI handshake initiation surface.

Definition of done:

- `RequestPreKeyBundleByPkhHandler` no longer throws `NotSupportedException` when attempting handshake.
- A successful call results in a persisted initiator session and a direct session record usable by secure messaging.

### Chunk D.3 — Simulator async state machine + persistence (restart-safe)

Outcome:

- The simulator persists outbound Signal initiation attempts so that:
  - NotUntil backoff survives restart
  - handshake “in progress” can survive restart and complete when messages arrive later

Work:

- Review what the simulator already persists today.
- Extend persisted simulator state to include (at minimum):
  - target PKH
  - selected route (direct endpoint or relay host peer id)
  - current phase
  - NotUntilUtc (optional)
  - last error (optional)
- Ensure the simulator UI is driven by the persisted state machine.

Definition of done:

- Restarting the app does not lose NotUntil state or in-progress handshake intent.

### Chunk D.4 — Standard handshake init send (direct or via relay queue)

Outcome:

- After a pre-key bundle is obtained:
  - the peer is created/updated with SPKI and PKH mapping
  - a standard handshake init is generated and sent via the selected route

Work:

- On bundle receipt:
  - create/update peer identity + PKH mapping (SPKI-derived PKH must match input)
  - upsert routing profile with the user-provided route
- Handshake init send:
  - Direct route: send `EstablishSessionRequest` to the endpoint
  - Relayed route: enqueue `HandshakeInitiatorHello` bytes to the relay queue for the target PKH
- Persist enough outbound state so the handshake can complete later when response arrives.

Definition of done:

- Main can initiate standard handshake to a simulator peer using direct or relay.

### Chunk D.5 — Tests + diagnostics

Outcome:

- Confidence that:
  - contract handling works (`pre_key_bundle` / `never` / `not_until`)
  - state machine transitions are correct
  - persistence retains NotUntil and in-progress state across restart

Work:

- Add/extend tests in Desktop.Wpf.Tests / ApplicationTests as appropriate.
- Add diagnostics events to simulator diagnostics tab for:
  - prekey request sent/response received
  - NotUntil enforced
  - handshake init sent

Definition of done:

- Automated tests cover the primary happy path and the NotUntil/Never paths.

---

## Chunk E — Implement Connection Management: Tab 3 (Import Token / OOB initiation)

Outcome:

- User can paste a base64 invite token produced by Simulator and click “Decode & Initiate”.
- Routing is automatically derived from decoded token.

Work:

- UI:
  - Large textbox
  - Decode & Initiate button
- Reference screenshot:
  - `design/connection-management.import.png`
- VM behavior:
  - Decode token -> parse `EstablishDirectSessionRequest` / `InviteHandshakeRequestPayload`
    - verify payload signature before trusting contents
    - extract inviter_host/port and correlation id
    - derive routing hints from token (if present) or fall back to a safe default
  - Create a Pending channel immediately
  - Add the invite to the **pending invitation inbox** (Main becomes the acceptor UI) so the user can approve and respond with `InviteHandshakeResponse`

Definition of done:

- Pasting a simulator-generated token initiates a pending **invitation approval flow** from Main.

---

## Chunk F — Implement Secure Channels unified list data model (Main Window)

Outcome:

- Main window has a **single unified list** (“Secure Channels”) that can represent:
  - Pending outbound invitations (awaiting `InviteHandshakeResponse`)
  - Active 1:1 sessions (Direct/Relay)
  - Group sessions (placeholder support acceptable)
  - Failed/expired handshakes
- List is **recency sorted** by `LastUpdate` descending.

Work:

- Introduce a UI model for list entries:
  - `SecureChannelListItemViewModel`
    - Id (session id / correlation id)
    - Display name
    - Badge type: DIRECT / RELAY / GROUP / PENDING / FAILED
    - Last snippet
    - Unread count
    - Online indicator
    - LastUpdate
- Ensure the model can represent the *two-phase* identity resolution that happens in Reverse-Signal:
  - Pending phase is keyed by `request_correlation_id`
  - Active phase is keyed by `session_id` (responder-assigned)
  - When an invite finalizes, migrate/merge the pending list item into the established session item while keeping UI continuity
- Add a container VM/service that owns the list and updates ordering.
- Wire to existing sessions / peer presence sources where available.

Glyphs:

- Group channels use `IconGroup` in place of initials avatar.

Definition of done:

- Outbound initiation (Chunks D/E) inserts a `PENDING` item that appears at top.

---

## Chunk F.1 — Application domain notifications for channel lifecycle (Option A)

Outcome:

- Application emits **authoritative** MediatR notifications when:
  - inbound pending invitations are created/removed/expired
  - active sessions are created
- Desktop can build a reactive UI projection without polling.

Scope:

- `Percolator.Application` only (no WPF changes required in this sub-chunk).

Work:

- Add new notifications (files in `Percolator.Application`):
  - `PendingSessionRemovedNotification(PendingSessionId PendingSessionId, RequestCorrelationId RequestCorrelationId, PendingSessionRemoveReason Reason)`
  - `SecureSessionCreatedNotification(SessionId SessionId, Percolator.Cryptography.Primitives.PeerId RemotePeerId, ProtocolVersion ProtocolVersion, SecureSessionCreatedReason Reason)`
  - Add enums:
    - `PendingSessionRemoveReason` = Accepted | Burned | Expired | Invalid
    - `SecureSessionCreatedReason` = AcceptedInvite | StandardHandshakeIngress | InitiatorFinalize

- Publish points (concrete locations):
  - `Percolator.Application/Network/EstablishDirectSessionService.cs`
    - already publishes `PendingSessionCreatedNotification` after `_pendingSessions.AddAsync(...)`.
  - `Percolator.Application/Network/ApprovePendingSessionCommand.cs` (`ApprovePendingSessionHandler`)
    - after `_sessions.AddAsync(session, ...)` publish `SecureSessionCreatedNotification(..., reason: AcceptedInvite)`.
    - after `_pending.DeleteAsync(pending.Id, ...)` publish `PendingSessionRemovedNotification(..., reason: Accepted)`.
    - if burn/reject is implemented elsewhere, publish `PendingSessionRemovedNotification(..., reason: Burned)`.
  - `Percolator.Application/Network/StandardHandshakeIngress.cs`
    - after `_sessions.AddAsync(session, ...)` publish `SecureSessionCreatedNotification(..., reason: StandardHandshakeIngress)`.
  - `Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`
    - after `_sessions.AddAsync(final, ...)` publish `SecureSessionCreatedNotification(..., reason: InitiatorFinalize)`.
  - `Percolator.Application/ReverseSignal/PendingSessionPurgeService.cs`
    - after `_repository.DeleteAsync(...)` publish `PendingSessionRemovedNotification(..., reason: Expired)`.

Notes:

- Notifications should carry stable identifiers only; UI projection resolves names/routes via repositories.

Definition of done:

- Handshake receipt publishes pending-created.
- Accept publishes session-created + pending-removed.
- Expiry purge publishes pending-removed.

---

## Chunk F.2 — Desktop shared models + store/service (reactive, not bindable)

Outcome:

- Desktop has a singleton in-memory state owner that represents the **UI read model** for:
  - unified channels list
  - inbound pending invitations list + count
- Shared models are reactive (`ReactiveProperty`) but **not WPF-bindable**.

Scope:

- `Desktop.Wpf` only. This chunk may break compilation until later chunks wire it in.

Work:

- Create new models (folder suggestion: `Desktop.Wpf/Features/Sessions/Models/`):
  - `SecureChannelKey` (stable key; supports aliasing correlation/session)
  - `SecureChannelModel` (reactive properties; no bindable types)
  - `PendingInvitationModel` (reactive properties)

- Create singleton store/service (folder suggestion: `Desktop.Wpf/Features/Sessions/State/`):
  - `ISecureChannelsStore`
  - `SecureChannelsStore`
    - owns mutable `ObservableCollection<SecureChannelModel>` and exposes `ReadOnlyObservableCollection<SecureChannelModel>`
    - owns mutable `ObservableCollection<PendingInvitationModel>` and exposes read-only wrapper
    - exposes `ReadOnlyReactiveProperty<int> PendingInboundCount` (derived from collection count)
    - provides internal mutation methods used by projection layer only:
      - `UpsertPendingInbound(...)`, `RemovePendingInbound(...)`
      - `UpsertSession(...)`
      - `UpsertOutboundPending(...)` (placeholder)
      - `UpsertFailure(...)` (placeholder)
    - enforces thread affinity for collection mutation (dispatcher marshal inside store)

- Register in DI (`Desktop.Wpf/App.xaml.cs`):
  - `services.AddSingleton<ISecureChannelsStore, SecureChannelsStore>();`

Definition of done:

- Store exists, with models, and can be mutated by an internal API.

---

## Chunk F.3 — Desktop projection: consume Application notifications and update store

Outcome:

- Desktop receives Application lifecycle notifications and updates the shared store.
- Projection is event-driven, single-flight, and dispatcher-safe.

Scope:

- `Desktop.Wpf` + notification handler types in the Desktop assembly.

Work:

- Add `SecureChannelsProjection` in `Desktop.Wpf/Features/Sessions/` implementing:
  - `INotificationHandler<PendingSessionCreatedNotification>`
  - `INotificationHandler<PendingSessionRemovedNotification>`
  - `INotificationHandler<SecureSessionCreatedNotification>`

- Projection strategy:
  - Start with “reload affected portion” (acceptable early):
    - For `PendingSessionCreatedNotification`: query `IPendingHandshakeQueries.EnumerateOpenAsync()` and repopulate pending inbound models.
    - For `PendingSessionRemovedNotification`: same as above (or incremental remove if you have enough IDs).
    - For `SecureSessionCreatedNotification`: query `ISessionRepository.GetAllActiveAsync(...)` and upsert session models.
  - Ensure:
    - event coalescing (debounce bursts)
    - single-flight reload to prevent overlap
    - store mutation happens on dispatcher.

- Register MediatR handlers:
  - confirm Desktop assembly registration already occurs in `App.xaml.cs` via `RegisterServicesFromAssembly(typeof(MainWindow).Assembly)`.

Definition of done:

- Inbound pending creates/removes update store.
- Session-created updates store.

---

## Chunk F.4 — ViewModel refactor: project shared models into bindable ViewModels

Outcome:

- Main window sidebar and pending menu bind to ViewModels that are projections of shared models.
- ViewModels are not shared; models are shared.

Scope:

- `Desktop.Wpf/Features/Sessions/`.

Work:

- Introduce bindable row VM:
  - `SecureChannelListItemViewModel` becomes a projection of `SecureChannelModel`.
  - It exposes `BindableReactiveProperty<T>` for XAML.
  - It subscribes to model `ReactiveProperty` streams and maps/derives:
    - `TimestampText`
    - `UnreadDisplay`
    - `HasUnread`

- Update `SessionsSidebarViewModel` to:
  - inject `ISecureChannelsStore`
  - maintain an item VM list derived from `store.Channels`
  - keep selection/navigation logic
  - remove direct repository queries from sidebar.

- Update pending menu + badge:
  - `PendingHandshakesMenuViewModel` binds to `store.PendingInbound` (projected into UI items if needed).
  - Sidebar badge binds to a VM property derived from `store.PendingInboundCount`.

Definition of done:

- Sidebar and badge are driven by the shared store via VM projections.

---

## Chunk F.5 — Remove legacy invalidation plumbing and scoped-VM mutation

Outcome:

- No WPF-local “notify changed” hack paths remain.
- No MediatR handler mutates scoped ViewModels directly.

Work:

- Remove/retire:
  - `ISecureChannelsListEvents` and implementations
  - manual `.NotifyChanged()` calls from dialog VMs
  - any remaining code paths that mutate `PendingHandshakesMenuViewModel.PendingHandshakes` from background scopes

Definition of done:

- Only the store/projection updates shared model state.

---

## Chunk F.6 — CorrelationId → SessionId migration and outbound pending normalization

Outcome:

- Pending outbound items migrate to active sessions while preserving UI continuity.

Work:

- Choose canonical outbound pending persistence source:
  - `IPreHandshakeSessionStore` and/or `ISentInvitationRepository`.
- Extend store to represent outbound pending items distinctly from inbound pending.
- Implement merge rules in store/projection:
  - when `SecureSessionCreatedNotification` arrives with enough information to correlate to an existing outbound pending (correlation id or recipient fingerprint), update a single `SecureChannelModel` instead of creating a new one.

Definition of done:

- Outbound pending rows transition to active without disappearing/reappearing as a new item.

Chunk F (F.1–F.6) overall definition of done:

- When a simulated peer sends an inbound handshake request:
  - “Add peer” badge increments immediately.
  - Secure Channels list shows a pending item without restarting.
- When the user accepts/burns/expires a pending invitation:
  - “Add peer” badge decrements immediately.
  - Pending item is removed and/or transitions to Active when the session is created.
- No MediatR handler mutates scoped WPF ViewModels directly.
- WPF UI state is owned by a single store/projection service that is dispatcher-safe.

---

## Chunk G — Main view right pane: channel-state-specific UI (Pending/Active/Offline/Failed)

Outcome:

- Selecting a channel updates the right pane to one of the states described in `design/main-window.md`:
  - Pending: disabled input + centered “Establishing…” banner
  - Active: enabled input + “E2E Encryption Established” banner
  - Offline: warning + queued send visuals (basic)
  - Failed: hidden input + Retry/Delete

Work:

- Create a `SelectedChannelViewModel` that exposes state.
- Implement a simple templated UI switching on state.
- Add commands:
  - Retry Connection (re-initiate handshake)
  - Delete Channel (remove from list)

Implementation note:

- “Queued send” visuals are allowed to be a UI-only placeholder initially; full delivery semantics/ack tracking can be implemented later.

Glyphs:

- Failed state can use `IconError` for the prominent error banner/iconography.

Definition of done:

- Pending -> Active transition updates UI when accept arrives.

---

## Chunk H — Bidirectional handshake plumbing end-to-end (Simulator <-> Main)

Outcome:

- Simulator peer can initiate handshake to Main by sending a **Reverse-Signal invitation** and Main can accept/reject in dialog.
- Main can initiate handshake to simulator peer by sending a **Reverse-Signal invitation** and simulator can accept and respond.
- Successful completion results in an Active channel entry with correct DIRECT vs RELAY badge.

Work:

- Ensure transport route is preserved through the full invitation lifecycle:
  - Invite delivery route (direct vs relayed)
  - Response delivery route (direct callback to inviter_host/port vs relayed response)
- Implement state transitions specific to Reverse-Signal:
  - Outbound Invite sent -> Pending
  - `InviteHandshakeResponse` received and inviter finalizes -> Active
  - Invite expires (no response) -> Failed/Expired
  - Invite rejected/burned -> Failed
- Ensure the “inviter finalizes” step matches `session-flow.md`:
  - lookup by `request_correlation_id` in SentInvitations
  - complete X3DH responder side and initialize ratchet
  - persist session keyed by responder-provided `session_id`
- Ensure inbound handling is symmetric for direct vs relayed ingress:
  - direct ingress via callback endpoint
  - relayed ingress via relay queue delivery service / command
  - explicitly cover `InviteHandshakeResponse` ingress for:
    - direct callback ingress (`InviteHandshakeResponseIngress`-style)
    - relayed opaque payload ingress (`ProcessRelayedOpaquePayloadCommand`-style)

Definition of done:

- You can perform both directions of handshake through UI.

---

## Chunk I — Notification badge + default focus behavior for Connection Management button

Outcome:

- Header button shows count badge and pulses when inbound requests exist.
- Opening dialog defaults to Tab 1 if inbound exists else Tab 2.

Work:

- Connect inbox count to shell VM.
- Implement simple animation or style trigger.

---

## Chunk J — Uplink Inspector side panel (Direct vs Relay topology)

Outcome:

- “UPLINK” button toggles a side panel showing connection topology:
  - Direct: Main <-> Peer
  - Relayed: Main -> Relay -> Peer

Work:

- Add inspector panel UI and toggle command.
- Back it with route data already tracked in Chunk H.

---

## Chunk K — Session reset / recovery action

Outcome:

- Kebab menu includes “Reset Secure Session”.
- Triggers a new outbound **Reverse-Signal invitation** behind the scenes while keeping channel history.

Work:

- Add command + UI affordance.
- Implement “temporary system message / banner” while re-establishing.

---

## Chunk L — Visual fidelity pass (match screenshots)

Outcome:

- Style and layout improvements to match `design/*.png`:
  - tab strip styling
  - button states
  - badge colors
  - list item layout

Work:

- Consolidate styles/templates.
- Ensure list item visuals (avatar, badges, unread, online dot).

---

## Chunk M — Cleanup / remove legacy handshake UI and orphaned code

Outcome:

- The codebase has a single, clear handshake entry point: **Connection Management**.
- Legacy UI and mappings that are no longer used are removed to avoid confusion and bit-rot.

Work:

- Remove legacy NewHandshake dialog artifacts if no longer used:
  - `NewHandshakeDialogWindow.xaml` / `.xaml.cs`
  - `NewHandshakeDialogViewModel.cs`
- Remove DI registrations for the legacy window/view model.
- Remove WindowManager mappings for legacy dialog in `Shared/Windowing/ViewMappings.xaml`.
- Remove any remaining call sites that open the legacy dialog (search for `ShowFor<NewHandshakeDialogViewModel>`).
- Ensure the “Add peer” header button and any other handshake affordances open `ConnectionManagementDialogViewModel`.

---

## Risks / tricky areas

- Debounced filtering and dispatcher scheduling: avoid hard UI-thread dependencies in core services.
- Preserving route provenance across handshake lifecycle (so UI can display “Arriving via …” accurately).
- Avoid mixing “pending inbound” with the Secure Channels list (anti-spam requirement).