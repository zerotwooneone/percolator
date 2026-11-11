# Percolator.Desktop Implementation Plan

---

## 1. Executive Summary & Core Architectural Invariants

`Percolator.Desktop` is the cross-platform desktop UI for the Percolator peer-to-peer ecosystem, targeting **Windows, Linux, and macOS** using **Avalonia UI 11.2**, **FluentAvalonia 2.1**, **CommunityToolkit.Mvvm 8.4**, **R3 1.3.1**, **R3Extensions.Avalonia 1.3.1**, and **ObservableCollections.R3 3.3.4** on **.NET 10**.

### 1.1 Architectural & Security Invariants

1. **Two-Stage Container Architecture & Zero-Repository Pre-Unlock Gate**:
   * **Unified Encrypted SQLite (`percolator.db`)**: All application data resides in a single database encrypted with SQLCipher AES-256-CBC with WAL mode.
   * **Bootstrap Container (Root DI)**: On startup, `App.axaml.cs` builds a lightweight container hosting only core presentation and bootstrap infrastructure (`IWindowManager`, `IWindowHandoffCoordinator`, `ICryptoEngine`, `IDateTimeProvider`, and `IDatabaseUnlockCoordinator`). **Zero SQLite DbContexts, repositories, or background sync workers exist in memory prior to unlock.**
   * **Unified Startup Frame ($480 \times 600\text{ px}$)**:
     * Screen 1 (Vault Setup / Unlock) and Screen 2 (Identity Gate) share a single centered window frame to eliminate screen flashing or awkward window resizing during startup.
     * *First-Time Setup (`DatabaseSetupStatus.Uninitialized`)*: Guides the user to create a master password and optional emergency duress passphrase. An empty master password (`""`) triggers an explicit confirmation dialog before vault initialization.
     * *Subsequent Launches (`DatabaseSetupStatus.Locked`)*: Presents the passphrase field requiring explicit unlock.
     * *Duress Kill Switch & Decoy Transition*: Entering an established duress passphrase cryptographically shreds the real vault key and header in $< 2\text{ ms}$, creates a valid decoy database encrypted with the duress key, and transitions seamlessly into Screen 2 (State 2B: Empty Vault) with zero error popups, alert badges, or forensic crash indicators.
   * **Canary Verification & Post-Unlock Migration**:
     * Derives a 256-bit candidate key using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$) and evaluates AEAD slots.
     * Validates passphrase with an isolated canary query `SELECT count(*) FROM sqlite_master;`, explicitly trapping native error code `26` (`SQLITE_NOTADB`).
     * On success, pending EF Core / SQLite migrations (`Database.MigrateAsync()`) execute immediately before any domain repositories are resolved.
   * **Authenticated Host (`IHost`)**:
     * Instantiates a child `IHost` containing `PercolatorDbContext`, connection pools with native `SQLitePCL.raw.sqlite3_key` interceptor, domain repositories, and background workers.
     * `await session.StartAsync()` automatically discovers and launches all registered `IHostedService` instances.
   * **Window Handoff & Relocking**:
     * On identity selection confirmation, `StartupWindow` closes and `MainWindow` ($1200 \times 800\text{ px}$) opens, restoring user window geometry.
     * Session lock or app exit triggers `session.StopAsync()`, flushes connection pools via `SqliteConnection.ClearAllPools()`, zeroizes the key buffer via `CryptographicOperations.ZeroMemory`, disposes the authenticated `IHost`, closes `MainWindow`, and re-opens `StartupWindow` in Screen 1B unlock state.
2. **Total Network Stealth Startup Invariant (`SYS-PRE-ACTIVATION-STEALTH`)**:
   * On launch, the application operates in **total network stealth**—zero sockets bound, zero UDP DHT packets, zero STUN probes, and zero Kestrel/gRPC listeners active.
   * Sockets bind **only after** database unlock and explicit persona confirmation in Screen 2 (`IdentityGateView`).
   * Session lock or deactivating all personas immediately terminates all listening sockets and tears down active network sessions, returning the application to stealth mode.
3. **Inbound Connection Silence Invariant (`INV-UNSOLICITED-CONNECTION-SILENCE`, `SYS-CONTACT-CONSENT-GATE`)**:
   * Inbound unsolicited connections or handshake offers **must never** interrupt the user with OS toasts, audio pings, or popup modals.
   * Inbound requests illuminate a subtle indicator light on the Pending Requests button at the top of the Left Sidebar; user review is entirely pull-based.
4. **Domain Purity & Presentation Model Isolation**:
   * `Percolator.Domain` is strictly isolated from UI dependencies (zero UI interfaces, zero dispatcher calls, zero snapshotting methods on domain entities).
   * All reactive UI models live exclusively in `Percolator.Desktop.Features.*.Models`.
   * Presentation models implement `.ToSnapshot()` returning immutable C# `record` types for lock-free background reads, caching, and export.
5. **State Ownership Boundary (Stateful UI Services vs. Local ViewModels)**:
   * **Local State (Owned by ViewModels)**: Transient view state (input text, validation errors, accordion expansion, dialog visibility, scroll offsets) is private to the ViewModel.
   * **Shared State (Owned by Stateful UI Services)**: Cross-cutting application state (active identities, peer sessions, message queues, DHT discovery tables, swarm file transfers) is owned exclusively by singleton/scoped **Stateful UI Services** (`IdentityStateService`, `PeerConnectionStateService`, `ChatStateService`, `DiscoveryStateService`, `FileTransferStateService`, `TimelineCardStateService`).
   * Stateful UI Services are **100% UI-agnostic**—they operate on background worker threads, guard mutations with concurrency gates (`SemaphoreSlim`), and expose canonical `ObservableList<T>` / `ObservableDictionary<TKey, TValue>` collections.
6. **Volatile Draft & LRU ViewModel Caching**:
   * The desktop application maintains an in-memory `ChatDraftStateService` and an LRU cache of recently active `ChatViewModel` instances (capacity 3–5).
   * Switching between chats or personas restores unsent message drafts, scroll positions, and attachment states from volatile memory without database re-fetching or schema pollution.
7. **Strict R3 Reactive Programming Rules**:
   * **Avalonia Dispatcher Integration**: Configured in `Program.cs` via `.UseR3()`, wiring `AvaloniaDispatcherTimeProvider` and `AvaloniaDispatcherFrameProvider` into `ObservableSystem`.
   * **Filter Early, Marshal Late**: Perform filtering, deduplication, debounce, and sorting on background worker threads; only marshal to the UI thread at the terminal step via `.ObserveOn(AvaloniaSynchronizationContext.Current)`.
   * **Throttle the Firehose**: High-frequency streaming events (network packet rates, DHT search hops, file piece bitfields) must be sampled (`.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)`) to protect the UI thread from 60fps+ inundation.
   * **Removal Disposal Pattern**: Every projected view created with `presentationList.CreateView(m => new ItemViewModel(m))` must explicitly observe item removal and dispose the projected ViewModel:
     ```csharp
     _view.ObserveRemove().Subscribe(e => e.Value.View.Dispose()).AddTo(ref Bag);
     ```
