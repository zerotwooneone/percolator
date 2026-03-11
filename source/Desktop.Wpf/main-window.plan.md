# Main Window + Simulator Handshake Enablement Plan

Goal: make the **Main Window** and **Simulator Window** capable of performing **handshakes in both directions** (Main -> Simulator peer, Simulator peer -> Main), while implementing the Main Window UI behaviors described in `design/main-window.md`.

Constraints / notes:

- Chunks are intentionally large.
- The project does not need to be buildable or testable between chunks.
- Prioritize **handshake plumbing + UI** over full visual polish.
- Replace the current “Add peer” context menu entry points with a **Connection Management** dialog.

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
  - list pending inbound handshake signals targeting Main
  - accept/reject a pending inbound handshake
  - initiate outbound handshake (direct or via relay)
  - initiate outbound handshake from an OOB invite token

Work:

- Introduce application-layer interfaces (names flexible, but keep responsibilities split):
  - `IMainHandshakeInbox` (query pending inbound handshakes)
  - `IMainHandshakeActions` (accept/reject/initiate)
- Introduce DTO(s) for the inbox items that are **UI-ready**:
  - `PendingHandshakeRequestDto`
    - `RequestId` / correlation id
    - `FromPeerId` + display name
    - `ArrivedVia` (Direct vs Relay + relay display name/id)
    - timestamp
    - raw payload bytes (optional, if needed)
- Wire these services to whatever existing simulator relay/runtime plumbing exists (even if initially stubbed).

Definition of done:

- There is a single place in production code where pending inbound requests are stored and can be acted upon.

---

## Chunk C — Implement Connection Management: Tab 1 (Incoming Signals)

Outcome:

- The dialog’s **Incoming Signals** tab shows pending inbound handshake requests.
- Pending inbound requests do **not** appear in the Secure Channels list yet.
- User can `ACCEPT` or `BURN/REJECT`.

Work:

- Implement the tab UI:
  - Empty state (“No pending handshake requests.”)
  - List state: each item shows peer name + “Arriving via: …” route
  - Actions: Accept / Reject
- Implement tab VM behavior:
  - Live-updating list (ObservableCollection)
  - Accept triggers `X3DH_ACCEPT` to be queued toward the simulator peer (via direct or relay route as recorded)
  - Reject removes the request and optionally emits diagnostics

Definition of done:

- Simulator can enqueue inbound requests to Main, and they appear here.
- Accepting causes handshake finalization flow to begin (even if the session UI is still basic).

---

## Chunk D — Implement Connection Management: Tab 2 (Network Search / outbound initiation)

Outcome:

- User can initiate a handshake from Main to a simulator peer by entering PKH and selecting a route.
- Route selection supports:
  - `Direct P2P (Local Mesh)`
  - Each active relay node by display name

Work:

- Implement the tab UI:
  - PKH input
  - Transport Route dropdown
  - “Search & Connect” button
- VM behavior:
  - Validate PKH format (lightweight)
  - On submit:
    - create/ensure a local “Pending channel” record in the Secure Channels list model (see Chunk F)
    - invoke handshake initiation API which queues `X3DH_INIT` toward the target, with the chosen route

Definition of done:

- Main can reliably send an outbound handshake INIT to a simulator peer via direct or relay.

---

## Chunk E — Implement Connection Management: Tab 3 (Import Token / OOB initiation)

Outcome:

- User can paste a base64 invite token produced by Simulator and click “Decode & Initiate”.
- Routing is derived from decoded token.

Work:

- UI:
  - Large textbox
  - Decode & Initiate button
- VM behavior:
  - Decode token -> extract peer identity + any routing hints
  - Create a Pending channel immediately
  - Queue `X3DH_INIT`

Definition of done:

- Pasting a simulator-generated token initiates a pending handshake from Main.

---

## Chunk F — Implement Secure Channels unified list data model (Main Window)

Outcome:

- Main window has a **single unified list** (“Secure Channels”) that can represent:
  - Pending outbound handshakes
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
- Add a container VM/service that owns the list and updates ordering.
- Wire to existing sessions / peer presence sources where available.

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

Definition of done:

- Pending -> Active transition updates UI when accept arrives.

---

## Chunk H — Bidirectional handshake plumbing end-to-end (Simulator <-> Main)

Outcome:

- Simulator peer can initiate handshake to Main (inbound INIT) and Main can accept/reject in dialog.
- Main can initiate handshake to simulator peer (outbound INIT) and simulator can accept and respond.
- Successful completion results in an Active channel entry with correct DIRECT vs RELAY badge.

Work:

- Ensure transport route is preserved through the handshake lifecycle so UI can display:
  - “Arriving via: Direct P2P”
  - “Arriving via: <RelayName>”
- Implement state transitions:
  - Outbound INIT -> Pending
  - Accept received -> Active
  - Expire/Reject -> Failed
- Ensure simulator can emit “handshake expired” events that Main can map to Failed state.

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
- Triggers a new outbound `X3DH_INIT` behind the scenes while keeping channel history.

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

## Risks / tricky areas

- Debounced filtering and dispatcher scheduling: avoid hard UI-thread dependencies in core services.
- Preserving route provenance across handshake lifecycle (so UI can display “Arriving via …” accurately).
- Avoid mixing “pending inbound” with the Secure Channels list (anti-spam requirement).