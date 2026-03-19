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

Work (recipe):

- State ownership (Desktop.Wpf shared model/store for shared + notification-driven state)
  - Add a shared selection model:
    - File: `Desktop.Wpf/Features/Sessions/State/SelectedChannelModel.cs`
    - Lifetime: singleton (identity-scoped or app singleton; must be stable for sidebar + right pane)
    - Public contract:
      - `ReactiveProperty<SecureChannelKey?> SelectedKey` (nullable when nothing selected)
  - Sidebar selection becomes a projection of `SelectedChannelModel.SelectedKey`.
    - `SessionsSidebarViewModel` continues to expose a bindable `SelectedSessionId` for XAML, but it must be derived from / written through to `SelectedKey`.
    - Avoid: storing a separate “selected id” field that can drift from the shared model.
  - Guideline clarification:
    - ViewModels MAY own ephemeral editor/UI state (e.g., draft text, checkboxes, transient validation messages).
    - ViewModels MUST NOT own shared or notification-driven state (anything that must remain consistent across multiple VMs, or changes due to MediatR/domain events).

- Per-channel right pane state model (LRU-cached; holds ephemeral state across re-selection)
  - Introduce a per-channel state model that exists specifically to preserve ephemeral right-pane state across selections.
  - This model MUST NOT replace `SecureChannelModel`.
    - `SecureChannelModel` remains the authoritative shared channel list model owned by `ISecureChannelsStore`.
    - The per-channel state model is an overlay for ephemeral UI/editor state (e.g., message draft text).
  - Suggested shape:
    - File: `Desktop.Wpf/Features/Sessions/State/SelectedSecureChannelStateModel.cs`
    - Keying:
      - `SecureChannelKey ChannelKey` (stable key; do not key by `SessionId` string)
    - Ephemeral fields (examples):
      - `ReactiveProperty<string> DraftMessageText`
      - Optional: `ReactiveProperty<bool> IsKebabOpen`, `ReactiveProperty<int> SelectedInspectorTab`, etc.
  - Passthrough vs snapshot recommendation:
    - Prefer PASSTHROUGH for shared facts:
      - Right pane reads display name/status/online/etc. from the current `SecureChannelModel` resolved from `ISecureChannelsStore.Channels` using `ChannelKey`.
    - Keep only ephemeral/editor state in `SelectedSecureChannelStateModel`:
      - e.g., draft message text that should be restored when returning to a channel.
    - Avoid snapshotting shared facts into the per-channel state model (leads to stale/duplicated state and breaks Chunk F invariants).
  - Cache owner:
    - File: `Desktop.Wpf/Features/Sessions/State/SelectedSecureChannelStateCache.cs`
    - Behavior:
      - `GetOrCreate(SecureChannelKey key) => SelectedSecureChannelStateModel`
      - LRU-bounded to max `10` items.
      - Evicted `SelectedSecureChannelStateModel` MUST be disposed.

- Memory / lifetime constraint (bounded recent selection)
  - The system must limit how many “recently selected” channel-specific objects remain alive in memory.
  - Start with a maximum of `10`.
  - Use an LRU policy:
    - When `SelectedKey` changes, mark the associated item as most-recently-used.
    - When the number of cached items exceeds the max, evict the least-recently-selected.
  - What is cached (implementation choice; must be explicit when implementing):
    - Cache `SelectedSecureChannelStateModel` instances (per-channel ephemeral right-pane/editor state).
    - Separately (optional future improvement): add an LRU to `ISessionScopeFactory.GetOrCreate(...)` if chat session scopes are heavy.
  - Eviction behavior:
    - Evicted items MUST be disposed (scope disposed, subscriptions released) to avoid leaks.
    - Eviction MUST NOT affect the shared models/store (channel list + selection model remain intact).