8. **Deterministic Virtualized Time Invariant**:
   * Replace all wall-clock `Task.Delay` and real-time timers with `System.TimeProvider`.
   * Enables instantaneous, deterministic unit and UI testing via `FakeTimeProvider` without wall-clock sleep delays.
9. **Zero Managed Key Strings & Memory Hygiene**:
   * Master passwords and candidate passphrases entered in UI text boxes are promptly transferred to `VaultPassphrase` instances and scrubbed from managed strings where feasible.
   * Key material is never formatted into SQL connection strings (`PRAGMA key = ...`); the UI delegates all crypto and session handling to `IDatabaseUnlockCoordinator`.

---

## 2. Technology Stack & Component Architecture

| Layer | Component | Description |
|---|---|---|
| **Presentation Framework** | Avalonia UI 11.2 | Hardware-accelerated, cross-platform XAML engine |
| **Theme & Controls** | FluentAvaloniaUI 2.1 | WinUI 3 / Fluent v2 styling: NavigationView, ContentDialog, CommandBar |
| **Database Security Gate** | `IDatabaseUnlockCoordinator` | SQLCipher master key derivation, canary verification, decoy duress DB creation |
| **Session Lifecycle** | `IWindowHandoffCoordinator` | Orchestrates two-stage DI transition, startup-to-shell window swap, and secure relock |
| **Privacy & Security** | `INetworkStealthGate` | Gated listener activation ensuring zero open ports until persona confirmation |
| **Port Leasing** | `IPortReservationService` | Dynamic port availability probing, conflict detection, and auto-leasing |
| **Draft Caching** | `IChatDraftStateService` | Volatile in-memory draft, scroll offset, and attachment caching |
| **Iconography** | Segoe Fluent Icons + FiraCode Nerd Font | Fluent icons for UI controls; embedded Nerd Font for P2P mesh & route telemetry |
| **Custom UI Primitives** | `PeerAvatar`, `StatusChip`, `Badge`, `PieceMapControl` | Reusable controls for presence, route pills, notification badge, and chunk canvas |
| **Session Scoping** | `SessionScopeFactory` | LRU-cached child DI scopes for active conversation ViewModels |
| **Data Synchronization** | `ReloadCoordinator` | Debounced reactive query batching with `AwaitOperation.Drop` |
| **MVVM Tooling** | CommunityToolkit.Mvvm 8.4 | Source-generated `[ObservableProperty]`, `[RelayCommand]` |
| **Reactive Pipelines** | R3 (v1.3.1) + R3Extensions.Avalonia | Reactive pipelines and Avalonia Dispatcher providers |
| **Collection Sync** | ObservableCollections.R3 (v3.3.4) | Synchronized collection projections and free-threaded `ObservableList<T>` |
| **Hosting & DI** | `Microsoft.Extensions.Hosting` | Two-stage dependency injection (`BootstrapContainer` vs. `AuthenticatedHost`) |
| **Domain & App Core** | `Percolator.Domain`, `Percolator.Application` | Core messaging, cryptographic identities, session managers |
| **App Plugins** | `Percolator.Apps.Chat`, `Percolator.Apps.Discovery`, `Percolator.Apps.FileTransfer` | Modular P2P feature applications |

---

## 3. UI Directory Structure & Layout

