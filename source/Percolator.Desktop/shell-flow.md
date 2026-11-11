# Percolator.Desktop Shell Flow Specification

This application is strictly controlled by the QMS system found in `/source/spec`.

---

## Main Application Shell

The main application shell is a modern, responsive desktop shell inspired by Discord and Microsoft Teams.

### Responsive Design Principles
- **Window Geometry & Adaptation**:
  - Minimum supported window size: $800 \times 600\text{ px}$. Default target: $1200 \times 800\text{ px}$.
  - The shell remembers and restores window dimensions, monitor coordinates, and maximized state across restarts.
  - **Narrow / Compact Screen Adaptation**:
    - When window width $< 1000\text{ px}$, the right-anchored inspector drawer acts as an overlay/drawer with a backdrop dismiss scrim rather than pushing the content area.
    - When window width $< 850\text{ px}$, the Right Sidebar collapses into a slide-out drawer accessible via a hamburger toggle.

---

### 1. Left Sidebar (Transports / Network Channels)
- **Top of Left Sidebar: Pending Requests Button**:
  - An unobtrusive icon button located at the very top of the Left Sidebar (similar to Discord's Direct Messages / Friends button).
  - Displays a subtle notification indicator light when incoming unsolicited handshake requests are pending.
  - **Zero Distraction Privacy Invariant (`INV-UNSOLICITED-CONNECTION-SILENCE`, `SYS-CONTACT-CONSENT-GATE`)**: Inbound unsolicited connections or handshake offers **must never** interrupt the user with OS toasts, audio pings, or popup modals.
  - Clicking opens the **Pending Requests Panel** showing inbound requests:
    - Requester display name and public key fingerprint.
    - Actions: **Accept** (initiates session and creates chat), **Reject** (silently dismisses), or **Burn** (permanently blacklists the peer ID and route per `SYS-BLOCK-WIRE-INDISTINGUISHABILITY`).
- **Transport Channels List (Direct Peers & Relays)**:
  - Displays direct peer connections combined with connected relays.
  - **Visual Presentation**:
    - Each direct peer displays their profile picture (or a deterministic git-like identicon if no avatar is set).
    - Each relay displays a distinct relay icon / badge.
    - **Zero Technical Clutter**: Do not show IP addresses, ports, or protocol strings in the sidebar list.
    - **Hover Tooltip**: Hovering the mouse over an item displays the remote peer or relay display name in a clean tooltip (like Discord's server list).
    - **Reordering**: Users can drag and drop items up and down the list; sort order is persisted in the local desktop database.
    - **Right-Click Context Menu**:
      - *Direct Peer*: "View Profile" (opens the right-anchored inspector drawer), "Mute", "Remove Connection".
      - *Relay*: "Relay Details" (opens the right-anchored inspector drawer), "Reconnect", "Remove Relay".
    - **Selection**: Clicking an item updates the Right Sidebar with that transport channel's contextual chats.
- **Bottom of Left Sidebar: Add Connection (`+`) Button**:
  - A prominent `+` button at the bottom of the transport list (similar to Discord's "Add a Server").
  - Clicking opens the **Add Connection Dialog**:
    - **Connect Outbound (Tab 1)**:
      - Input field to enter a peer endpoint URL or relay URI.
      - *Direct Peer*: Submitting initiates an outbound X3DH handshake. The peer immediately appears in the Left Sidebar with a subtle `Connecting...` status.
      - *Relay*: Submitting validates connectivity over TOFU TLS, verifies gRPC service availability (`AuthenticatedRelayService` / `AnonymousGroupRelayService`), and registers the relay into the sidebar.
    - **Share My Invitation (Tab 2)**:
      - Generates and displays the current identity's out-of-band invitation payload (URL and QR code) containing its listening endpoint, public identity key, and ephemeral prekey material for a peer to scan or import.
- **Empty State (Left Sidebar)**:
  - If no direct peers or relays exist (e.g. on fresh install or decoy duress vault), displays a subtle dashed placeholder guiding the user to the `+` button: *"Connect to a peer or relay to start messaging"*.

---

### 2. Right Sidebar (Chats & Conversations)
- Displays the list of chats **contextual to the item selected in the Left Sidebar**:
  - **When a Direct Peer is selected**: Exactly one 1-to-1 chat is shown. If no prior chat exists, the chat is created when the user sends or receives the first message.
  - **When a Relay is selected**: Shows all individual chats and group chats known and routed via that relay.
- **Visual Presentation**:
  - Displays conversation name, avatar, unread message badge count, and last message timestamp/snippet.
  - **Zero Technical Clutter**: Do not display route labels like `(Relayed 1-on-1)` in the list items.
  - Drag-and-drop reordering with database-persisted positions.
  - **Right-Click Context Menu**: Allows actions related to the chat item ("View Details", "Mute Notifications", "Clear History", "Leave Group").
  - Clicking an item loads that conversation into the Content Area.
- **Empty State (Right Sidebar)**:
  - *"No conversations yet. Send a message to start chatting."*
- **Group Creation Workflow (Placeholder)**:
  - *Technical limitation*: All members of a group must be reachable on a shared relay.
  - The workflow must detect which peers are already present on the relay; for invitees not yet on the relay, the system orchestrates out-of-band/pairwise relay invitations, awaiting peer join confirmations before dispatching the Groups V2 ZK presentation invitation.
  - Full group invitation and epoch negotiation workflow will be detailed in a dedicated design turn.

---

### 3. Content Area (Conversation & Messaging Experience)
- **Header**:
  - Shows the conversation display name and avatar.
  - **Zero Technical Clutter**: Do not display route tags in the header.
  - Clicking the header title or avatar opens the **Right-Anchored Profile & Connection Inspector**.
  - Top-right icon button toggles the inspector drawer open and closed.
- **Empty State (Content Area)**:
  - When no conversation is selected, displays a clean centered welcome screen with the Percolator emblem, active identity display name, and a keyboard shortcut cheatsheet (`Ctrl+K` for Quick-Switcher, `Ctrl+Shift+L` to Lock).
- **Message List & Item Presentation (Placeholder)**:
  - The detailed visual and interaction design for sent/received message bubbles (states, receipts, reactions, quotes, edits, tombstones) deserves its own dedicated design turn.
- **P2P Swarm File Transfer Cards (Placeholder)**:
  - In-chat embedded cards representing multi-stream swarm chunk progress, privacy policy opt-in gates, and disk assembly status will be detailed in a dedicated design turn.
- **Chat Composer**:
  - Auto-expanding message text box, emoji reaction flyout, and file attachment drop-zone.
- **Volatile Draft & ViewModel Caching**:
  - The desktop application maintains an in-memory `ChatDraftStateService` and an LRU cache of recently active `ChatViewModel` instances.
  - Switching back and forth between chats or sidebars is instantaneous, restoring unsent message drafts, scroll positions, and attachment states from volatile memory without database re-fetching or schema pollution.

---

### 4. Right-Anchored Profile & Connection Inspector Drawer
Inspired by Signal Desktop, Microsoft Teams, and Discord, clicking a peer's profile name in the chat header or selecting "View Profile" from a sidebar context menu opens a slide-over panel on the right side of the window.

- **Direct Peer Inspector**:
  - **Profile Summary**:
    - Large avatar (or identicon).
    - Remote display name and copyable truncated Identity ID (`4a8f9c...b12e`).
  - **Disappearing Messages (Signal-style)**:
    - Shows current timer status (*Off*, *30 seconds*, *5 minutes*, *1 hour*, *1 day*, *1 week*, *4 weeks*).
    - Clicking allows changing the expiration timer, which applies immediately to all future messages in the chat.
  - **Notification Settings**:
    - Mute toggle with presets: *1 hour*, *8 hours*, *1 day*, *7 days*, or *Always*.
  - **Safety Numbers & Verification (Contextual - `UN-CONTACT-VERIFICATION`, `SYS-KEY-CHANGE-TRUST-HALT`)**:
    - *Not displayed by default* during ordinary conversation.
    - Appears only if the peer has not yet established a completed handshake, or if an **Identity Key Change Alert** is triggered (notifying the user that the remote peer's public key has changed, warning of potential reinstallation or MITM).
    - When visible, displays the 60-digit fingerprint with a "Mark as Verified" toggle.
  - **Connection & Route Telemetry**:
    - Clean technical metadata: Route type (Direct LAN, STUN, or Relayed) and current roundtrip latency/ping.
  - **Contact Actions**:
    - "Clear Chat History" (deletes local messages and tombstones).
    - "Block & Burn Contact" (destroys ratchet session, rejects future ingress, and removes from sidebar).

- **Relay / Group Inspector Variant**:
  - *Relay*: Shows Relay URI, connection status, latency, and active hosted mailboxes.
  - *Group*: Shows Group Name, Avatar, Disappearing Message timer, and **Member Roster** (with Admin / Member badges). Clicking a member opens that peer's profile.

---

### 5. Identity Area (Bottom of Left Sidebar, Spanning Right Sidebar)
- **Compact Visual Focus**:
  - Focus is placed on the local user's **profile picture**.
  - The local display name is compact and truncated with an ellipsis (`...`) like Discord to preserve screen real estate.
  - **Do NOT show port status in the identity area.**
  - The profile picture is clickable and opens the **Identity & Key Management** form (display name, avatar, cryptographic fingerprints, and key export).
- **Active Identity Switcher Flyout**:
  - The notification indicator (which displays if there are unread messages from other active identities) serves as the click anchor for the **Identity Switcher Flyout**.
  - Clicking it opens a flyout listing all identities currently activated for the session, showing each identity's avatar, display name, and unread badge count.
  - **Strict No-Quick-Jump Rule**: The user must switch to the identity first; clicking an identity switches the shell's active context to that persona. It does not launch directly into a conversation.
  - Once switched, the Left and Right sidebars reactively re-filter to display the newly focused identity's channels, contacts, and chats. Background message processing and outboxes continue running concurrently for all active identities.

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
  1. Tears down all active Kestrel listener sockets and disconnects all relay streams.
  2. Clears all SQLite connection pools (`SqliteConnection.ClearAllPools()`).
  3. Securely zeroizes the master key in memory (`CryptographicOperations.ZeroMemory`).
  4. Disposes the Stage 2 container and closes `MainWindow`.
  5. Re-opens the unlock window in total network stealth mode.