- Right pane view model (projection-only)
  - Create a right pane VM that derives its entire state from `SelectedChannelModel.SelectedKey` + `ISecureChannelsStore.Channels`:
    - File: `Desktop.Wpf/Features/Sessions/SelectedChannelPaneViewModel.cs`
    - Inputs:
      - `SelectedChannelModel`
      - `ISecureChannelsStore`
      - `IMediator` (for commands only)
    - Public bindable outputs (example contract; adjust to match `design/main-window.md`):
      - `BindableReactiveProperty<SelectedPaneState>` where `SelectedPaneState` is an enum: `None`, `Pending`, `Active`, `Offline`, `Failed`
      - `BindableReactiveProperty<string?> DisplayName`, `BindableReactiveProperty<string?> BannerText`, `BindableReactiveProperty<bool> IsInputEnabled`, etc.
      - Optional: a bindable `CurrentChannelKey` or `CurrentSessionId` for command payloads.
  - State mapping rules (must be explicit and stable):
    - `SecureChannelKind.PendingInbound` or `PendingOutbound` => `Pending`
    - `SecureChannelKind.Direct` and `IsOnline == true` => `Active`
    - `SecureChannelKind.Direct` and `IsOnline == false` => `Offline`
    - `SecureChannelKind.Failed` => `Failed`
  - Critical invariant:
    - Pending->Active transitions must be driven by the existing shared model instance changing via store migration (Chunk F.6).
    - The right pane must react solely to store/model changes (no imperative refresh calls).

- UI composition / templating
  - Host the right pane inside `SessionShellView` (right column content).
  - Implement UI switching via templated `ContentControl`:
    - File: `Desktop.Wpf/Features/Sessions/SessionShellView.xaml`
    - Bind the pane VM (or its `CurrentState`) and switch templates based on `SelectedPaneState`.
    - Keep the sidebar as-is (`SessionShellViewModel.Sidebar`).
  - Add view mappings for any new view types if needed:
    - File: `Desktop.Wpf/Shared/Theme/ViewMappings.xaml` or `Desktop.Wpf/Shared/Windowing/ViewMappings.xaml` (use whichever is the active mapping source in the app)

- Commands (Application layer; projection updates store)
  - Retry Connection
    - Behavior: re-initiate handshake for the selected peer/channel.
    - Implementation constraint: invoke via `IMediator.Send(...)` only.
    - Resulting UI updates must occur via domain notifications -> `SecureChannelsProjection` -> store.
  - Delete Channel
    - Behavior: remove channel from list.
    - Implementation constraint: invoke via `IMediator.Send(...)` and have application/domain remove/purge.
    - Avoid: directly mutating the store from the VM.

Implementation note:

- “Queued send” visuals are allowed to be a UI-only placeholder initially; full delivery semantics/ack tracking can be implemented later.

Glyphs:

- Failed state can use `IconError` for the prominent error banner/iconography.

Definition of done:

- Selecting a pending row shows Pending pane state (disabled input + “Establishing…” banner).
- Selecting an active row shows Active pane state (enabled input + “E2E Encryption Established” banner).
- Selecting an active row that is offline shows Offline pane state.
- Selecting a failed row shows Failed pane state (input hidden) and exposes Retry/Delete commands.
- Pending -> Active transition updates right pane automatically when the store migrates keys (Chunk F.6) and the channel kind becomes Direct.

Minimal tests:

- Add a non-brittle unit test for projection behavior:
  - When `SelectedKey` changes, the pane VM updates its bindable state based on the matching `SecureChannelModel` in `ISecureChannelsStore.Channels`.
  - When a `SecureChannelModel.Kind` changes (pending -> direct), the pane VM updates state without recreating the model.

---

## Chunk H — Bidirectional handshake plumbing end-to-end (Simulator <-> Main)

Outcome:

- Simulator peer can initiate handshake to Main by sending a **Reverse-Signal invitation** and Main can accept/reject in dialog.
- Main can initiate handshake to simulator peer by sending a **Reverse-Signal invitation** and simulator can accept and respond.
- Successful completion results in an Active channel entry with correct DIRECT vs RELAY badge.

Work (recipe):

- Guideline constraint (repeat for safety)
  - Application-layer services/handlers MUST NOT mutate WPF ViewModels.
  - UI state changes MUST be reflected via domain notifications -> `SecureChannelsProjection` -> `SecureChannelsStore`.