```
Percolator.Desktop/
├── Assets/                               # Embedded resources & fonts
│   └── Fonts/
│       └── FiraCodeNerdFontMono-Regular.ttf
├── Common/
│   ├── Behaviors/                        # Badge.cs (attached properties for count & pulse)
│   ├── Controls/                         # NerdGlyph.cs, NerdIcon.cs, NerdIconSource.cs, PeerAvatar, StatusChip
│   ├── Converters/                       # IdentityHashToBrushConverter, RouteToColorConverter
│   ├── Navigation/                       # INavigationService.cs, NavigationService.cs
│   ├── Services/                         # NetworkStealthGate, PortReservationService, SystemDateTimeProvider
│   ├── Storage/                          # DesktopIdentitySettingsEntity.cs, IDesktopSettingsRepository.cs
│   ├── Styles/                           # Spacing.axaml, CustomStyles.axaml
│   └── Windowing/                        # IWindowManager.cs, WindowManager.cs, IWindowHandoffCoordinator.cs, WindowHandoffCoordinator.cs
├── Features/
│   ├── Startup/                          # Screen 1 & Screen 2 Container ($480x600)
│   │   ├── Models/                       # StartupStateModel.cs, SelectablePersonaModel.cs
│   │   ├── ViewModels/                   # StartupHostViewModel.cs, VaultSetupViewModel.cs, VaultUnlockViewModel.cs, IdentityGateViewModel.cs, CreatePersonaDialogViewModel.cs, DeletePersonaDialogViewModel.cs
│   │   └── Views/                        # StartupWindow.axaml, VaultSetupView.axaml, VaultUnlockView.axaml, IdentityGateView.axaml, CreatePersonaDialog.axaml, DeletePersonaDialog.axaml
│   ├── Shell/                            # Screen 3 Main Shell Frame ($1200x800)
│   │   ├── Models/                       # QuickSwitcherItemModel.cs
│   │   ├── ViewModels/                   # MainWindowViewModel.cs, QuickSwitcherViewModel.cs
│   │   └── Views/                        # MainWindow.axaml, QuickSwitcherDialog.axaml
│   ├── Transports/                       # Screen 3 Left Sidebar (Transports, Relays, Pending Handshakes)
│   │   ├── Models/                       # TransportChannelModel.cs, PendingHandshakeModel.cs
│   │   ├── Services/                     # IPeerConnectionStateService.cs, PeerConnectionStateService.cs
│   │   ├── ViewModels/                   # TransportsSidebarViewModel.cs, PendingRequestsPanelViewModel.cs, AddConnectionDialogViewModel.cs
│   │   └── Views/                        # TransportsSidebarView.axaml, PendingRequestsPanelView.axaml, AddConnectionDialog.axaml
│   ├── Chats/                            # Screen 3 Right Sidebar (Contextual Chats) & Messaging View
│   │   ├── Controls/                     # ChatComposer.axaml, ReactionPickerFlyout.axaml
│   │   ├── Models/                       # ConversationSummaryModel.cs, ChatMessageModel.cs, ChatMessageSnapshot.cs
│   │   ├── Services/                     # IChatStateService.cs, ChatStateService.cs, IChatDraftStateService.cs, ChatDraftStateService.cs, ChatReloadCoordinator.cs
│   │   ├── ViewModels/                   # ChatsSidebarViewModel.cs, ChatViewModel.cs, ChatMessageViewModel.cs, GroupCreationDialogViewModel.cs
│   │   └── Views/                        # ChatsSidebarView.axaml, ChatView.axaml, GroupCreationDialog.axaml
│   ├── Inspector/                        # Screen 3 Right-Anchored Inspector Drawer (Signal-inspired)
│   │   ├── Models/                       # PeerProfileInspectorModel.cs, RelayInspectorModel.cs
│   │   ├── ViewModels/                   # ProfileInspectorViewModel.cs
│   │   └── Views/                        # ProfileInspectorView.axaml
│   ├── Discovery/                        # Sovereign Peer Discovery & Telemetry
│   │   ├── Models/                       # DiscoveredPeerModel.cs, BlindedRendezvousModel.cs, SwarmTelemetryModel.cs
│   │   ├── Services/                     # IDiscoveryStateService.cs, DiscoveryStateService.cs
│   │   ├── ViewModels/                   # DiscoveryViewModel.cs, DiscoveredPeerItemViewModel.cs
│   │   └── Views/                        # DiscoveryView.axaml
│   ├── FileTransfer/                     # P2P Swarm File Sharing & In-Chat Cards
│   │   ├── Controls/                     # PieceMapControl.axaml (direct DrawingContext bitfield rendering)
│   │   ├── Models/                       # FileTransferTaskModel.cs, TransferBitfieldSnapshot.cs
│   │   ├── Services/                     # IFileTransferStateService.cs, FileTransferStateService.cs
│   │   ├── ViewModels/                   # FileTransferViewModel.cs, TransferTaskItemViewModel.cs
│   │   └── Views/                        # FileTransferView.axaml
│   ├── Timeline/                         # Plugin Timeline & Card Host
│   │   ├── Models/                       # TimelineCardModel.cs, TimelineActionModel.cs
│   │   ├── Services/                     # ITimelineCardStateService.cs, TimelineCardStateService.cs, DesktopTimelinePostSink.cs, DesktopPluginActionHandler.cs
│   │   ├── ViewModels/                   # TimelineViewModel.cs, TimelineCardViewModel.cs
│   │   └── Views/                        # TimelineView.axaml
│   └── Settings/                         # Identity, Security, Network & Display Settings
│       ├── ViewModels/                   # SettingsViewModel.cs
│       └── Views/                        # SettingsView.axaml
├── ViewModels/
│   └── ViewModelBase.cs                  # ObservableObject + DisposableBag base class
├── App.axaml                             # Styles, FluentAvalonia theme, merged resource dictionaries
├── App.axaml.cs                          # App bootstrap, R3 unhandled exceptions, DI composition root
├── Program.cs                            # Avalonia entry point with .UseR3()
└── Percolator.Desktop.csproj
```

---

## 4. Sequential Implementation Chunks

```mermaid
flowchart TD
    subgraph Phase 1: Shell & Theming Bootstrap
        C1["Chunk 1: Application Shell, Theming & Responsive 3-Pane Frame"]
        C2["Chunk 2: Pre-Unlock Vault Gate (StartupWindow Screen 1A & 1B + Duress Decoy)"]
        C3["Chunk 3: Multi-Persona Gate & Stealth Port Binding (Screen 2 + DesktopIdentitySettings)"]
        C4["Chunk 4: Two-Stage DI & Window Handoff Coordinator (Lifecycle & Relocking)"]
    end

    subgraph Phase 2: Transports, Messaging & Inspection
        C5["Chunk 5: Two-Tier Sidebar, Add Connection & Pending Requests (Signal Inspector)"]
        C6["Chunk 6: Real-Time Chat, Volatile Drafts & Disappearing Messages (Apps.Chat)"]
    end

    subgraph Phase 3: P2P Swarm & Platform Services
        C7["Chunk 7: Sovereign Discovery & Swarm Telemetry (Apps.Discovery)"]
        C8["Chunk 8: Swarm File Transfer Dashboard & In-Chat Cards (Apps.FileTransfer)"]
        C9["Chunk 9: Timeline Cards & PluginSdk Integration (PluginSdk)"]
        C10["Chunk 10: Desktop Tray, Silent Notifications & Quick-Switcher (Ctrl+K)"]
        C11["Chunk 11: Headless UI & Concurrency Verification Test Suite"]
    end

    C1 --> C2 --> C3 --> C4 --> C5 --> C6 --> C7 --> C8 --> C9 --> C10 --> C11
```

---

### Chunk 1: Application Shell, Theming & Responsive 3-Pane Frame

**Goal**: Deliver the visual foundation of the application so `dotnet run` boots immediately into a responsive, fully themed 3-pane Discord/Teams-inspired shell frame with design tokens, embedded fonts, and responsive layout behavior.

#### Tasks
- [ ] Implement `Common/Styles/Spacing.axaml`:
  - Centralized Fluent v2 geometry tokens: `CardCornerRadius` (6), `OverlayCornerRadius` (8), `ControlCornerRadius` (4).
- [ ] Implement `Common/Styles/CustomStyles.axaml`:
  - `Button.danger` destructive button style (`SystemFillColorCriticalBrush`).
  - `Border.persona-card` with `BrushTransition` on border hover.
- [ ] Configure `App.axaml`:
  - Single theme engine: `<sty:FluentAvaloniaTheme PreferUserAccentColor="True" PreferSystemTheme="True" />`.
  - Zero theme mixing (no `<FluentTheme>` or `<SimpleTheme>`).
  - Link `Spacing.axaml` and `CustomStyles.axaml`.
- [ ] Link embedded Nerd Font definitions in `App.axaml` and declare `NerdFontFamily`.
- [ ] Implement `Features/Shell/Views/MainWindow.axaml` & `Features/Shell/ViewModels/MainWindowViewModel.cs`:
  - Responsive Grid Layout:
    - Left Sidebar ($72\text{ px}$ fixed): Top pending requests button placeholder, middle transport items placeholder, bottom `+` add button, and compact identity area.
    - Right Sidebar ($240\text{ px}$ collapsible): Contextual chat list placeholder.
    - Content Area: Header, message list placeholder, chat composer placeholder.
    - Right-Anchored Inspector Drawer ($300\text{ px}$ slide-over): Slide-over inspector panel.
  - Responsive layout breakpoints:
    - Window width $< 1000\text{ px}$: Inspector drawer switches to an overlay drawer with backdrop dismiss scrim.
    - Window width $< 850\text{ px}$: Right Sidebar collapses into a slide-out drawer with hamburger toggle.
  - Window state preservation: minimum size $800 \times 600\text{ px}$, default target $1200 \times 800\text{ px}$.

