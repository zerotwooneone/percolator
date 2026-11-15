# Percolator.Desktop Shell Flow Specification

This application is strictly controlled by the QMS system found in `/source/spec`.

---

## Main Application Shell

The main application shell is a modern, responsive desktop shell inspired by Discord and Microsoft Teams.

### Responsive Design Principles
- **Window Geometry**:
  - Minimum supported window size: $800 \times 600\text{ px}$. Default target: $1200 \times 800\text{ px}$.

---

### 1. Left Sidebar (Transports & Navigation Rail)

The Left Sidebar uses a functional Split Rail layout inspired by modern collaboration platforms, categorizing Direct Messages and Relay Post Offices with clear visual hierarchy while keeping pending invitations and identity controls accessible at the bottom.

- **Top Zone: Direct Messages Button (`[Direct Messages]`)**:
  - A prominent icon button at the very top of the rail featuring a direct chat bubble icon, unread message count badge, and clear tooltip (*"Direct Messages"*).
  - Clicking selects the Direct Messages view, loading all pairwise 1-to-1 conversations into the Right Sidebar.
- **Middle Zone: Relays / Post Offices List**:
  - Displays connected relays (post offices) facilitating asynchronous store-and-forward routing and group messaging.
  - **Visual Presentation**:
    - Each relay displays a distinct relay icon / badge.
    - **Zero Technical Clutter**: Do not show IP addresses, ports, or protocol strings in the sidebar list.
    - **Hover Tooltip**: Hovering over an item displays the relay's friendly display name or public endpoint in a clean tooltip.
  - **Drag-and-Drop Reordering & Relay Folders**:
    - Users can drag and drop relay icons vertically to reorder them; order is persisted per persona.
    - Users can drag one relay onto another to create a collapsible **Relay Folder**.
    - Relays can be dragged into or out of existing folders.
    - Right-clicking a folder opens a menu to *"Rename Folder"* or *"Ungroup Folder"*.
  - **Right-Click Context Menu (Relay)**:
    - "Relay Details" (opens the right-anchored inspector drawer), "Reconnect", "Remove Relay".
  - **Selection**: Clicking a relay selects it, populating the Right Sidebar with channels and group chats hosted on that relay.
- **Bottom Zone: Connection Controls & Anchored Pending**:
  - **Add Connection (`+`) Button**:
    - Prominently anchored above `[Pending Requests]`.
    - Clicking opens the redesigned **Add Connection Dialog** (see modal specification below).
  - **Pending Requests Button (`[Pending Requests]`)**:
    - Anchored at the bottom of the rail, strictly positioned above the user's avatar.
    - **UX Rationale**: Anchoring at the bottom prevents pending invitation spam from occupying prime visual real estate at the top of the interface, allowing users to comfortably ignore incoming connection requests.
    - Displays a subtle badge/indicator light showing the count of pending inbound requests.
    - **Zero Distraction Privacy Invariant (`INV-UNSOLICITED-CONNECTION-SILENCE`, `SYS-CONTACT-CONSENT-GATE`)**: Inbound unsolicited connections or handshake offers **must never** trigger OS toasts, audio pings, or unsolicited modal popups.
    - **Strict Isolation from DMs**: Inbound unsolicited requests **must never** appear in the Direct Messages list. They are isolated entirely within this panel.
    - Clicking opens the **Pending Requests Panel**:
      - Lists incoming invitations with requester display name and cryptographic fingerprint.
      - Actions per request:
        - **Accept**: Completes the X3DH / Double Ratchet cryptographic session and creates an active DM in the Direct Messages list.
        - **Reject**: Silently dismisses the invitation without informing the sender.
        - **Block** (formerly "Burn"): Permanently blacklists the peer ID and ingress route per `SYS-BLOCK-WIRE-INDISTINGUISHABILITY` (subsequent wire packets drop silently with zero cryptographic or TCP feedback).
- **Empty State (Left Sidebar)**:
  - When no relays are configured, displays a subtle dashed placeholder guiding the user to the `+` button: *"Add a relay to discover public groups and post offices"*.

---

### Add Connection Dialog (`+` Modal)

The modal is cleanly focused on outbound connection workflows, split into two dedicated tabs:

#### Tab 1: Connect to Peer (Direct P2P)
- **Input Field**:
  - Preceded by a static UI prefix label: `percolator://`.
  - The input box prompts: *"Enter invitation code or link"*.
  - **Automatic Prefix Stripping**: If the user pastes a string containing `percolator://`, the application silently strips the prefix, keeping the text field clean.
  - **Paste from Clipboard Button**: Dedicated button next to the input allowing instant one-click insertion from the system clipboard.
- **Invitation Protocol & Port Ranges**:
  - Accepts standard `percolator://invite?...` links or raw base64 invitation codes containing `PublicIdentityId`, `BlindedRoutingToken`, `DeliveryToken`, and endpoint hints.
  - **Port Range Support**: Invitations can specify:
    1. A single dedicated port (e.g. `port=5123`).
    2. A range of ports to scan/try sequentially (e.g. `ports=5100-5120`).
    3. No port parameter, in which case the desktop client defaults to probing the standard Percolator dynamic port range (`5100-5299`).