- Identify and use existing authoritative ingress/delivery primitives
  - Direct callback ingress for Reverse-Signal response:
    - File: `Percolator.Application/Network/InviteHandshakeResponseIngress.cs`
    - Contract: `IInviteHandshakeResponseIngress.HandleAsync(SelfId, InviteHandshakeResponse, ct)`
    - Delegates to: `HandleHandshakeResponderHelloCommand`
  - Direct/Relayed delivery for Reverse-Signal response:
    - File: `Percolator.Application/Network/InviteHandshakeResponseDeliveryService.cs`
    - Behavior:
      - Direct: `_grpc.DeliverInviteHandshakeResponseAsync(directCallbackEndpoint, response)`
      - Relayed: `_transport.SendViaRelayAsync(...)` to chosen relay from `IRelayTopology`
      - Emits send-path strings: `Direct`, `Relay:<peerId>`, and supports simulator wiretap `Simulated`
  - Relayed ingress for Reverse-Signal invite + response (opaque bytes):
    - File: `Percolator.Application/Network/Handshake/ProcessRelayedOpaquePayloadCommand.cs`
    - `TryHandleNonSessionPayloadAsync(...)` already recognizes:
      - Reverse-signal invite: `EstablishDirectSessionRequest` (queue invite as `isRelayed: true`)
      - Reverse-signal response: `InviteHandshakeResponse` (delegates to `IInviteHandshakeResponseIngress`)

- Route provenance: define what must be preserved and where it lives
  - Data that must survive from invitation to active session:
    - Invite delivery route: `Direct` vs `Relay:<peerId>`
    - Response delivery route: `Direct` vs `Relay:<peerId>`
  - Representation choice (Desktop.Wpf shared models):
    - Add fields to `SecureChannelModel` (shared; notification-driven) to represent route provenance used by UI badges:
      - Example: `ReactiveProperty<string?> RouteText` or `ReactiveProperty<bool> IsRelayed` + `ReactiveProperty<string?> RelayPeerId`
    - These fields MUST be written only by the projection/store.

- Reverse-Signal state transitions (authoritative behaviors)
  - Outbound invite sent -> PendingOutbound channel row
    - Key: correlation id (`SecureChannelKeyType.PendingCorrelation`)
    - Kind: `SecureChannelKind.PendingOutbound`
    - Route fields populated from invite send path
  - Invite accepted -> inviter receives `InviteHandshakeResponse` -> inviter finalizes -> Active
    - Response ingress must go through:
      - direct callback: `InviteHandshakeResponseIngress`
      - relayed opaque: `ProcessRelayedOpaquePayloadCommand` -> `IInviteHandshakeResponseIngress`
    - Finalization must match `session-flow.md`:
      - lookup by `request_correlation_id` in sent invitations
      - complete X3DH + initialize ratchet
      - persist session keyed by responder-provided `session_id`
    - UI continuity requirement:
      - the pending outbound row migrates to the active session row (Chunk F.6).
  - Invite expires (no response) -> Failed/Expired
    - Pending outbound row transitions to `SecureChannelKind.Failed` and records a reason string/time.
  - Invite rejected/burned -> Failed
    - Pending outbound row transitions to `SecureChannelKind.Failed`.

- Projection requirements (Desktop.Wpf)
  - Update `SecureChannelsProjection.ReloadAsync` to incorporate any new persistence sources for Reverse-Signal state:
    - Pending outbound source(s): `IPreHandshakeSessionStore` (already used) and/or `ISentInvitationRepository`.
    - If route provenance is only available from `ISentInvitationRepository`, use it to enrich the outbound pending rows.
  - Ensure route fields (Direct/Relay) are set on:
    - PendingOutbound rows
    - Active session rows (based on last-known route provenance)

- Simulator parity / validation helpers (Desktop.Wpf)
  - Simulator can already deliver `InviteHandshakeResponse` to main:
    - File: `Desktop.Wpf/Features/Simulator/SimulatedPeerRuntimeService.cs`
    - Method: `DeliverInviteHandshakeResponseToMainAsync(...)`
  - Ensure both directions can be driven via UI affordances (Connection Management dialog + simulator UI).

### Subchunk H.1 — Simulator handshake UI state updates when Main initiates

Problem:

- When Main initiates a reverse-signal handshake to a simulated peer (direct `127.77.x.y:5002` path), the simulator runtime can create the session, but the simulator “Handshakes” UI state for that peer may not reflect the transition.

Plan:

- For Main -> Simulator direct initiation (outbound interceptor path), the simulator must update the same UI-owned state as the simulator->main flow:
  - On receipt of `EstablishDirectSessionRequest` for a simulated peer, parse the invite payload’s `request_correlation_id` and set:
    - `SimulatedPeerModel.MarkInboundPending(correlationId)`
    - (optional) attempt phase string (e.g., `InviteReceived`) via the existing runtime-state attempt helpers.
  - Ensure the pending `InviteHandshakeResponse` is stored in the simulator pending inbox keyed by `(simulatedPeerId, correlationId)` so the Accept button can finalize.