#### Verification
- App compiles and runs cleanly with `dotnet run`.
- FluentAvalonia Light/Dark/System variant switching works automatically.
- Window resizing across $1200\text{ px} \rightarrow < 1000\text{ px} \rightarrow < 850\text{ px}$ triggers responsive layout adaptations smoothly without visual glitches.

---

### Chunk 2: Pre-Unlock Vault Gate (`StartupWindow` Screen 1A & 1B + Duress Decoy)

**Goal**: Deliver the pre-database security gate in a dedicated $480 \times 600\text{ px}$ startup frame, supporting first-time setup, returning user unlock, blank-password confirmation dialogs, and instant decoy duress vault initialization.

#### Tasks
- [ ] Implement `Features/Startup/Views/StartupWindow.axaml` & `StartupHostViewModel.cs`:
  - Fixed-size, non-resizable centered frame ($480 \times 600\text{ px}$) hosting Screen 1 and Screen 2 views via dynamic view locator transition.
- [ ] Implement `Features/Startup/Views/VaultSetupView.axaml` & `VaultSetupViewModel.cs` (State 1A):
  - Master Password + Confirm Password fields (`PasswordChar="•"` with built-in reveal toggle).
  - Expandable `[+ Add Emergency Duress Passphrase]` progressive disclosure link.
  - Validation: Duress cannot match master password; cannot be whitespace-only.
  - Blank Password Dialog: If password is empty, displays `ContentDialog`: *"Skip password protection? Your vault will still be encrypted with a random key, but that key will be accessible without a password."* with actions `[ Cancel ]` and `[ Continue Without Password ]`.
  - Calls `IDatabaseUnlockCoordinator.InitializeAsync(masterPassword, duressPassword, ct)`.
- [ ] Implement `Features/Startup/Views/VaultUnlockView.axaml` & `VaultUnlockViewModel.cs` (State 1B):
  - Auto-focused master password field with show/hide toggle.
  - Inline loading spinner during Argon2id key derivation.
  - Error feedback: subtle shake animation and critical inline text (*"Incorrect password. Please try again."*) on native error `26` (`SQLITE_NOTADB`).
  - Auto-launch preference bypass: If user enabled auto-start for a persona, unlock immediately launches `MainWindow`. Holding `Shift` (or `Shift+Enter`) overrides bypass and forces Screen 2 to appear.
  - Emergency Duress Activation: Entering duress passphrase cryptographically shreds real header in $< 2\text{ ms}$, creates a valid decoy SQLite DB encrypted with duress key, and transitions smoothly into Screen 2 (State 2B: Empty Vault) with zero error popups or forensic crash signatures.

#### Verification
- Fresh install displays `VaultSetupView`.
- Initializing with empty password prompts confirmation dialog.
- Subsequent launch displays `VaultUnlockView`.
- Incorrect password displays inline error without crashing.
- Duress passphrase wipes vault and smoothly presents empty vault state.

---

### Chunk 3: Multi-Persona Gate & Stealth Port Binding (Screen 2)

**Goal**: Deliver Screen 2 within the startup window frame, enforcing total network stealth until persona confirmation, displaying per-persona network port status pills, auto-resolving port conflicts, and supporting persona CRUD.

#### Tasks
- [ ] Implement `Common/Storage/DesktopIdentitySettingsEntity.cs` & `IDesktopSettingsRepository`:
  - Local desktop database entity (PK `IdentityId`, `PreferredPort`, `LastBoundPort`, `AutoStart`, `SidebarOrder`).
  - Stores UI preferences and network bindings without polluting domain `IdentityProfile`.
- [ ] Implement `Common/Services/PortReservationService.cs` (`IPortReservationService`):
  - Probes local TCP/UDP port availability in default range `5100`–`5299`.
  - Auto-assigns next free port on conflict.
- [ ] Implement `Features/Startup/Views/IdentityGateView.axaml` & `IdentityGateViewModel.cs`:
  - **State 2A (Normal Return)**:
    - Multi-select persona cards (`Border.persona-card`).
    - Port status pill: Green dot (`Port 5100 Ready`), Orange dot (`Port 5100 in use → Auto-selected 5101`), Red alert pill (`All default ports busy`).
    - Inline port editor popover (`[ ✎ ]`) to customize port.
    - Single-persona auto-start checkbox: `[ ] Always auto-start with this persona on unlock`.
    - `[ Launch Selected Personas → ]` primary button (disabled if zero checked or conflict unresolved).
  - **State 2B (Empty Vault)**:
    - Centered card: *"No personas yet"*, with friendly tip callout: *"Tip: Many people create separate personas for Work vs. Personal, or Family vs. Friends."*
    - `[ Create Your First Persona ]` action button.
- [ ] Implement `CreatePersonaDialog.axaml` & `CreatePersonaDialogViewModel.cs`:
  - Display name (max 32 chars), avatar picker with identicon generator, and advanced port settings.
- [ ] Implement `DeletePersonaDialog.axaml` & `DeletePersonaDialogViewModel.cs`:
  - Selectable persona name box so power users can copy with keyboard/mouse.
  - Confirmation prompt requiring user to type exact persona name to enable `[ Shred & Delete Persona ]` (`Button.danger`).

#### Verification
- Launching on empty/decoy vault shows State 2B with tip callout.
- Creating persona persists keypair in `ILocalIdentityKeyStore`, profile in `IIdentityProfileRepository`, and settings in `DesktopIdentitySettingsEntity`.
- Simulating port conflict updates pill to orange with auto-selected next port.
- Deleting persona shreds keys and removes profile only when typed name matches exactly.

---

### Chunk 4: Two-Stage DI & Window Handoff Coordinator

**Goal**: Wire the two-stage dependency injection architecture (`BootstrapContainer` $\rightarrow$ `AuthenticatedSession`), coordinating window swaps between `StartupWindow` and `MainWindow`, and handling secure session relocking with memory zeroization.

#### Tasks
- [ ] Configure `App.axaml.cs` Two-Stage DI:
  - Stage 1 `BootstrapContainer`: Minimal services (`IWindowManager`, `IWindowHandoffCoordinator`, `ICryptoEngine`, `IDateTimeProvider`, `IDatabaseUnlockCoordinator`, `StartupHostViewModel`).
  - Stage 2 `AuthenticatedSession`: Instantiated only after successful canary verification, containing `PercolatorDbContext`, connection pools, domain repositories, and hosted services.