- **Progressive Feedback & Offline Handling**:
  - Submitting initiates an immediate cryptographic handshake probe with a progressive status indicator (*"Resolving endpoint..."* -> *"Probing network route..."*).
  - **Fast Offline Timeout (2–3 seconds)**:
    - If the remote peer is unreachable within 2–3 seconds, the dialog **does not** fail or trap the user in an error state.
    - Instead, the modal closes and creates an **Outbound-Pending DM Placeholder** in the Direct Messages list.
    - The chat placeholder appears slightly dimmed with a status label: *"Connection pending (peer offline)..."*.
    - In the conversation view, the chat composer is disabled with a non-intrusive banner explaining that Percolator will automatically establish the session when the peer comes online.
    - A lightweight background retry service periodically probes the peer until the handshake completes.

#### Tab 2: Add Relay
- **Input Field**:
  - Static UI prefix label: `percolator://` (or `relay://`). Auto-strips URI schemes if pasted.
  - Prompts: *"Relay host:port (e.g. relay.example.net:50051)"*.
- **No TLS Certificate Pinning**:
  - Transport encryption utilizes self-signed or CA-issued TLS certificates that rotate on restart.
  - Per Percolator security architecture, authentication and message privacy are guaranteed end-to-end via the Signal Double Ratchet and Sealed Sender envelopes, eliminating the need for rigid relay certificate pinning.
- **Strict Single-Persona Isolation**:
  - The relay is registered **strictly and exclusively** for the currently active persona.
  - Multi-persona relay registration from the same client session is prohibited to prevent malicious relays from correlating multiple persona mailboxes to the same client IP address.
- **Prekey Seeding (Opt-in)**:
  - Checkbox toggle: *"Publish prekey bundle to relay for offline messaging"* (Unchecked by default).
  - Respects strict privacy invariants: prekeys are never seeded or broadcast to a third-party relay without explicit user consent.

---

### 2. Right Sidebar (Chats & Conversations)

Displays conversations contextual to the selection in the Left Sidebar:

- **When Direct Messages is Selected**:
  - Lists all 1-to-1 pairwise direct conversations associated with the active persona.
  - Displays conversation avatar, display name, last message snippet, timestamp, and unread badge.
  - **Outbound-Pending Placeholders**: Displays initiated connections waiting for offline peers to respond, with a distinctive status badge (*"Pending..."*).
  - **Strict Inbound Exclusion**: Inbound unsolicited requests **never** appear here; they reside solely in the anchored `[Pending Requests]` panel.
- **When a Relay is Selected**:
  - Lists public and private channels, groups, and relay mailboxes hosted through that specific relay post office.
- **Drag-and-Drop Reordering**:
  - Chats can be reordered vertically with sort order persisted per view.
- **Empty State**:
  - *"No conversations yet. Use the '+' button to connect with a peer or relay."*

---

### 3. Content Area (Conversation & Messaging Experience)

- **Header**:
  - Shows conversation display name, avatar, and active status indicator.
  - **Pending Connection Banner**: For outbound-pending connections, displays an informational banner: *"Waiting for peer to come online. Messages will be queued once connected."*
  - Clicking the header title or avatar opens the **Right-Anchored Profile & Connection Inspector**.
  - Top-right icon button toggles the inspector drawer open and closed.
- **Empty State (Content Area)**:
  - When no conversation is selected, displays a centered welcome screen with the Percolator emblem, active identity display name, and keyboard shortcut cheatsheet (`Ctrl+K` for Quick-Switcher, `Ctrl+Shift+L` to Lock).
- **Message List & Item Presentation (Placeholder)**:
  - Visual and interaction design for sent/received message bubbles (receipts, reactions, quotes, edits, tombstones) will be detailed in a dedicated design turn.
- **P2P Swarm File Transfer Cards (Placeholder)**:
  - In-chat embedded cards representing swarm chunk progress and disk assembly status will be detailed in a dedicated design turn.
- **Chat Composer**:
  - Auto-expanding message text box, emoji reaction flyout, and file attachment drop-zone. Disabled when viewing an unestablished pending connection.
- **Volatile Draft & ViewModel Caching**:
  - The desktop application maintains an in-memory `ChatDraftStateService` and an LRU cache of recently active `ChatViewModel` instances.
  - Switching back and forth between chats or sidebars is instantaneous, restoring unsent message drafts, scroll positions, and attachment states from volatile memory without database re-fetching or schema pollution.

---

### 4. Right-Anchored Profile & Connection Inspector Drawer

Clicking a peer's profile name in the chat header or selecting "View Profile" opens a slide-over panel on the right side of the window.