- Ensure the “Handshakes” UI updates remain MVVM-safe:
  - State transitions happen on the model (`SimulatedPeerModel`) and are observed by `SimulatedHandshakeStateMachineCardViewModel` via `RuntimeState`.
  - No application-layer services directly mutate VMs.

Definition of done:

- Main sends direct reverse-signal invite to simulated peer.
- The simulated peer card in the Handshakes tab visibly enters an inbound pending state.
- After accepting, the simulated peer card shows established.

### Subchunk H.2 — Replace hardcoded auto-accept with pending + manual accept

Problem:

- The current interception flow effectively “auto-accepts” by generating an `InviteHandshakeResponse` and delivering it back to Main immediately.
- This prevents the simulator from behaving like a real peer with an explicit “Accept” action.

Plan:

- Refactor the Main -> Simulator direct initiation pipeline to split two concerns:
  - **Receipt:** accept and prepare the response, but do not deliver it.
  - **Delivery:** user-driven via simulator UI (Accept button), optionally with an auto-accept toggle.
- Replace the current behavior in `SimulatorOutboundInterceptor.EstablishDirectSessionAsync(...)`:
  - Instead of calling `AcceptReverseSignalInviteAsync(...)` and immediately `PercolatorMessageService.DeliverInviteHandshakeResponse(...)`, route to a simulator runtime service method that:
    - Computes `InviteHandshakeResponse` and stores it in `SimulatedPeerPendingInbox` (and persists it via `SimulatedPeerRuntimeStoreDto.PendingInviteHandshakeResponses`).
    - Calls `SimulatedPeerModel.MarkInboundPending(correlationId)`.
    - Returns `EstablishDirectSessionResponse.Queued` to Main (with `RequestCorrelationId` populated).
- Update simulator Accept flow to be the single place that triggers delivery:
  - On Accept, deliver the stored `InviteHandshakeResponse` to Main using the existing helper (`ISimulatedPeerRuntimeService.DeliverInviteHandshakeResponseToMainAsync(...)`) or directly via `PercolatorMessageService.DeliverInviteHandshakeResponse(...)`.
  - Then call `TryFinalizeInviteHandshakeResponseFromMainAsync(...)` so the simulated peer finalizes the session and transitions to `MarkEstablished()`.
- (Optional) Simulator-only toggles:
  - If `SimulatedHandshakeStateMachineCardViewModel.AutoAccept` / `AutoRespond` are kept, implement them by invoking the same Accept command automatically after receipt (do not reintroduce a separate codepath).

Definition of done:

- Main initiating a handshake does NOT immediately complete unless simulator user clicks Accept.
- Simulator UI shows inbound pending with Accept/Reject buttons.
- Clicking Accept delivers response to Main and results in established state on both sides.

### Subchunk H.3 — Main-initiated reverse-signal should show PendingOutbound immediately (source: SentInvitation)

Problem:

- After clicking “Fetch & Initiate” (Network Search tab), the user expects to see a PendingOutbound secure channel appear in the main window list immediately.
- The reverse-signal send path already persists a `SentInvitation` keyed by `request_correlation_id`, but the main list does not reliably render it as a pending channel.

What needs to change:

- **Projection must render PendingOutbound from `ISentInvitationRepository` (reverse-signal source of truth)**
  - Do not force reverse-signal into `IPreHandshakeSessionStore` (it requires fields that are not available/meaningful at reverse-signal send time).
  - Extend `SecureChannelsProjection.ReloadAsync` so PendingOutbound rows include unexpired `SentInvitation` rows that have not yet migrated to an established session.
  - Key the pending channel by correlation id:
    - `SecureChannelKey.FromPendingCorrelationId(sent.RequestCorrelationId.Value)`
- **Define “still pending” vs “migrated” rule**
  - A `SentInvitation` should stop rendering as PendingOutbound once:
    - it is expired, or
    - it has been matched/migrated to an established session (existing migration mechanism already uses correlation id continuity).
- **Immediate UI refresh when invite is sent**
  - Emitting the `SentInvitation` row is not enough if the projection is not notified.
  - Add an outbound-send notification (e.g., `SentInvitationUpsertedNotification(RequestCorrelationId)` or `OutboundInviteSentNotification(RequestCorrelationId)`), and have `SecureChannelsProjection` handle it by requesting a reload.
  - Avoid pushing UI-only reload calls from the dialog VM; prefer notification-driven projection behavior.