- [ ] Implement `Common/Windowing/WindowHandoffCoordinator.cs` (`IWindowHandoffCoordinator`):
  - `TransitionToGateAsync(IAuthenticatedSession session)`: Transitions `StartupWindow` from Screen 1 to Screen 2.
  - `TransitionToMainShellAsync(IReadOnlyList<DesktopActivePersona> activePersonas)`:
    - Acquires port leases via `IPortReservationService` and starts Kestrel listeners strictly for chosen personas.
    - Closes `StartupWindow`.
    - Resolves and displays `MainWindow` from authenticated container, restoring saved geometry.
  - `LockSessionAsync()`:
    - Deactivates network listeners via `INetworkStealthGate.DeactivateAsync()`.
    - Stops `IHost`, flushes SQLite pools (`SqliteConnection.ClearAllPools()`), and zeroizes master key span (`CryptographicOperations.ZeroMemory`).
    - Closes `MainWindow`, instantiates fresh `StartupWindow` in State 1B unlock state.

#### Verification
- App boots cleanly in Stage 1 container without opening SQLite connections or listening ports.
- Successful unlock transitions to Screen 2 inside `StartupWindow`.
- Confirming persona launch closes `StartupWindow`, binds ports, and opens `MainWindow`.
- Locking session tears down sockets, clears connection pools, zeroizes memory, and re-opens `StartupWindow`.

---

### Chunk 5: Two-Tier Sidebar, Add Connection & Pending Requests (Signal Inspector)

**Goal**: Implement the Slack/Discord-inspired Two-Tier Sidebar in `MainWindow`, the unobtrusive pending requests panel, the Add Connection dialog, and the right-anchored Signal-inspired inspector drawer.

#### Tasks
- [ ] Left Sidebar (`Features/Transports/Views/TransportsSidebarView.axaml` & `TransportsSidebarViewModel.cs`):
  - **Top: Pending Requests Button**:
    - Unobtrusive icon button with subtle notification light.
    - **Inbound Connection Silence Invariant**: Zero OS toasts, popups, or audio pings on unsolicited inbound handshakes.
    - Opens `PendingRequestsPanelView.axaml`: Displays requester display name and fingerprint with `Accept`, `Reject`, and `Burn` (permanent blacklist) actions.
  - **Middle: Transport Channels List**:
    - Direct peer profile picture (or identicon) and relay badges.
    - Zero technical clutter (no IP addresses, ports, or protocol strings in the list).
    - Mouse hover shows remote display name in clean tooltip.
    - Reorderable via drag-and-drop (persisted in `DesktopIdentitySettingsEntity`).
    - Right-click context menus: "View Profile", "Mute", "Remove Connection" / "Relay Details", "Reconnect".
  - **Bottom: Add Connection (`+`) Button**:
    - Prominent `+` button opening `AddConnectionDialog.axaml`:
      - Tab 1 (*Connect Outbound*): Enter peer endpoint or relay URI; validates connectivity and initiates handshake.
      - Tab 2 (*Share My Invitation*): Generates and displays active identity's out-of-band invitation URL and QR code.
  - **Identity Area (Bottom of Left Sidebar)**:
    - Focus placed on local user's profile picture; local display name truncated with ellipsis (`...`).
    - Clicking profile picture opens Identity & Key Management.
    - Unread indicator acts as anchor for **Active Identity Switcher Flyout**: switches shell context to another active persona without quick-jumping directly into a chat.
- [ ] Right Sidebar (`Features/Chats/Views/ChatsSidebarView.axaml` & `ChatsSidebarViewModel.cs`):
  - Displays chats contextual to selected Left Sidebar transport (single 1-on-1 chat for direct peer; individual and group channels for relays).
  - Conversation avatar, display name, unread badge count, and last message snippet.
  - Zero technical clutter (no route tags like `(Relayed 1-on-1)`).
  - Right-click context menu: "View Details", "Mute Notifications", "Clear History", "Leave Group".
- [ ] Right-Anchored Profile & Connection Inspector (`Features/Inspector/Views/ProfileInspectorView.axaml` & `ProfileInspectorViewModel.cs`):
  - Direct Peer Inspector (Signal-inspired):
    - Profile summary: avatar, remote name, copyable truncated Identity ID.
    - Disappearing Messages selector: presets (*Off, 30s, 5m, 1h, 1d, 1w, 4w*).
    - Notification settings: Mute toggle (*1h, 8h, 1d, 7d, Always*).
    - Contextual Safety Numbers (`SYS-KEY-CHANGE-TRUST-HALT`): Hidden by default; visible only if handshake is incomplete or an Identity Key Change Alert is triggered. Displays 60-digit fingerprint with "Mark as Verified" toggle.
    - Connection & route telemetry: Route type (Direct LAN, STUN, Relayed) and roundtrip ping.
    - Contact actions: "Clear Chat History", "Block & Burn Contact".
  - Relay / Group Inspector Variant:
    - Relay URI, connection status, latency, hosted mailboxes.
    - Group name, avatar, disappearing timer, member roster with Admin/Member badges.

#### Verification
- Hovering over Left Sidebar transport icons displays clean name tooltips without IP/port clutter.
- Inbound unsolicited handshake illuminates pending requests indicator without firing OS toasts.
- Clicking pending requests opens panel; accepting establishes chat in Right Sidebar; burning blacklists peer.
- Clicking profile name in chat header slides open Right-Anchored Inspector Drawer.
- Safety numbers panel remains hidden during normal chat, but appears if key change is simulated.

---

### Chunk 6: Real-Time Chat, Volatile Drafts & Disappearing Messages (`Percolator.Apps.Chat` Integration)

**Goal**: Deliver a high-performance messaging interface integrating `Percolator.Apps.Chat`, featuring volatile message draft caching, LRU ViewModel scoping, Signal-style disappearing messages, and inline reactions.

#### Tasks
- [ ] Presentation Models in `Features/Chats/Models/`:
  - `ChatMessageModel`: `MessageId`, `SessionId`, `SenderId`, `SenderDisplayName`, `ContentText`, `TimestampUtc`, `DeliveryStatus` (`Queued`, `Sending`, `Sent`, `Delivered`, `Read`, `Failed`), `IsOutgoing`, `IsEdited`, `IsDeleted`, `ExpiresAtUtc`, `Reactions` (`ObservableList<ReactionModel>`), `Attachments` (`ObservableList<MediaAttachmentModel>`).
  - `ChatMessageSnapshot` immutable `record` for lock-free background reads, search indexing, and export.
