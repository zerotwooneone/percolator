# Percolator.Desktop Shell Flow Specification

This application is strictly controlled by the QMS system found in `/source/spec`.

---

## Screen 1: Vault Setup & Unlock Gate

Screen 1 serves as the pre-database bootstrap gate. At this point:
- `PercolatorDbContext` is not available.
- **Hidden / Offline Invariant (`SYS-PRE-ACTIVATION-STEALTH`)**: The application operates completely offline. Zero network listener sockets are opened, probed, or bound.
- The window provides standard OS window controls (Close `[X]`, Minimize).
- **Window Geometry & Transition**: Screen 1 and Screen 2 share a cohesive, centered startup window frame ($480 \times 600\text{ px}$) to eliminate awkward screen resizing or window flashing during unlock.

Depending on whether a vault database exists on disk, Screen 1 presents one of two visual states:

### State 1A: First-Time Vault Setup (`DatabaseSetupStatus.Uninitialized`)
Displayed on initial application launch or immediately following decoy duress shredding.
- **Header & Guidance**:
  - Title: *"Welcome to Percolator"* (uses `Theme="{StaticResource TitleTextBlockStyle}"`)
  - Subtitle: *"Choose a master password to protect your messages, keys, and contacts on this device."* (uses `Theme="{StaticResource CaptionTextBlockStyle}"` with `Foreground="{DynamicResource TextFillColorSecondaryBrush}"`)
- **Interactive Controls & Layout**:
  1. `Master Password` input field:
     - `TextBox` with `PasswordChar="•"`.
     - Built-in Fluent password revelation toggle.
  2. `Confirm Password` input field (`PasswordChar="•"`).
  3. `[ Create Vault ]` primary accent button:
     - Uses `Classes="accent"`.
     - Enabled by default if passwords match.
     - **Blank Password Confirmation Dialog**:
       If the user leaves the master password empty and clicks `[ Create Vault ]`:
       - **Dialog Title**: *"Skip password protection?"*
       - **Body**: *"Your vault will still be encrypted with a random key, but that key will be accessible without a password. Anyone with access to this computer can open your messages."*
       - **Actions**: `[ Cancel ]` (secondary), `[ Continue Without Password ]` (primary outline).
  4. `[+ Add Emergency Duress Passphrase]` (expandable progressive disclosure link):
     - Explainer text: *"If forced to unlock Percolator under coercion, entering this passphrase will securely erase your data and open an empty decoy vault."*
     - Input fields: `Duress Passphrase` + `Confirm Duress Passphrase`.
     - Validation: Cannot match the master password; cannot be whitespace-only if expanded.

### State 1B: Returning User Unlock (`DatabaseSetupStatus.Locked`)
Displayed on subsequent launches when an initialized vault exists.
- **Header**: *"Percolator"* / *"Unlock Vault"* (uses `Theme="{StaticResource TitleTextBlockStyle}"`)
- **Interactive Controls & Layout**:
  1. `Master Password` input field:
     - Auto-focused on window presentation.
     - Masked with `PasswordChar="•"`.
     - Pressing `Enter` triggers unlock.
  2. `[ Unlock ]` primary accent button (`Classes="accent"`):
     - Displays an inline loading spinner while Argon2id key derivation runs.
- **Auto-Launch Bypass (Single-Persona Streamlining)**:
  - If the user previously checked *"Always auto-start with this persona"* on Screen 2, and that persona's port is free, successful unlock immediately binds the port and launches directly into Screen 3 (`MainWindow`), bypassing Screen 2.
  - *Bypass Override*: Holding `Shift` while clicking `[ Unlock ]` (or pressing `Shift+Enter`) always forces Screen 2 to appear.
- **Emergency Duress Activation**:
  - If the user enters the configured duress passphrase in the `Master Password` field:
    1. The real vault encryption key and header are cryptographically shredded in $< 2\text{ ms}$.
    2. A new random key is generated and encrypted with the duress passphrase.
    3. An empty, valid decoy SQLite database is initialized.
    4. The application transitions seamlessly into Screen 2 (Empty Vault State) without any alert badges, error popups, or forensic crash signatures.

### Failure Handling & Recovery (Screen 1)
- **Incorrect Password**:
  - Argon2id key derivation completes, but SQLCipher canary query fails (`SQLITE_NOTADB` error `26`).
  - **Feedback**: The password field shakes subtly; an inline error banner appears below the field: *"Incorrect password. Please try again."* (styled using `Foreground="{DynamicResource SystemFillColorCriticalBrush}"`).
  - The input text is highlighted for immediate retyping. The application does not crash or exit.