Definition of done:

- Clicking “Fetch & Initiate” shows a PendingOutbound secure channel immediately.
- When the handshake completes, the pending row migrates to Active without duplicating entries.

### Subchunk H.4 — Reverse-signal peer naming + endpoint-only initiation

Goal:

- The display name entered in Network Search is used consistently:
  - immediately on the PendingOutbound row
  - later on the saved peer identity once the remote identity is learned
- Additionally, allow direct reverse-signal initiation with **endpoint only** (no PKH), using correlation-id keyed pending identity.

What needs to change:

- **Persist outbound metadata on `SentInvitation`**
  - Add fields to `SentInvitation` (schema + repository + writer at send site):
    - `TargetDisplayName`
    - `TargetEndpointHost` + `TargetEndpointPort` (or a single normalized endpoint string)
  - These fields are keyed by `RequestCorrelationId` and survive until finalize.
- **Render PendingOutbound label from `SentInvitation`**
  - Update `SecureChannelsProjection` PendingOutbound rendering to prefer:
    - `PeerIdentity.DisplayName` if a peer exists
    - else `SentInvitation.TargetDisplayName`
    - else `"Outbound invite"`
- **Endpoint-only UX**
  - For Direct mode, allow `TargetPkhText` to be optional.
  - Validate endpoint and send invite regardless of PKH presence.
  - Persist the name + endpoint metadata on `SentInvitation` so the UI can show a coherent pending row.
- **Upgrade to real peer identity on response**
  - When `InviteHandshakeResponse` is received, compute the remote PKH from `acceptor_identity_key`.
  - Resolve or create `PeerIdentity` keyed by PKH.
  - Apply `TargetDisplayName` to the peer only if the peer has no existing display name.

Definition of done:

- The PendingOutbound row displays the user-entered name even when no peer exists yet.
- Endpoint-only initiation works for Direct mode and results in a saved peer name once the response arrives.

### Subchunk H.5 — Network Search: Relay mode UX + validation (requires PKH)

Goal:

- When the route mode is set to **Via Relay Host**, the user must:
  - pick a relay peer from the Relay Host dropdown
  - enter a target peer PKH (hex)
- The UI only enforces inputs and shows clear validation errors. No protocol changes yet.

Work:

- Update `ConnectionManagementDialogWindow.xaml`
  - Add a `TARGET IDENTITY (PKH)` label + input.
  - Visibility trigger: only visible when `SelectedRouteMode.Value.Key == "relay"`.
- Update `ConnectionManagementDialogViewModel`
  - Add `TargetPkhText` as a reactive property.
  - In `ExecuteNetworkSearchAsync`, enforce Relay mode requirements:
    - `SelectedRelayHost` is required
    - `TargetPkhText` is required and must parse to 32 bytes
  - Keep Direct mode behavior unchanged.

Definition of done:

- Relay mode cannot start without Relay Host + PKH.
- Error messages are clear and do not throw to the UI thread.

### Subchunk H.6 — Relay mode: fetch target pre-key bundle via relay host session

Goal:

- Clicking `Fetch & Initiate` in Relay mode performs only the “fetch pre-key bundle” step.
- Provide early UI feedback when the entered PKH already corresponds to an established session (to prevent accidental session resets).

Work:

- Debounced PKH awareness (UX-only; no protocol changes)
  - On `TargetPkhText` changes, debounce (e.g. ~300–500ms) and attempt parse.
  - If parse succeeds, query local state:
    - whether a `PeerIdentity` exists for that PKH
    - whether any established secure session exists for that identity
  - If an established session exists, show warning text under the PKH field:
    - “An existing secure session with this identity already exists. Initiating a new handshake may reset/replace the active session.”
  - Hide `Fetch & Initiate` in this state, and instead expose an explicit “Re-establish session” action.

- Fetch semantics (align to Standard Flow: bundle fetch is the normal initiator step)
  - Standard X3DH initiation requires the responder’s current pre-key bundle (see `source/session-flow.md` Part 1, Step 1.1).
  - Do not skip the fetch merely because the PKH is “known”; the bundle includes key IDs and may rotate.
  - It is acceptable to cache the fetched bundle briefly and reuse it while it is fresh.
    - Use staleness/TTL to decide reuse vs refetch (e.g. “fetched within the last N minutes”).
    - If the response includes explicit expiry metadata, use that.