- [ ] Implement `Features/Chats/Services/ChatDraftStateService.cs` (`IChatDraftStateService`):
  - In-memory volatile store for unsent drafts, pending attachments, and conversation scroll offsets.
  - Instantaneous draft recovery when switching between chats or identities without database re-fetching or schema pollution.
- [ ] `SessionScopeFactory`:
  - Manages child `IServiceScope` instances for active conversation tabs.
  - LRU cache with capacity = 5; evicts and disposes least recently used scope upon opening 6th conversation.
- [ ] `ChatReloadCoordinator`:
  - Debounces message arrivals and receipt updates by `50ms` with `AwaitOperation.Drop` to coalesce SQLite roundtrips.
- [ ] `ChatComposer.axaml` & `ChatComposer.axaml.cs`:
  - Auto-expanding `TextBox` (1 to 6 lines max).
  - Keyboard handling: `Enter` sends message; `Shift+Enter` or `Ctrl+Enter` inserts newline.
  - Attachment button triggering file picker flyout.
  - Emoji reaction picker flyout.
- [ ] `ChatView.axaml` & `ChatViewModel.cs`:
  - Virtualized message list with smooth scroll-to-bottom on new messages.
  - Asymmetric message bubbles: Right-aligned accent bubble for outgoing messages; left-aligned surface bubble for incoming messages.
  - Message status icons: Single tick (Sent), Double tick (Delivered), Colored double tick (Read), Exclamation (Failed).
  - Inline message editor and tombstone (*"This message was deleted"*) placeholder.
  - Reaction summary pills with click-to-toggle reaction.
  - Disappearing Messages Reaper: Monitors `ExpiresAtUtc` and triggers tombstone purging upon timer expiration.
- [ ] Group Creation Placeholder (`GroupCreationDialog.axaml` & `GroupCreationDialogViewModel.cs`):
  - Group creation UI requiring all members to be on a shared relay.
  - Orchestrates pairwise relay invitations and awaits peer join confirmations before dispatching Groups V2 ZK invitations.

#### Verification
- Typing an unsent message in Chat A, navigating to Chat B, and returning to Chat A restores draft text and scroll position.
- Opening 6 consecutive chats evicts the 1st chat's `IServiceScope`, triggering disposal of its subscriptions.
- Sending a message enqueues `SendMessageCommand` in `Percolator.Apps.Chat` and renders an outgoing bubble in `Queued` state.
- Disappearing message expiration triggers local deletion and tombstone update.

---

### Chunk 7: Sovereign Peer Discovery & Swarm Telemetry (`Percolator.Apps.Discovery` Integration)

**Goal**: Deliver a live peer discovery dashboard and network swarm inspector integrating `Percolator.Apps.Discovery`, showing candidate peers, NAT traversal status, roundtrip latency, and private blinded rendezvous.

#### Tasks
- [ ] Presentation Models in `Features/Discovery/Models/`:
  - `DiscoveredPeerModel`: `PeerId`, `DisplayName`, `Endpoint`, `NatTraversalStatus` (`DirectPublic`, `StunTraversed`, `RelayedOnly`), `RoundtripLatencyMs`, `DiscoveredViaRelayId`, `DiscoveredAtUtc`.
  - `BlindedRendezvousModel`: `CurrentEpochNumber`, `BlindedLocatorHex`, `NextEpochTransitionUtc`, `MatchedContactsCount`.
  - `SwarmTelemetryModel`: `ActivePeersCount`, `KnownRelaysCount`, `AverageLatencyMs`, `BandwidthInBps`, `BandwidthOutBps`.
- [ ] `IDiscoveryStateService` / `DiscoveryStateService`:
  - Bridges `INearbyPeerDiscoveryWorkflow`, `IPrivateRendezvousService`, and `IRelayPeerDirectory`.
  - Throttles candidate event stream using `.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)` to protect the UI thread from high-frequency candidate packet bursts.
- [ ] `DiscoveryView.axaml` & `DiscoveryViewModel.cs`:
  - Candidate peer cards with public key preview, avatar hash, NAT status badge, and latency meter.
  - One-click "Connect / Handshake" action triggering on-demand prekey retrieval and X3DH handshake.
  - Blinded Rendezvous Panel: 24-hr UTC epoch countdown timer and matched sovereign contacts count.
  - Sovereign Relay Selection dropdown allowing users to select which relay directory to query.

#### Verification
- High-frequency discovery stream (100 candidates/sec) is sampled down to 60fps without freezing UI thread.
- Clicking "Connect" on a candidate invokes `INearbyPeerDiscoveryWorkflow` to retrieve Signed PreKey and initiate handshake.
- Latency meter updates reactively via `BindableReactiveProperty`.

---

### Chunk 8: High-Performance Swarm File Transfer Dashboard & In-Chat Cards (`Percolator.Apps.FileTransfer` Integration)

**Goal**: Deliver a comprehensive swarm file transfer manager integrating `Percolator.Apps.FileTransfer`, featuring real-time chunk piece-map rendering, drag-and-drop file sharing, in-chat transfer cards, and safe atomic disk staging.

#### Tasks
- [ ] Presentation Models in `Features/FileTransfer/Models/`:
  - `FileTransferTaskModel`: `TransferId`, `FileName`, `TotalSizeBytes`, `TransferredSizeBytes`, `TransferProgress` ($0.0 \dots 1.0$), `TransferSpeedBps`, `EtaTimeSpan`, `SwarmPeersCount`, `Direction` (`Download` vs `Upload`), `Status` (`Queued`, `Transferring`, `Verifying`, `Paused`, `Completed`, `Failed`, `Canceled`).
  - `TransferBitfieldSnapshot`: Immutable snapshot of piece bitfield array for high-performance rendering.
- [ ] `IFileTransferStateService` / `FileTransferStateService`:
  - Bridges `IFileTransferQueryService`, `ITransferSessionRepository`, and `DiskChunkAssembler`.
  - Exposes `ObservableList<FileTransferTaskModel> ActiveTransfers` and `ObservableList<FileTransferTaskModel> CompletedTransfers`.
  - Rate-samples progress updates via `.Sample(TimeSpan.FromMilliseconds(50), _timeProvider)`.
- [ ] `PieceMapControl.axaml` & `PieceMapControl.axaml.cs`:
  - Custom Avalonia control rendering a graphical bitfield piece grid on an Avalonia `DrawingContext`.
  - Visual states: Empty cell (missing), Accent fill (downloaded & verified), Pulse amber (in-flight request).
  - Handles 10,000+ pieces efficiently via direct rectangle drawing operations without creating individual XAML visual elements.
  - Reusable: Designed to render both in the full dashboard and embedded inside in-chat file transfer bubbles.