- **Direct Peer Inspector**:
  - **Profile Summary**:
    - Large avatar (or identicon).
    - Remote display name and copyable truncated Identity ID (`4a8f9c...b12e`).
  - **Disappearing Messages (Signal-style)**:
    - Shows current timer status (*Off*, *30 seconds*, *5 minutes*, *1 hour*, *1 day*, *1 week*, *4 weeks*).
    - Changing the timer applies immediately to all future messages in the chat.
  - **Notification Settings**:
    - Mute toggle with presets: *1 hour*, *8 hours*, *1 day*, *7 days*, or *Always*.
  - **Safety Numbers & Verification (Contextual - `UN-CONTACT-VERIFICATION`, `SYS-KEY-CHANGE-TRUST-HALT`)**:
    - *Not displayed by default* during ordinary conversation.
    - Appears only if the peer has not yet established a completed handshake, or if an **Identity Key Change Alert** is triggered.
    - When visible, displays the 60-digit fingerprint with a "Mark as Verified" toggle.
  - **Connection & Route Telemetry**:
    - Clean technical metadata: Route type (Direct LAN, STUN, or Relayed) and current roundtrip latency/ping.
  - **Contact Actions**:
    - "Clear Chat History" (deletes local messages and tombstones).
    - "Block Contact" (destroys ratchet session, rejects future ingress, and removes from sidebar per `SYS-BLOCK-WIRE-INDISTINGUISHABILITY`).

- **Relay / Group Inspector Variant**:
  - *Relay*: Shows Relay URI, connection status, latency, and active hosted mailboxes for the current persona.
  - *Group*: Shows Group Name, Avatar, Disappearing Message timer, and **Member Roster** (with Admin / Member badges). Clicking a member opens that peer's profile.

---

### 5. Identity Area (Bottom of Left Sidebar & Profile Drawer)

- **Compact Visual Focus**:
  - Anchored at the very bottom of the Left Sidebar.
  - Focus is placed on the local user's **profile picture**.
  - Local display name is compact and truncated with an ellipsis (`...`) to preserve screen real estate.
  - **Do NOT show port status in the identity area.**
  - Clicking the profile picture opens the **Identity & Profile Drawer**.
- **Share My Invitation (Moved from Add Connection)**:
  - Accessible via the Identity & Profile Drawer ("View Profile & Share Invite"):
    - Displays the current persona's out-of-band invitation QR code and copyable `percolator://invite?...` link.
    - **Port Range Privacy Configuration**:
      - Inviter can choose how connection endpoints are shared in generated invitations:
        1. *Default Port Range*: Omits port numbers; invites recipient to probe standard dynamic ports (`5100-5299`).
        2. *Custom Port Range*: Allows specifying a discrete range (e.g. `5100-5120`).
        3. *Single Dedicated Port*: Pins the invitation strictly to the current listening port (e.g. `5123`).
- **Active Identity Switcher Flyout**:
  - The notification indicator (which displays if there are unread messages from other active personas) serves as the click anchor for the **Identity Switcher Flyout**.
  - Clicking it opens a flyout listing all identities currently activated for the session, showing each identity's avatar, display name, and unread badge count.
  - **Strict No-Quick-Jump Rule**: The user must switch to the persona first; clicking an identity switches the shell's active context to that persona. It does not launch directly into a conversation.
  - Once switched, the Left and Right sidebars reactively re-filter to display the newly focused persona's channels, contacts, and chats. Background message processing and outboxes continue running concurrently for all active identities.

---

### QMS Non-Functional Invariant: Local Network Stealth
- **Strict Opt-In Discovery (`SYS-PRE-ACTIVATION-STEALTH`, `INV-UNSOLICITED-CONNECTION-SILENCE`)**:
  - Automatic Wi-Fi / local LAN multicast or broadcast peer discovery is **strictly prohibited** by default to prevent unintentional metadata leakage or network fingerprinting.
  - Automatic peer discovery requires an explicit, opt-in toggle in Persona Settings and is **deferred as out-of-scope** for current milestone deliverables.

---

## Global Quick-Switcher (`Ctrl+K` / `Ctrl+P`)

- Pressing `Ctrl+K` or `Ctrl+P` opens a centered modal Command Palette.
- Provides fast fuzzy-search navigation across:
  - Direct peers.
  - Relays.
  - Group conversations.
  - Quick actions ("Add Connection", "Lock Session").
- Selecting an item switches the active transport and chat view immediately without taking hands off the keyboard.

---

## Session Locking & Teardown (`UN-SESSION-LOCK`, `SYS-LOCK-TEARDOWN-AND-ZEROIZATION`)

- A "Lock Session" action in the shell menu or system tray executes immediate teardown:
  - The unlock window re-appears in total network stealth mode and `MainWindow` closes.
  - Session teardown clears all active Kestrel listener sockets and relay streams, clears all SQLite connection pools (`SqliteConnection.ClearAllPools()`), securely zeroizes the master key in memory (`CryptographicOperations.ZeroMemory`), and disposes the Stage 2 container.