- Use existing envelope pattern over the Main↔Relay secure session:
  - Request: `InternalEnvelope.PrekeyEnvelope.GetPreKeyBundleRequest` (`PublicKeyHash = targetPKH`)
  - Encrypt to relay with `ISecureMessagingService.EncryptAsync(relaySessionId, ...)`
  - Send to relay with `IMessageTransportService.SendMessageAsync(relayPeerId, relayDirectSessionId, cipher, ct)`
  - Decrypt response with `ISecureMessagingService.DecryptInboundAsync(selfId, ...)`
  - Expect: `InternalEnvelope.GetPreKeyBundleResponse.PreKeyBundle`

- Overwrite/reset confirmation (only for explicit re-establish)
  - If the user chooses “Re-establish session” while an established session exists:
    - Show a yes/no confirmation dialog:
      - “This will create a new session with this identity and may invalidate existing pending messages. Continue?”
    - Do not delete historical sessions silently; prefer marking which session is “current” for routing.

- Failures must be surfaced as user-facing errors:
  - relay offline/unavailable
  - relay returns no response payload
  - response decrypt/parse failure
  - target not found

Definition of done:

- For valid relay+PKH, UI reaches “bundle fetched” state.
- For invalid/unavailable cases, UI shows the correct error.
- If PKH maps to an existing established session, the UI warns (debounced) and prevents accidental initiation; a re-establish path is explicit and confirmed.

### Subchunk H.7 — Relay mode: enqueue standard handshake initiator hello + show PendingOutbound

Goal:

- After pre-key bundle is available, Main initiates a standard (not reverse-signal) handshake via relay:
  - create a `HandshakeInitiatorHello`
  - enqueue it to the relay host’s message queue addressed to the target PKH
  - immediately show a PendingOutbound channel in the main window list

Work:

- Create `HandshakeInitiatorHello` from the fetched bundle.
  - MVP invariant: at most one pending standard-handshake-via-relay attempt per initiator at a time.
- Enqueue to relay host:
  - `InternalEnvelope.MessageQueueEnvelope.EnqueueOpaqueMessageRequest`
    - `RecipientPublicKeyHash = targetPKH`
    - `MessageBlob = hello.ToByteArray()`
  - Encrypt to relay and send over the direct session.
- Persist a “pending standard handshake via relay” record so projection can render PendingOutbound.
  - Source of truth should be a durable store (prefer: extend `IPreHandshakeSessionStore`, or create a dedicated store if needed).
  - Persist:
    - target PKH
    - relay peer id
    - display name (if supplied)
    - created time / expiry
    - the initiator ephemeral material needed to finalize.
- Projection (`SecureChannelsProjection`) must render PendingOutbound from this store.
  - Route provenance text should include `Relay:<relayPeerId>`.

Definition of done:

- Clicking `Fetch & Initiate` in Relay mode produces a PendingOutbound row immediately.
- The row includes relay route info.

### Subchunk H.8 — Relay mode: async completion (finalize initiator on inbound relayed delivery)

Goal:

- When the target accepts/responds, the initiator finalizes and the pending row migrates to Active.

Work:

- No polling. Completion is driven by delivery.
  - The relay host is responsible for delivering queued opaque messages to the initiator when possible.
  - When Main receives an inbound relayed opaque payload from the relay host:
    - Attempt parse as `EstablishSessionResponse`.
    - Finalize initiator-side session using the persisted pending handshake state.
    - Emit `SecureSessionCreatedNotification` so projection migrates PendingOutbound -> Active.
- Failure/timeout semantics:
  - If no completion after expiry, mark the pending attempt Failed/Expired and project it accordingly.

Definition of done:

- Completing the handshake via relay transitions the main window entry from PendingOutbound to Active.
- The completion still works if Connection Management dialog is closed.

### Subchunk H.9 — Simulator: relay host must respond to PreKey bundle requests (RPC response payload)

Goal:

- Relay-mode `Fetch & Initiate` must successfully fetch a target’s pre-key bundle through a simulated relay host.
- The simulated relay host must behave like a real node for `DeliverOpaqueMessage` request/response semantics:
  - When Main delivers an encrypted `InternalEnvelope` requesting `GetPreKeyBundleRequest` to the relay host,
  - the relay host returns a `DeliverOpaqueMessageResponse.ResponsePayload` containing an encrypted `InternalEnvelope.GetPreKeyBundleResponse`.