- **Corrupted Vault File**:
  - Database header is truncated or unparseable.
  - **Modal Dialog**: *"Vault Data Damaged"*.
  - Text: *"The database file could not be opened. It may have been damaged or altered."*
  - Actions: `[ Open Data Folder ]` (opens file explorer to allow restoring from backup), `[ Exit ]`.
- **Cryptographic Engine Failure (Smoke Test Failure)**:
  - Native library bridge fails (`SignalCrypto.TestConnection() != 42`) due to missing OS runtime libraries.
  - **Modal Dialog**: *"Security Engine Error"*.
  - Text: *"The native cryptographic library (`signal_shim`) could not be loaded on your system."*
  - Actions: `[ View Diagnostic Log ]`, `[ Exit ]`.

---

## Screen 2: Identity Gate & Port Binding (Privacy Gatekeeper)

Screen 2 is loaded within the startup window frame after the database smoke test passes.
- **Hidden / Offline Invariant (`SYS-PRE-ACTIVATION-STEALTH`)**: The application **remains hidden from the network** before the user selects personas and confirms activation. Zero Kestrel listeners and zero outbound transport channels are active.

### State 2A: Normal Return (Existing Personas)
- **Header**: *"Select Active Personas"* (`Theme="{StaticResource SubtitleTextBlockStyle}"`)
- **Subheader**: *"You are currently hidden from the network. Your selected personas will only come online after you continue."* (`Foreground="{DynamicResource TextFillColorSecondaryBrush}"`)
- **Persona Cards List**:
  - Each card represents an independent persona (`SYS-ISOLATED-PERSONAS`):
    - Configured with `Classes="persona-card"` with smooth `BrushTransition` on border hover.
    - Checkbox (multi-select enabled).
    - Persona Avatar (or generated identicon) + Display Name (`Theme="{StaticResource BodyStrongTextBlockStyle}"`).
    - **Network Port Pill**:
      - Available: `Port 5100` [Green Dot: Ready, using `SystemFillColorSuccessBrush`].
      - Auto-Resolved Conflict: `Port 5100 in use → Auto-selected 5101` [Orange Dot, using `SystemFillColorCautionBrush` with explanation tooltip].
      - `[ ✎ ]` (Edit Port button, using `Theme="{StaticResource TransparentButton}"`): Opens an inline popover with a number field to specify a custom port outside the default range.
  - Card Context Menu (right-click or `···` kebab menu):
    - `Edit Profile`: Modifies display name and avatar.
    - `Delete Persona`: Opens the permanent removal confirmation dialog.
- **Auto-Launch Preference**:
  - Below the list, a checkbox appears when 1 persona is selected:
    `[ ] Always auto-start with this persona on unlock`
  - Persisted in local desktop settings (`DesktopIdentitySettingsEntity`).
- **Action Bar**:
  1. `[ + New Persona ]` secondary button (opens the Add Persona modal).
  2. `[ Launch Selected Personas → ]` primary accent button (`Classes="accent"`):
     - Displays count of selected personas (e.g. `[ Launch 2 Selected Personas → ]`).
     - Disabled if zero personas are checked, or if any selected persona has an unresolved port collision.

### State 2B: Empty Vault (First-Time User or Decoy Duress Vault)
Displayed when no personas exist in the vault.
- **Visual Presentation**:
  - Centered welcome card using `CardBackgroundFillColorDefaultBrush` and `CardStrokeColorDefaultBrush`.
  - Headline: *"No personas yet"* (`Theme="{StaticResource SubtitleTextBlockStyle}"`).
  - Body: *"Percolator lets you create separate profiles for different parts of your life."*
  - **Tip Callout**: *"Tip: Many people create separate personas for Work vs. Personal, or Family vs. Friends."*
  - Action Button: `[ Create Your First Persona ]` (`Classes="accent"`).
  - The `[ Launch Selected Personas → ]` button remains hidden until at least one persona is created.

### Persona Management Dialogs (Screen 2)

#### Add Persona Modal
- Title: *"Create New Persona"*
- `Display Name` input field (auto-focused, max 32 characters, whitespace-trimmed).
- Avatar selector: Displays a default generated identicon; includes `[ Choose Picture ]` button to select a local `.png` or `.jpg`.
- `Network Port` (expandable advanced settings): Displays auto-assigned port (e.g. `5100`).
- Actions: `[ Cancel ]`, `[ Create Persona ]` (primary accent button, disabled until a display name is entered).

