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
- Glyphs:
  - Dialog close button: `IconClose`
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

## Chunk D — Implement Connection Management: Tab 2 (Network Search / outbound initiation)

Outcome:

- User can initiate a **Reverse-Signal invitation** from Main to a simulator peer by entering PKH and selecting a route.
- Route selection supports:
  - `Direct P2P (Local Mesh)`
  - Each active relay node by display name

Clarification:

- This is not a “Signal pre-key fetch” flow. This tab sends a Reverse-Signal **invitation** (Main acts as X3DH responder/inviter).

Work:

- Implement the tab UI:
  - PKH input
  - Transport Route dropdown
  - “Search & Connect” button
- Reference screenshot:
  - `design/connection-management.addPeer.png`
- VM behavior:
  - Validate PKH format (lightweight)
  - On submit:
    - create/ensure a local “Pending channel” record in the Secure Channels list model (see Chunk F)
    - invoke invitation initiation API which:
      - generates an `EstablishDirectSessionRequest` (serialized `InviteHandshakeRequestPayload`)
      - records a `SentInvitation` keyed by `request_correlation_id`
      - delivers the invite to the target via chosen transport route (direct or relayed)

Definition of done:

- Main can reliably send an outbound **invitation** to a simulator peer via direct or relay.

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