- [ ] `FileTransferView.axaml` & `FileTransferViewModel.cs`:
  - Active transfers list with file icon, transfer speed meter (KB/s, MB/s), ETA timer, and swarm peer count badge.
  - Action buttons: Pause, Resume, Cancel, Open Containing Folder.
  - Drag-and-drop target supporting Avalonia `DragDrop.DropEvent`.
  - File picker button integrated with Avalonia `TopLevel.StorageProvider`.
- [ ] Integration with `IDesktopFileStorage` (`PortableDesktopFileStore`):
  - Immediate zero-byte file allocation upon transfer acceptance.
  - `.percolator-part` staging and safe atomic rename on verification completion.
  - Path sanitization enforcing zero Win32 reserved device names (`CON`, `PRN`, `AUX`, `NUL`), zero NTFS alternate data streams (`:`), and Unicode Form C normalization.

#### Verification
- Dropping a 50 MB file onto chat or dashboard creates a transfer manifest and stages upload.
- Inbound transfer acceptance creates zero-byte file and dynamically renders `PieceMapControl` as chunks arrive.
- Completed transfers rename `.percolator-part` staging files to target file names atomically.

---

### Chunk 9: Timeline Cards & Plugin Integration (`Percolator.PluginSdk`)

**Goal**: Deliver an extensible Timeline Card notification center and concrete Desktop adapters for `Percolator.PluginSdk` (`ITimelinePostSink` and `IPluginActionHandler`), allowing plugins to surface interactive cards and handle action callbacks.

#### Tasks
- [ ] Presentation Models in `Features/Timeline/Models/`:
  - `TimelineCardModel`: `CardId`, `SourceAppId`, `Title`, `SummaryText`, `IconGlyph`, `TimestampUtc`, `Metadata` (`IReadOnlyDictionary<string, string>`), `Actions` (`ObservableList<TimelineActionModel>`).
  - `TimelineActionModel`: `ActionId`, `ButtonText`, `CustomPayload`, `IsDestructive`, `Command`.
- [ ] `ITimelineCardStateService` / `TimelineCardStateService`:
  - Manages active timeline cards posted by plugins.
  - Exposes `ObservableList<TimelineCardModel> Cards`.
  - Concurrency-guarded methods for adding, updating, and dismissing cards.
- [ ] Concrete Desktop Adapters for `Percolator.PluginSdk`:
  - `DesktopTimelinePostSink` implementing `Percolator.PluginSdk.ITimelinePostSink`:
    - `PublishCardAsync`: Translates DTO to `TimelineCardModel` and pushes to `ITimelineCardStateService`.
    - `UpdateCardStatusAsync`: Updates existing card content reactively.
    - `RemoveCardAsync`: Dismisses card.
  - `DesktopPluginActionHandlerDispatcher` implementing `Percolator.PluginSdk.IPluginActionHandler`:
    - Dispatches card action button clicks back to target plugin's registered action handler.
- [ ] `TimelineView.axaml` & `TimelineViewModel.cs`:
  - Feed of interactive cards with header, source app icon, summary text, metadata chips, and action buttons.
  - Dismiss button removing card from view and invoking `ITimelinePostSink.RemoveCardAsync`.

#### Verification
- Calling `ITimelinePostSink.PublishCardAsync` from a plugin displays a new interactive card in `TimelineView`.
- Clicking a card action button executes command and invokes `IPluginActionHandler.HandleActionAsync` with matching payload.
- Dismissing a card removes it from the UI and invokes `RemoveCardAsync` cleanly.

---

### Chunk 10: Desktop System Integration, Native Notifications & Quick-Switcher (`Ctrl+K`)

**Goal**: Provide native operating system desktop polish, including minimize-to-tray background listening, native OS notifications respecting privacy invariants, window geometry persistence, and the Global Quick-Switcher.

#### Tasks
- [ ] Global Quick-Switcher (`Ctrl+K` / `Ctrl+P`):
  - Centered modal Command Palette (`QuickSwitcherDialog.axaml` & `QuickSwitcherViewModel.cs`).
  - Fuzzy-search navigation across direct peers, relays, group chats, and commands ("Add Connection", "Lock Session").
  - Selecting an item switches transport and conversation focus immediately.
- [ ] System Tray Integration (`TrayIcon`):
  - Cross-platform system tray icon configured in `App.axaml`.
  - Context menu: "Open Percolator", "Lock Session (Stealth)", "Exit".
  - Minimize-to-tray on close when enabled in settings.
- [ ] Desktop Notifications Bridge:
  - Cross-platform in-app notification toasts (`FluentAvalonia.UI.Controls.InfoBar` banner).
  - Platform bridge: WinRT Toast notifications on Windows, D-Bus notifications on Linux, Notification Center on macOS.
  - **Inbound Handshake Silence Invariant Enforcement**: Inbound unsolicited handshakes never fire OS toasts or audio. Only messages in established chats or system security alerts trigger toasts.
- [ ] Window State Preservation:
  - Persist window position, width, height, and maximized state across restarts in `DesktopIdentitySettingsEntity`.

#### Verification
- Pressing `Ctrl+K` opens Quick-Switcher; fuzzy searching a peer and hitting Enter navigates directly to that conversation.
- Closing window with "Minimize to tray" enabled hides window to notification area without terminating background services.
- Inbound chat message triggers OS notification toast when window is not focused; inbound unsolicited handshake remains silent.

---

### Chunk 11: Headless UI & Concurrency Verification Test Suite

**Goal**: Establish an automated headless UI test project (`Percolator.Desktop.Tests`) using `Avalonia.Headless.XUnit` to verify ViewModels, stealth mode invariants, LRU scope eviction, and reactive reload coordinators without spinning up native OS windows.

#### Tasks
- [ ] Scaffold `Percolator.Desktop.Tests` project:
  - Package references: `Avalonia.Headless.XUnit`, `Microsoft.NET.Test.Sdk`, `xunit`, `FluentAssertions`, `NSubstitute`, `R3`.
  - Headless test bootstrap: `[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`.
- [ ] Vault Gate & Two-Stage Lifecycle Headless Tests:
  - Test `VaultSetupViewModel`:
    - Assert `DetermineStatusAsync` returning `DatabaseSetupStatus.Uninitialized` presents setup view.
    - Assert empty password prompts confirmation dialog alert.
    - Assert duress password matching master password fails validation.
    - Assert successful initialization calls `IWindowHandoffCoordinator.TransitionToGateAsync`.
  - Test `VaultUnlockViewModel`:
    - Assert `DetermineStatusAsync` returning `DatabaseSetupStatus.Locked` presents unlock view.
    - Assert invalid passphrase sets inline error message without crashing.
    - Assert valid passphrase calls `IWindowHandoffCoordinator.TransitionToGateAsync` (or `TransitionToMainShellAsync` if auto-start enabled).
    - Assert duress passphrase triggers decoy initialization and transitions into empty vault state without error popups.
  - Test `WindowHandoffCoordinator`:
    - Assert `TransitionToMainShellAsync` starts hosted services, opens `MainWindow`, and closes `StartupWindow`.
    - Assert `LockSessionAsync` awaits `coordinator.LockAsync()`, closes `MainWindow`, and opens fresh `StartupWindow`.