#### Delete Persona Modal (`SYS-ISOLATED-PERSONAS`, `UN-DATA-RETENTION`)
- Title: *"Permanently Delete Persona?"*
- Body:
  *"This action cannot be undone. All private cryptographic keys, direct chat histories, channels, and contacts associated with this persona will be permanently shredded from disk."*
- **Selectable Persona Name Box**:
  - Displays the exact persona name in a selectable, highlighted text box so power users can easily select and copy it with keyboard or mouse.
- **Confirmation Input**:
  - Text field with prompt: *"Type the persona name to confirm:"*
- Actions:
  - `[ Cancel ]` (secondary).
  - `[ Shred & Delete Persona ]` (destructive button using `Classes="danger"`; disabled until typed text matches the persona name exactly).

### Failure Handling & Recovery (Screen 2)
- **All Ports in Range Occupied**:
  - If all ports in the application's default range (`5100`–`5299`) are already in use:
    - Persona card displays an inline alert pill: `[ Port conflict: All default ports busy ]` (`Foreground="{DynamicResource SystemFillColorCriticalBrush}"`).
    - Clicking opens the port editor prompting the user to enter any free port $> 1024$.
    - `[ Launch Selected Personas → ]` remains disabled until valid ports are assigned.
- **Port Race Condition on Launch**:
  - If a port was free during initial selection but was claimed by another OS application before `Launch` was pressed:
    - An inline banner appears: *"Port 5101 was claimed by another application. Re-probing next available port..."*
    - `IPortReservationService` automatically re-leases the next free port and updates the card.
- **Zero Personas Selected**:
  - If the user unchecks all personas, `[ Launch Selected Personas → ]` is disabled and helper text reads: *"Select at least one persona to continue."*

### Transition to Shell
1. User confirms persona selection.
2. The application acquires leases for the selected ports via `IPortReservationService` and starts Kestrel transport listeners strictly for the chosen personas.
3. The startup window closes, and Screen 3 (`MainWindow`) opens, restoring the user's previously saved window position, size, and maximized state.

---

## Screen 3: Main Application Shell

Screen 3 is a modern, responsive desktop shell inspired by Discord and Microsoft Teams.

### Responsive Design Principles
- **Window Geometry & Adaptation**:
  - Minimum supported window size: $800 \times 600\text{ px}$. Default target: $1200 \times 800\text{ px}$.
  - The shell remembers and restores window dimensions, monitor coordinates, and maximized state across restarts.
  - **Narrow / Compact Screen Adaptation**:
    - When window width $< 1000\text{ px}$, the right-anchored inspector drawer acts as an overlay/drawer with a backdrop dismiss scrim rather than pushing the content area.
    - When window width $< 850\text{ px}$, the Right Sidebar collapses into a slide-out drawer accessible via a hamburger toggle.

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
  5. Re-opens Screen 1 (`UnlockWindow`) in total network stealth mode.

---

## Idiomatic Avalonia & Fluent Theming Architecture

The application strictly adheres to the **Avalonia 11+ / FluentAvalonia 2.1** styling architecture to ensure visual consistency, seamless Light/Dark/High-Contrast variant switching, and native OS desktop fidelity.

### 1. Minimalist Composition Root (`App.axaml`)
- `FluentAvaloniaTheme` replaces Avalonia's default theme engine and serves as the single source of truth for styles and control themes:
  ```xml
  <Application xmlns="https://github.com/avaloniaui"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               xmlns:sty="using:FluentAvalonia.Styling"
               xmlns:local="using:Percolator.Desktop"
               x:Class="Percolator.Desktop.App"
               RequestedThemeVariant="Default">

      <Application.DataTemplates>
          <local:ViewLocator />
      </Application.DataTemplates>

      <Application.Resources>
          <ResourceDictionary>
              <ResourceDictionary.MergedDictionaries>
                  <ResourceInclude Source="avares://Percolator.Desktop/Common/Styles/Spacing.axaml" />
              </ResourceDictionary.MergedDictionaries>
          </ResourceDictionary>
      </Application.Resources>

      <Application.Styles>
          <!-- FluentAvalonia v2 Theme Engine (Auto-detects OS Dark/Light & Accent Color) -->
          <sty:FluentAvaloniaTheme PreferUserAccentColor="True" 
                                   PreferSystemTheme="True" />
                                   
          <!-- Application-specific utility overrides -->
          <StyleInclude Source="avares://Percolator.Desktop/Common/Styles/CustomStyles.axaml" />
      </Application.Styles>
  </Application>
  ```