Root cause (current behavior):

- Main sends `InternalEnvelope.PrekeyEnvelope.GetPreKeyBundleRequest` to the relay host over the *direct* session (expected request/response).
- In simulator mode, `GrpcMessageTransportService` intercepts this RPC via `ISimulatorOutboundInterceptor.TryDeliverOpaqueMessage`.
- The simulated peer handler (`SimulatedPeerRuntime.ReceiveOpaqueMessageFromMainAsync`) only handles:
  - `InternalEnvelope.MessageQueueEnvelope.EnqueueOpaqueMessageRequest`
  - and returns `DeliverOpaqueMessageResponse { Version = 1 }` for everything else.
- Therefore the UI sees: `DeliverOpaqueMessageResponse` with **no** `ResponsePayload` and surfaces "No response payload returned.".

Design principles:

- The UI must not repair simulator state or routing (it’s a consumer).
- The simulator must not “reach into” the main application’s state or repositories.
  - The simulator pretends to be a remote peer; the only contract surface between Main and the simulator is the fake gRPC transport interception.
- There should be one canonical envelope format (`InternalEnvelope`) and one canonical set of request/response message types.
  - We already have this: `InternalEnvelope` + existing handlers on the Main side.
- Avoid introducing a new application-layer “envelope processor” abstraction.
  - We already have a request/response boundary in the application via MediatR (`ProcessInternalEnvelopeCommand`) and the ingress pipeline.
  - The simulator should implement the *remote peer* behavior, not a second copy of the Main ingress pipeline.

Work:

- Implement request/response handling for `PrekeyEnvelope.GetPreKeyBundleRequest` inside the simulated relay host runtime.
  - Location: `Desktop.Wpf/Features/Simulator/SimulatedPeerRuntimeService.cs` (nested `SimulatedPeerRuntime.ReceiveOpaqueMessageFromMainAsync`).
  - Current behavior: handles only `MessageQueueEnvelope.EnqueueOpaqueMessageRequest`; returns empty `DeliverOpaqueMessageResponse` otherwise.
  - New behavior for relay host:
    - When decrypted `InternalEnvelope.ApplicationPayloadCase == PrekeyEnvelope` and `MessageCase == GetPreKeyBundleRequest`:
      - Read the bundle bytes from the relay host’s own simulated store (owned by the simulator).
        - Use existing simulator mechanisms (e.g., `ISimulatorStateService.TryPopPreKeyBundleByRecipientPkhAsync` or equivalent) keyed by `PublicKeyHash`.
      - Build an `InternalEnvelope.GetPreKeyBundleResponse`:
        - If bundle found: populate `PreKeyBundle` fields from the stored bytes.
        - If not found: return a valid `GetPreKeyBundleResponse` with `PreKeyBundle` unset.
      - Encrypt the response envelope back to Main using the matched session.
      - Return `DeliverOpaqueMessageResponse` with `ResponsePayload` set to the encrypted bytes.
    - This mirrors real gRPC semantics: request in, response payload out.

- Keep relay-queue semantics unchanged.
  - `EnqueueOpaqueMessageRequest` should remain supported and should still return an empty response payload (fire-and-forget enqueue).

- Add simulator diagnostics to reduce brittleness when debugging.
  - Emit a diagnostic event when the relay host receives a `GetPreKeyBundleRequest`.
  - Emit a diagnostic event for:
    - bundle found (and owner peer id if known)
    - bundle not found
    - decrypt/parse failure

- Add targeted tests (simulator-focused) instead of inventing new application ports.
  - Unit/integration test that:
    - given an established direct session between Main and the simulated relay host,
    - delivering a `DeliverOpaqueMessageRequest` containing an encrypted `GetPreKeyBundleRequest` returns a `DeliverOpaqueMessageResponse` with non-empty `ResponsePayload`.
    - decrypting that payload yields an `InternalEnvelope.GetPreKeyBundleResponse`.

Definition of done:

- Relay-mode Network Search no longer errors with "No response payload returned.".
- When the target bundle exists (published to relay), the UI reaches the “bundle fetched” state and proceeds to enqueue the handshake.
- When the target bundle does not exist, the relay returns a valid encrypted `GetPreKeyBundleResponse` with `PreKeyBundle` unset (and the UI shows "Target not found.").

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