- [ ] Stealth Mode & Network Gate Verification Tests:
  - Assert zero network sockets are initialized upon test app startup.
  - Assert sockets bind only after `IdentityGateViewModel.LaunchPersonasCommand.Execute()`.
  - Assert locking session triggers `INetworkStealthGate.DeactivateAsync()`.
- [ ] `SessionScopeFactory` LRU Cache Tests:
  - Simulate opening 6 consecutive chat sessions; verify the first session's `IServiceScope` is disposed and hot memory contains exactly 5 scopes.
- [ ] `ChatReloadCoordinator` Debounce & Drop Tests:
  - Using `FakeTimeProvider`, simulate 100 rapid reload triggers in 10ms; assert database read service is invoked exactly once after 50ms advance.
- [ ] `ChatDraftStateService` Volatile Caching Tests:
  - Verify draft text and attachment lists persist across conversation switches without touching SQLite.
- [ ] Synchronized View Projection & Disposal Tests:
  - Verify `ObserveRemove` triggers `Dispose()` on projected ViewModel when item is removed from `ObservableList`.

#### Verification
- All headless tests run and pass via `dotnet test Percolator.Desktop.Tests` in $< 5\text{ seconds}$.
- Zero cross-thread exceptions or unhandled reactive exceptions thrown during test execution.

---

## 5. Port-to-Adapter Realization Matrix

This matrix maps all infrastructure ports and desktop adapters across their owning chunks, ensuring zero architectural disconnects between domain, infrastructure, plugins, and UI.

| Interface / Port | Layer / Source | Concrete Realization / Adapter | Chunk |
| :--- | :--- | :--- | :--- |
| `IDatabaseUnlockCoordinator` | Application Ports | `SqlCipherDatabaseUnlockCoordinator` (Infrastructure) | Chunk 2 |
| `IAuthenticatedSession` | Application Ports | `AuthenticatedSession` (Infrastructure) | Chunk 4 |
| `IWindowHandoffCoordinator` | Desktop Windowing | `WindowHandoffCoordinator` | Chunk 4 |
| `INavigationService` | Desktop Shell | `NavigationService` | Chunk 1 |
| `IWindowManager` | Desktop Shell | `WindowManager` | Chunk 1 |
| `INetworkStealthGate` | Desktop Core | `NetworkStealthGate` | Chunk 3 |
| `IPortReservationService` | Desktop Core | `PortReservationService` | Chunk 3 |
| `IDesktopSettingsRepository`| Desktop Storage | `SqliteDesktopSettingsRepository` | Chunk 3 |
| `IDesktopIdentityKeyStore` | Desktop Core | `DesktopLocalIdentityKeyStore` | Chunk 3 |
| `IIdentityProfileRepository` | Domain Identities | `DesktopIdentityProfileRepository` (SQLite) | Chunk 3 |
| `ILocalIdentityKeyStore` | Domain Identities | `DesktopLocalIdentityKeyStore` (SQLite) | Chunk 3 |
| `IPeerConnectionStateService` | Desktop Transports | `PeerConnectionStateService` | Chunk 5 |
| `IPeerContactQueryService` | Application Delivery | `SqlPeerContactQueryService` (Infrastructure) | Chunk 5 |
| `IPendingHandshakeRepository`| Domain Delivery | `SqlitePendingHandshakeRepository` | Chunk 5 |
| `IHandshakeService` | Application Handshake| `HandshakeService` | Chunk 5 |
| `IChatStateService` | Desktop Chats | `ChatStateService` | Chunk 6 |
| `IChatDraftStateService` | Desktop Chats | `ChatDraftStateService` | Chunk 6 |
| `IChatService` | Apps Chat | `ChatService` | Chunk 6 |
| `IChatMessageQueryService` | Apps Chat | `SqlChatMessageQueryService` (Infrastructure) | Chunk 6 |
| `IConversationListQueryService`| Apps Chat | `SqlConversationListQueryService` (Infrastructure)| Chunk 6 |
| `IDiscoveryStateService` | Desktop Discovery | `DiscoveryStateService` | Chunk 7 |
| `INearbyPeerDiscoveryWorkflow` | Apps Discovery | `NearbyPeerDiscoveryWorkflow` | Chunk 7 |
| `IPrivateRendezvousService` | Apps Discovery | `PrivateRendezvousService` | Chunk 7 |
| `IRelayPeerDirectory` | Apps Discovery | `SqliteRelayPeerDirectory` (Infrastructure) | Chunk 7 |
| `IFileTransferStateService` | Desktop FileTransfer| `FileTransferStateService` | Chunk 8 |
| `IFileTransferQueryService` | Apps FileTransfer | `FileTransferQueryService` | Chunk 8 |
| `ITransferSessionRepository` | Apps FileTransfer | `SqliteTransferSessionRepository` (Infrastructure)| Chunk 8 |
| `IDesktopFileStorage` | Apps FileTransfer | `PortableDesktopFileStore` (Infrastructure) | Chunk 8 |
| `ITimelineCardStateService` | Desktop Timeline | `TimelineCardStateService` | Chunk 9 |
| `ITimelinePostSink` | PluginSdk | `DesktopTimelinePostSink` | Chunk 9 |
| `IPluginActionHandler` | PluginSdk | `DesktopPluginActionHandlerDispatcher` | Chunk 9 |
| `IDateTimeProvider` | Domain Common | `SystemDateTimeProvider` | Chunk 1 |

---

## 6. Developer Tooling & Simulation Interoperability

Testing multi-peer network topologies, relays, and handshakes is handled by the standalone companion project **`Percolator.Simulator`** (see [`Percolator.Simulator/implementation-plan.md`](../Percolator.Simulator/implementation-plan.md)).

`Percolator.Desktop` contains **zero simulator code** or mock interceptors. To interoperate with the simulator during development, `Percolator.Desktop` supports a development configuration flag:
* `Development:AllowLocalhostPeers: true` (or `--allow-localhost` CLI argument).
* When set, loopback `127.0.0.1` targets are permitted as valid peer endpoints without triggering production WAN stealth security rejections.