- **Zero Theme Mixing**: `<FluentTheme />` or `<SimpleTheme />` are strictly prohibited alongside `<sty:FluentAvaloniaTheme />` to prevent property leakage and conflicting resource definitions.

### 2. Native Fluent Design Tokens (Zero Arbitrary Color Inventions)
- The application directly references standard WinUI / Fluent v2 design tokens via `{DynamicResource ...}` so all backgrounds, borders, and typography adapt automatically across Light, Dark, and High-Contrast modes without custom palette maintenance:
  - **Surfaces & Cards**:
    - `SolidBackgroundFillColorBaseBrush` (Main window background)
    - `CardBackgroundFillColorDefaultBrush` (Elevated card surfaces)
    - `LayerOnAcrylicFillColorDefaultBrush` (Translucent header/navigation layers)
  - **Borders & Dividers**:
    - `CardStrokeColorDefaultBrush` (Card outlines)
    - `DividerStrokeColorDefaultBrush` (Subtle separator lines)
  - **Typography (Text Brushes & Built-in TextBlock Themes)**:
    - `TextFillColorPrimaryBrush` (Headings, primary body)
    - `TextFillColorSecondaryBrush` (Muted hints, subtitles, timestamps)
    - `TextFillColorTertiaryBrush` (Placeholder / watermark text)
    - `Theme="{StaticResource TitleTextBlockStyle}"` (28pt SemiBold)
    - `Theme="{StaticResource SubtitleTextBlockStyle}"` (20pt SemiBold)
    - `Theme="{StaticResource BodyStrongTextBlockStyle}"` (14pt SemiBold)
    - `Theme="{StaticResource BodyTextBlockStyle}"` (14pt Regular)
    - `Theme="{StaticResource CaptionTextBlockStyle}"` (12pt Secondary)
  - **Accent & Status Colors**:
    - `AccentFillColorDefaultBrush` (Primary buttons, active indicators)
    - `SystemFillColorSuccessBrush` (Ready / verified indicators)
    - `SystemFillColorCautionBrush` (Port collision / warnings)
    - `SystemFillColorCriticalBrush` (Errors, destructive actions)

### 3. Layout & Geometry Tokens (`Common/Styles/Spacing.axaml`)
- Centralized immutable metrics matching Windows 11 Fluent v2 layout grids:
  ```xml
  <ResourceDictionary xmlns="https://github.com/avaloniaui"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <CornerRadius x:Key="OverlayCornerRadius">8</CornerRadius>
      <CornerRadius x:Key="CardCornerRadius">6</CornerRadius>
      <CornerRadius x:Key="ControlCornerRadius">4</CornerRadius>
  </ResourceDictionary>
  ```

### 4. Application Modifiers (`Common/Styles/CustomStyles.axaml`)
- Reusable classes for behaviors unique to Percolator (e.g. destructive actions and smooth card transitions):
  ```xml
  <Styles xmlns="https://github.com/avaloniaui"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

      <!-- Destructive Action Button -->
      <Style Selector="Button.danger">
          <Setter Property="Background" Value="{DynamicResource SystemFillColorCriticalBrush}" />
          <Setter Property="Foreground" Value="White" />
      </Style>
      <Style Selector="Button.danger:pointerover /template/ ContentPresenter">
          <Setter Property="Background" Value="{DynamicResource SystemFillColorCriticalBackgroundBrush}" />
      </Style>

      <!-- Persona Card with Smooth Border Transition -->
      <Style Selector="Border.persona-card">
          <Setter Property="Background" Value="{DynamicResource CardBackgroundFillColorDefaultBrush}" />
          <Setter Property="BorderBrush" Value="{DynamicResource CardStrokeColorDefaultBrush}" />
          <Setter Property="BorderThickness" Value="1" />
          <Setter Property="CornerRadius" Value="{StaticResource CardCornerRadius}" />
          <Setter Property="Padding" Value="16,12" />
          <Setter Property="Transitions">
              <Transitions>
                  <BrushTransition Property="BorderBrush" Duration="0:0:0.15" />
              </Transitions>
          </Setter>
      </Style>
      <Style Selector="Border.persona-card:pointerover">
          <Setter Property="BorderBrush" Value="{DynamicResource AccentFillColorDefaultBrush}" />
      </Style>
  </Styles>
  ```
- **Control Themes for Buttons & Inputs**:
  - Primary action buttons use `Classes="accent"`.
  - Icon buttons (kebab menus, edit icons) use `Theme="{StaticResource TransparentButton}"`.
  - Password inputs use native `PasswordChar="•"` with built-in Fluent reveal mechanics.
