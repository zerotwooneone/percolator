# Percolator.Desktop Implementation Plan

---

## 1. Executive Summary & Core Architectural Invariants

`Percolator.Desktop` is the cross-platform desktop UI for the Percolator peer-to-peer ecosystem, targeting **Windows, Linux, and macOS** using **Avalonia UI 11.2**, **FluentAvalonia 2.1**, **CommunityToolkit.Mvvm 8.4**, **R3 1.3.1**, **R3Extensions.Avalonia 1.3.1**, and **ObservableCollections.R3 3.3.4** on **.NET 10**.

### 1.1 Architectural & Security Invariants

1. **Two-Stage Container Architecture & Zero-Repository Pre-Unlock Gate (`DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE`, `DEP-DESKTOP-STARTUP-VAULT-AUTHENTICATION`, `INV-DESKTOP-TWO-STAGE-HANDOFF`)**:
   * **Unified Encrypted SQLite (`percolator.db`)**: All application data resides in a single database encrypted with SQLCipher AES-256-CBC with WAL mode (`DEC-SQLITE-WAL-PRAGMAS`).
   * **Bootstrap Container (Root DI)**: On startup, `App.axaml.cs` builds a lightweight container hosting only core presentation and bootstrap infrastructure (`IWindowManager`, `IWindowHandoffCoordinator`, `ICryptoEngine`, `IDateTimeProvider`, and `IDatabaseUnlockCoordinator`). **Zero SQLite DbContexts, repositories, or background sync workers exist in memory prior to unlock.**
   * **Unified Startup Frame ($480 \times 600\text{ px}$)**:
     * Screen 1 (Vault Setup / Unlock) and Screen 2 (Identity Gate) share a single centered window frame to eliminate screen flashing or awkward window resizing during startup.
     * *First-Time Setup (`DatabaseSetupStatus.Uninitialized`)*: Guides the user to create a master password and optional emergency duress passphrase. An empty master password (`""`) triggers an explicit confirmation dialog before vault initialization.
     * *Subsequent Launches (`DatabaseSetupStatus.Locked`)*: Presents the passphrase field requiring explicit unlock.
     * *Duress Kill Switch & Decoy Transition (`DEC-TWIN-SLOT-DURESS-HEADER`)*: Entering an established duress passphrase cryptographically shreds the real vault key and header in $< 2\text{ ms}$, creates a valid decoy database encrypted with the duress key, and transitions seamlessly into Screen 2 (State 2B: Empty Vault) with zero error popups, alert badges, or forensic crash indicators.
   * **Canary Verification & Post-Unlock Migration (`DEC-CANARY-ERROR26-UNWRAP`)**:
     * Derives a 256-bit candidate key using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$, `DEC-ARGON2ID-PARAMETERS`) and evaluates AEAD slots.
     * Validates passphrase with an isolated canary query `SELECT count(*) FROM sqlite_master;`, explicitly trapping native error code `26` (`SQLITE_NOTADB`).
     * On success, pending EF Core / SQLite migrations (`Database.MigrateAsync()`) execute immediately before any domain repositories are resolved.
   * **Authenticated Host (`IHost`)**:
     * Instantiates a child `IHost` containing `PercolatorDbContext`, connection pools with native `SQLitePCL.raw.sqlite3_key` interceptor (`DEC-PINNED-SQLITE-KEY`), domain repositories, and background workers.
     * `await session.StartAsync()` automatically discovers and launches all registered `IHostedService` instances.
   * **Window Handoff & Relocking (`DEP-DESKTOP-SESSION-LOCK-ZEROIZATION`)**:
     * On identity selection confirmation, `StartupWindow` closes and `MainWindow` ($1200 \times 800\text{ px}$) opens, restoring user window geometry.
     * Session lock or app exit triggers `session.StopAsync()`, flushes connection pools via `SqliteConnection.ClearAllPools()`, zeroizes the key buffer via `CryptographicOperations.ZeroMemory`, disposes the authenticated `IHost`, closes `MainWindow`, and re-opens `StartupWindow` in Screen 1B unlock state.
2. **Total Network Stealth Startup Invariant (`DEP-DESKTOP-PRE-ACTIVATION-STEALTH`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`)**:
   * On launch, the application operates in **total network stealth**—zero sockets bound, zero UDP DHT packets, zero STUN probes, and zero Kestrel/gRPC listeners active.
   * Sockets bind **only after** database unlock and explicit persona confirmation in Screen 2 (`IdentityGateView`).
   * Session lock or deactivating all personas immediately terminates all listening sockets and tears down active network sessions, returning the application to stealth mode.
3. **Inbound Connection Silence Invariant (`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`, `DEC-PULL-BASED-CONSENT-STAGING`, `DEP-DESKTOP-PEER-BLOCK-UI-SILENCE`)**:
   * Inbound unsolicited connections or handshake offers **must never** interrupt the user with OS toasts, audio pings, or popup modals.
   * Inbound requests illuminate a subtle indicator light on the Pending Requests button at the top of the Left Sidebar; user review is entirely pull-based.
4. **Domain Purity & Presentation Model Isolation (`DEP-DESKTOP-SEGREGATION-ROLE`, `INV-DESKTOP-PRESENTATION-MODELS`)**:
   * `Percolator.Domain` is strictly isolated from UI dependencies (zero UI interfaces, zero dispatcher calls, zero snapshotting methods on domain entities).
   * All reactive UI models live exclusively in `Percolator.Desktop.Features.*.Models`.
   * Presentation models implement `.ToSnapshot()` returning immutable C# `record` types for lock-free background reads, caching, and export.
5. **State Ownership Boundary (`INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-THREAD-DISCIPLINE`)**:
   * **Local State (Owned by ViewModels)**: Transient view state (input text, validation errors, accordion expansion, dialog visibility, scroll offsets) is private to the ViewModel.
   * **Shared State (Owned by Stateful UI Services)**: Cross-cutting application state (active identities, peer sessions, message queues, DHT discovery tables, swarm file transfers) is owned exclusively by singleton/scoped **Stateful UI Services** (`IdentityStateService`, `PeerConnectionStateService`, `ChatStateService`, `DiscoveryStateService`, `FileTransferStateService`, `TimelineCardStateService`).
   * Stateful UI Services are **100% UI-agnostic**—they operate on background worker threads, guard mutations with concurrency gates (`SemaphoreSlim`), and expose canonical `ObservableList<T>` / `ObservableDictionary<TKey, TValue>` collections.
6. **Volatile Draft & LRU ViewModel Caching (`DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION`, `DEC-LRU-SESSION-SCOPES`, `DEC-DEBOUNCED-DROP-RELOAD`)**:
   * The desktop application maintains an in-memory `ChatDraftStateService` and an LRU cache of recently active `ChatViewModel` instances (capacity 3–5).
   * Switching between chats or personas restores unsent message drafts, scroll positions, and attachment states from volatile memory without database re-fetching or schema pollution.
7. **Strict R3 Reactive Programming Rules (`R3-001` through `R3-014`)**:
   * **Avalonia Dispatcher Integration (`R3-001`)**: Configured in `Program.cs` via `.UseR3()`, wiring `AvaloniaDispatcherTimeProvider` and `AvaloniaDispatcherFrameProvider` into `ObservableSystem`.
   * **Filter Early, Marshal Late (`R3-002`)**: Perform filtering, deduplication, debounce, and sorting on background worker threads; only marshal to the UI thread at the terminal step via `.ObserveOn(AvaloniaSynchronizationContext.Current)`.
   * **Throttle the Firehose (`R3-007`)**: High-frequency streaming events (network packet rates, DHT search hops, file piece bitfields) must be sampled (`.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)`) to protect the UI thread from 60fps+ inundation.

---

## 2. Technology Stack & Component Architecture

| Component | Library / Framework | Architectural Purpose |
| :--- | :--- | :--- |
| **UI Framework** | Avalonia UI 11.2 | Cross-platform XAML rendering engine for Windows, Linux, and macOS |
| **Control System** | FluentAvalonia 2.1 | Fluent v2 WinUI styling, `FluentAvaloniaTheme`, dynamic palettes, `ContentDialog` |
| **Session Lifecycle** | `IWindowHandoffCoordinator` | Orchestrates two-stage DI transition, startup-to-shell window swap, and secure relock |
| **Privacy & Security** | `INetworkStealthGate` | Gated listener activation ensuring zero open ports until persona confirmation |
| **Port Leasing** | `IPortReservationService` | Dynamic port availability probing, conflict detection, and auto-leasing |
| **Draft Caching** | `IChatDraftStateService` | Volatile in-memory draft, scroll offset, and attachment caching |
| **Iconography** | Segoe Fluent Icons + FiraCode Nerd Font | Fluent icons for UI controls; embedded Nerd Font for P2P mesh & route telemetry |
| **Custom UI Primitives** | `PeerAvatar`, `StatusChip`, `Badge`, `PieceMapControl` | Reusable controls for presence, route pills, notification badge, and chunk canvas |
| **Session Scoping** | `SessionScopeFactory` | LRU-cached child DI scopes for active conversation ViewModels (`DEC-LRU-SESSION-SCOPES`) |
| **Data Synchronization** | `ReloadCoordinator` | Debounced reactive query batching with `AwaitOperation.Drop` (`DEC-DEBOUNCED-DROP-RELOAD`) |
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
│   ├── Storage/                          # IDesktopSettingsRepository.cs, VaultDesktopSettingsRepository.cs
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
│   │   ├── Services/                     # IChatStateService.cs, ChatStateService.cs, IChatDraftStateService.cs, ChatDraftStateService.cs, ChatReloadCoordinator.cs, SessionScopeFactory.cs
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
        C1["[DESK-01] Shell Token Foundation & Responsive 3-Pane Frame"]
        C2["[DESK-02] Pre-Unlock Vault Gate (StartupWindow Screen 1A & 1B + Duress Decoy)"]
        C3["[DESK-03] Multi-Persona Gate & Stealth Port Binding (Screen 2 + DesktopIdentitySettings)"]
        C4["[DESK-04] Two-Stage DI & Window Handoff Coordinator (Lifecycle & Relocking)"]
    end

    subgraph Phase 2: Transports, Messaging & Inspection
        C5["[DESK-05] Two-Tier Sidebar, Add Connection & Pending Requests (Signal Inspector)"]
        C6["[DESK-06] Real-Time Chat, Volatile Drafts & Disappearing Messages (Apps.Chat)"]
    end

    subgraph Phase 3: P2P Swarm & Platform Services
        C7["[DESK-07] Sovereign Discovery & Swarm Telemetry (Apps.Discovery)"]
        C8["[DESK-08] Swarm File Transfer Dashboard & In-Chat Cards (Apps.FileTransfer)"]
        C9["[DESK-09] Timeline Cards & PluginSdk Integration (PluginSdk)"]
        C10["[DESK-10] Desktop System Integration, Native Notifications & Quick-Switcher (Ctrl+K)"]
        C11["[DESK-11] Headless UI & Concurrency Verification Test Suite"]
    end

    C1 --> C2 --> C3 --> C4 --> C5 --> C6 --> C7 --> C8 --> C9 --> C10 --> C11
```

---

### [DESK-01] Shell Token Foundation & Responsive 3-Pane Frame
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE` (Desktop bounded strictly to UI presentation, layout, and windowing; zero cryptographic or transport state mutations)
* **Detailed Design**: `INV-DESKTOP-TWO-STAGE-HANDOFF`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[STYLE-001]` to `[STYLE-008]`, `[VIEW-001]`, `[VIEW-002]`, `[VIEW-003]`, `[VIEW-007]`, `[VIEW-008]`, `[VIEW-009]`, `[CB-001]`, `[CB-002]`, `[CB-003]`, `[CB-005]`, `[CB-007]`, `[CB-009]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[R3-001]`, `[R3-002]`, `[R3-003]`
* **Target Deliverables**:
  * Create: `Common/Styles/Spacing.axaml` (geometry and spacing tokens: `CardCornerRadius` = 6, `OverlayCornerRadius` = 8, `ControlCornerRadius` = 4, margin tokens)
  * Create: `Common/Styles/CustomStyles.axaml` (`Button.danger` with `SystemFillColorCriticalBrush`, `Border.persona-card` with hover `BrushTransition`)
  * Modify: `App.axaml` & `App.axaml.cs` (configure `<sty:FluentAvaloniaTheme PreferUserAccentColor="True" PreferSystemTheme="True" />`, merge resource dictionaries, embed FiraCode Nerd Font, zero theme mixing)
  * Create: `Features/Shell/Views/MainWindow.axaml` & `Features/Shell/Views/MainWindow.axaml.cs`
  * Create: `Features/Shell/ViewModels/MainWindowViewModel.cs`
* **Acceptance Criteria**:
  * Boots into responsive, fully themed 3-pane Discord/Teams-inspired shell frame ($1200 \times 800\text{ px}$, min $800 \times 600\text{ px}$).
  * Layout panes: Left Sidebar ($72\text{ px}$ fixed), Right Sidebar ($240\text{ px}$ collapsible), Content Area (header, message list placeholder, chat composer placeholder), Right-Anchored Inspector Drawer ($300\text{ px}$ slide-over).
  * Responsive layout breakpoints: Window width $< 1000\text{ px}$ transitions Inspector drawer to an overlay drawer with backdrop dismiss scrim; window width $< 850\text{ px}$ collapses Right Sidebar into a slide-out drawer with hamburger toggle.
  * Theme switching between Light, Dark, and High Contrast occurs dynamically using semantic system brush tokens (`[STYLE-005]`).
  * FiraCode Nerd Font definitions linked and accessible via `NerdFontFamily`.
* **Verification Gate**:
  * `dotnet build Percolator.Desktop/Percolator.Desktop.csproj` succeeds with 0 errors and 0 warnings.
  * Resizing window across $1200\text{ px} \rightarrow < 1000\text{ px} \rightarrow < 850\text{ px}$ executes smooth responsive layout transitions without clipping or visual artifacts.

---

### [DESK-02] Pre-Unlock Vault Gate & Duress Decoy
* **QMS Traceability**: `DEP-DESKTOP-STARTUP-VAULT-AUTHENTICATION` (allocates `SYS-STARTUP-VAULT-AUTHENTICATION`), `DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE`
* **Detailed Design**: `INV-DESKTOP-TWO-STAGE-HANDOFF`, `DEC-CANARY-ERROR26-UNWRAP`, `DEC-PINNED-SQLITE-KEY`, `DEC-TWIN-SLOT-DURESS-HEADER`, `DEC-ARGON2ID-PARAMETERS`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-004]`, `[VIEW-009]`, `[CB-001]`, `[CB-002]`, `[CB-003]`, `[CB-004]`, `[CB-008]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-010]`, `[VM-014]`, `[VM-016]`, `[R3-001]`, `[R3-003]`, `[R3-004]`
* **Target Deliverables**:
  * Create: `Features/Startup/Views/StartupWindow.axaml` & `Features/Startup/Views/StartupWindow.axaml.cs`
  * Create: `Features/Startup/ViewModels/StartupHostViewModel.cs`
  * Create: `Features/Startup/Views/VaultSetupView.axaml` & `Features/Startup/Views/VaultSetupView.axaml.cs`
  * Create: `Features/Startup/ViewModels/VaultSetupViewModel.cs`
  * Create: `Features/Startup/Views/VaultUnlockView.axaml` & `Features/Startup/Views/VaultUnlockView.axaml.cs`
  * Create: `Features/Startup/ViewModels/VaultUnlockViewModel.cs`
  * Create: `Features/Startup/Models/StartupStateModel.cs`
* **Acceptance Criteria**:
  * Unified $480 \times 600\text{ px}$ centered, non-resizable frame hosting Screen 1 and Screen 2 views via dynamic transition.
  * State 1A (`VaultSetupView`):
    * Header: Title *"Welcome to Percolator"* (`Theme="{StaticResource TitleTextBlockStyle}"`), Subtitle *"Choose a master password to protect your messages, keys, and contacts on this device."* (`Theme="{StaticResource CaptionTextBlockStyle}"`, `Foreground="{DynamicResource TextFillColorSecondaryBrush}"`).
    * Master Password + Confirm Password fields (`PasswordChar="•"` with built-in reveal toggle).
    * Expandable `[+ Add Emergency Duress Passphrase]` progressive disclosure link.
    * Validation: Duress cannot match master password; whitespace-only disallowed.
    * Blank Password Dialog: If password is empty, presents a confirmation modal via the application's injected dialog service abstraction: *"Skip password protection? Your vault will still be encrypted with a random key, but that key will be accessible without a password."* with actions `[ Cancel ]` and `[ Continue Without Password ]`.
    * Calls `IDatabaseUnlockCoordinator.InitializeAsync(masterPassword, duressPassword, ct)`.
  * State 1B (`VaultUnlockView`):
    * Header: *"Percolator"* / *"Unlock Vault"* (`Theme="{StaticResource TitleTextBlockStyle}"`).
    * Auto-focused master password field with show/hide toggle.
    * Inline loading spinner during Argon2id key derivation.
    * Error feedback: subtle shake animation and critical inline text (*"Incorrect password. Please try again."*) on native error `26` (`SQLITE_NOTADB`).
    * Auto-launch preference bypass *(deferred to DESK-03 — requires `IDesktopSettingsRepository` for the auto-start flag; DESK-02 always transitions to Screen 2 on successful unlock)*: If auto-start is enabled for a persona, unlock immediately launches `MainWindow`. Holding `Shift` (or `Shift+Enter`) overrides bypass and forces Screen 2 to appear.
    * Emergency Duress Activation: Entering duress passphrase cryptographically shreds real header in $< 2\text{ ms}$, creates a valid decoy SQLite DB encrypted with duress key, and transitions smoothly into Screen 2 (State 2B: Empty Vault) with zero error popups or forensic crash signatures.
  * Corrupted Vault Modal: If database header is unparseable or damaged, displays *"Vault Data Damaged"* dialog (*"The database file could not be opened. It may have been damaged or altered."*) with actions `[ Open Data Folder ]` and `[ Exit ]`.
  * Security Engine Error Modal: If native cryptographic smoke test fails (`SignalCrypto.TestConnection() != 42` due to missing VC++ runtimes), displays *"Security Engine Error"* dialog (*"The native cryptographic library (signal_shim) could not be loaded on your system."*) with actions `[ View Diagnostic Log ]` and `[ Exit ]`.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Vault"`
  * Fresh install presents `VaultSetupView` with proper typography hierarchy. Initializing with empty password prompts confirmation dialog. Subsequent launch presents `VaultUnlockView`. Incorrect passphrase shows inline error without crashing. Duress passphrase wipes vault and smoothly displays empty vault state.

---

### [DESK-03] Multi-Persona Gate & Stealth Port Binding
* **QMS Traceability**: `DEP-DESKTOP-PRE-ACTIVATION-STEALTH` (allocates `SYS-PRE-ACTIVATION-STEALTH`), `DEP-DESKTOP-PERSONA-ISOLATION` (allocates `SYS-PERSONA-KEY-SEGREGATION`)
* **Detailed Design**: `INV-DESKTOP-STEALTH-ACTIVATION-GATE`, `INV-DESKTOP-PRESENTATION-MODELS`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-004]`, `[CB-001]`, `[CB-004]`, `[CB-008]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[VM-013]`, `[VM-014]`, `[R3-004]`, `[R3-005]`
* **Target Deliverables**:
  * Create: `Common/Storage/IDesktopSettingsRepository.cs` + vault-backed `VaultDesktopSettingsRepository` over `IVaultSettingsStore` (Stage-2; rows are `IdentityLaunchSettings` records — `PreferredPort`, `LastBoundPort`, `AutoStart`, `SidebarOrder` — keyed by `PublicIdentityId` inside the encrypted vault)
  * Create: `Common/Services/PortReservationService.cs` (`IPortReservationService` probing range `5100`–`5299`)
  * Extend: `Features/Startup/Views/IdentityGateView.axaml` & `Features/Startup/Views/IdentityGateView.axaml.cs` (shell created by DESK-02 as the State 2B empty-vault duress landing; add persona cards, port pills, context menus)
  * Extend: `Features/Startup/ViewModels/IdentityGateViewModel.cs` (skeleton created by DESK-02; add persona collection, auto-start flag, launch command)
  * Create: `Features/Startup/Models/SelectablePersonaModel.cs`
  * Create: `Features/Startup/Views/CreatePersonaDialog.axaml` & `Features/Startup/ViewModels/CreatePersonaDialogViewModel.cs`
  * Create: `Features/Startup/Views/DeletePersonaDialog.axaml` & `Features/Startup/ViewModels/DeletePersonaDialogViewModel.cs`
* **Acceptance Criteria**:
  * State 2A (Normal Return):
    * Multi-select persona cards (`Border.persona-card`).
    * Port status pill: Green dot (`Port 5100 Ready`), Orange dot (`Port 5100 in use → Auto-selected 5101`), Red alert pill (`All default ports busy` in range `5100`–`5299`).
    * Inline port editor popover (`[ ✎ ]`) to customize port.
    * Card Context Menu (right-click or `···` kebab menu): `Edit Profile` (display name/avatar) and `Delete Persona`.
    * Port Race Condition Recovery: If a port is claimed by another process between selection and launch, displays inline banner (*"Port 5101 was claimed by another application. Re-probing next available port..."*) and automatically re-leases next free port.
    * Single-persona auto-start checkbox: `[ ] Always auto-start with this persona on unlock`.
    * Primary button `[ Launch Selected Personas → ]` (disabled if zero checked or conflict unresolved).
  * State 2B (Empty Vault):
    * Centered card: *"No personas yet"*, with friendly tip callout: *"Tip: Many people create separate personas for Work vs. Personal, or Family vs. Friends."*
    * `[ Create Your First Persona ]` action button.
  * `CreatePersonaDialog`: Display name (max 32 chars, whitespace-trimmed), avatar picker with identicon generator, advanced port settings.
  * `DeletePersonaDialog`: Selectable persona name box so power users can copy with keyboard/mouse. Confirmation prompt requiring user to type exact persona name to enable `[ Shred & Delete Persona ]` (`Button.danger`).
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~IdentityGate|FullyQualifiedName~PortReservation"`
  * Assert zero network sockets are opened prior to `LaunchPersonasCommand.Execute()`. Port conflict correctly auto-selects next free port. Deleting persona shreds keys and removes profile only when typed name matches exactly.

---

### [DESK-04] Two-Stage DI & Window Handoff Coordinator
* **QMS Traceability**: `DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE`, `DEP-DESKTOP-SESSION-LOCK-ZEROIZATION` (allocates `SYS-SESSION-LOCK-ZEROIZATION`), `DEP-INFRA-VAULT-SCHEMA-MIGRATION` (allocates `SYS-VAULT-UPGRADE-DATA-PRESERVATION`), `DEP-INFRA-DURESS-DECOY-VAULT-LIFECYCLE` (allocates `SYS-DECOY-VAULT-DENIABILITY`), `DEP-INFRA-LOCAL-STORAGE-CONTAINMENT` (allocates `SYS-LOCAL-STORAGE-CONTAINMENT`)
* **Detailed Design**: `INV-DESKTOP-TWO-STAGE-HANDOFF`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`
* **Governing Specs**: `[VIEW-009]`, `[CB-001]`, `[CB-002]`, `[CB-009]`, `[VM-003]`, `[VM-004]`, `[VM-007]`, `[VM-010]`, `[R3-001]`, `[R3-014]`
* **Target Deliverables**:
  * Modify: `App.axaml.cs` (wire Stage 1 `BootstrapContainer` and Stage 2 `AuthenticatedSession` builder)
  * Extend: `Common/Windowing/IWindowHandoffCoordinator.cs` & `Common/Windowing/WindowHandoffCoordinator.cs` (interface and window-swap/relock shells created by DESK-02; add port leasing, stealth-gate deactivation, geometry restore)
  * Create: `Common/Services/INetworkStealthGate.cs` & `Common/Services/NetworkStealthGate.cs`
  * **Explicit VM→Window mapping (replace reflection convention)**: `WindowManager.ResolveWindowByConvention` currently resolves dialog windows via `FooViewModel` → `FooWindow`/`FooView` name mangling + `Activator.CreateInstance` — fragile, untrimmed-safe, and silently broken until DESK-03 exercised it. Replace with a registered `IReadOnlyDictionary<Type, Type>` (or equivalent `IDialogRegistry` service) populated at composition root: each dialog ViewModel type maps explicitly to its `Window` type, resolved with compile-time checking and zero reflection — prerequisite for Native AOT and removes the namespace-convention foot-gun entirely.
  * **Schema versioning (EF Core migrations)**: The DESK-03 wiring fix made `SqlCipherSchemaMigrator`/`SqliteDatabaseMigrator` run at unlock, but with zero EF migration classes it falls back to an idempotent `CREATE ... IF NOT EXISTS` script — which handles *missing* tables yet cannot evolve an existing schema (column adds/drops, index changes). Promote migrations to the primary mechanism:
    * Create: `Percolator.Infrastructure/Migrations/` baseline via `dotnet ef migrations add InitialSchema` plus a `IDesignTimeDbContextFactory<PercolatorDbContext>` (throwaway unencrypted context for tooling) and the `Microsoft.EntityFrameworkCore.Design` package.
    * Preserve: the idempotent-create fallback for databases that predate the migrations baseline (no `__EFMigrationsHistory` row) — a one-time bootstrap adoption path so existing vaults are not stranded.
    * Policy: subsequent model changes must ship a generated migration; the manual `IF NOT EXISTS` path is bootstrap-only, not a schema-evolution story.
* **Acceptance Criteria**:
  * Stage 1 `BootstrapContainer`: Minimal services (`IWindowManager`, `IWindowHandoffCoordinator`, `ICryptoEngine`, `IDateTimeProvider`, `IDatabaseUnlockCoordinator`, `StartupHostViewModel`). Zero DbContexts, repositories, or network listeners.
  * Stage 2 `AuthenticatedSession`: Instantiated only after successful canary verification, containing `PercolatorDbContext`, connection pools with native `sqlite3_key` interceptor, domain repositories, and hosted services.
  * `TransitionToGateAsync(session)`: Transitions `StartupWindow` from Screen 1 to Screen 2.
  * `TransitionToMainShellAsync(activePersonas)`: Acquires port leases via `IPortReservationService`, binds Kestrel listeners strictly for chosen personas, closes `StartupWindow`, opens `MainWindow`, restoring geometry.
  * `LockSessionAsync()`: Deactivates network listeners via `INetworkStealthGate.DeactivateAsync()`, stops `IHost`, flushes SQLite connection pools (`SqliteConnection.ClearAllPools()`), zeroizes master key span (`CryptographicOperations.ZeroMemory`), disposes `IHost`, closes `MainWindow`, and instantiates fresh `StartupWindow` in State 1B unlock state.
  * **Versioned schema evolution**: `Database.MigrateAsync()` applies pending EF migrations at unlock inside `SqlCipherSchemaMigrator` before repositories resolve; `__EFMigrationsHistory` records applied versions. A model change that adds/renames/drops a column produces a migration which alters the existing vault table in place without data loss; legacy vaults without `__EFMigrationsHistory` adopt the baseline via the idempotent-create path, then migrate forward.
  * **Hazard re-rating**: once the migration regression evidence above is green, update `RiskRegister.md` — flip `HAZ-VAULT-SCHEMA-EVOLUTION-INCOMPATIBILITY` residual status `Identified` → `Controlled` citing the migration control (human approval required per `risk-register-spec` §3.3; the deliverable includes producing the verification evidence for approval, not self-approving).
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~WindowHandoff|FullyQualifiedName~Lifecycle"`
  * `dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Migrat"`
  * Verifies complete socket teardown, connection pool flushing, key zeroization, and window swap upon session lock.
  * Migration regression: a seeded DB containing only `__percolator_vault` gains the full model schema; a seeded older-schema DB gains the new column/index via `ALTER`-style migration while retaining existing rows.
  * QMS bookkeeping: `HAZ-VAULT-SCHEMA-EVOLUTION-INCOMPATIBILITY` re-rated to `Controlled` in `RiskRegister.md` after human approval; `SYS-VAULT-UPGRADE-DATA-PRESERVATION` marked verified.

---

### [DESK-05] Two-Tier Sidebar, Add Connection & Pending Requests
* **QMS Traceability**: `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (allocates `SYS-CONTACT-CONSENT-GATE`, `SYS-DIRECT-CONTACT-REQUEST-OUTBOX`), `DEP-DESKTOP-PEER-BLOCK-UI-SILENCE` (allocates `SYS-PEER-BLOCK-SILENT-DROP`), `DEP-DESKTOP-OUT-OF-BAND-VERIFICATION` (allocates `SYS-OUT-OF-BAND-SECURITY-CODE`), `DEP-DESKTOP-KEY-CHANGE-WARNING` (allocates `SYS-KEY-CHANGE-TRUST-HALT`)
* **Detailed Design**: `DEC-PULL-BASED-CONSENT-STAGING`, `INV-DESKTOP-PRESENTATION-MODELS`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-002]`, `[VIEW-003]`, `[VIEW-004]`, `[CB-001]`, `[CB-004]`, `[CB-008]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[R3-002]`, `[R3-004]`, `[R3-005]`
* **Target Deliverables**:
  * Create: `Features/Transports/Views/TransportsSidebarView.axaml` & `Features/Transports/Views/TransportsSidebarView.axaml.cs`
  * Create: `Features/Transports/ViewModels/TransportsSidebarViewModel.cs`
  * Create: `Features/Transports/Views/PendingRequestsPanelView.axaml` & `Features/Transports/ViewModels/PendingRequestsPanelViewModel.cs`
  * Create: `Features/Transports/Views/AddConnectionDialog.axaml` & `Features/Transports/ViewModels/AddConnectionDialogViewModel.cs`
  * Create: `Features/Chats/Views/ChatsSidebarView.axaml` & `Features/Chats/Views/ChatsSidebarView.axaml.cs`
  * Create: `Features/Chats/ViewModels/ChatsSidebarViewModel.cs`
  * Create: `Features/Inspector/Views/ProfileInspectorView.axaml` & `Features/Inspector/Views/ProfileInspectorView.axaml.cs`
  * Create: `Features/Inspector/ViewModels/ProfileInspectorViewModel.cs`
  * Create: `Features/Transports/Services/IPeerConnectionStateService.cs` & `Features/Transports/Services/PeerConnectionStateService.cs`
  * Create: `Features/Transports/Models/TransportChannelModel.cs`, `PendingHandshakeModel.cs`
  * Create: `Features/Inspector/Models/PeerProfileInspectorModel.cs`, `RelayInspectorModel.cs`
* **Acceptance Criteria**:
  * Left Sidebar ($72\text{ px}$ fixed):
    * Top Pending Requests Button: Unobtrusive icon button with subtle notification light. **Inbound Connection Silence Invariant**: Zero OS toasts, popups, or audio pings on unsolicited inbound handshakes.
    * Middle Transport Channels List: Direct peer profile picture (or identicon) and relay badges. Zero technical clutter (no IP addresses, ports, or protocol strings in the list). Mouse hover shows remote display name in clean tooltip. Reorderable via drag-and-drop (persisted via `IDesktopSettingsRepository` as `IdentityLaunchSettings` inside the encrypted vault). Right-click context menus: *"View Profile"*, *"Mute"*, *"Remove Connection"* / *"Relay Details"*, *"Reconnect"*.
    * Bottom Add Connection (`+`) Button: Prominent `+` button opening `AddConnectionDialog.axaml`: Tab 1 (*Connect Outbound* - peer endpoint or relay URI, validates connectivity and initiates handshake), Tab 2 (*Share My Invitation* - generates and displays active identity's out-of-band invitation URL and QR code).
    * Identity Area: Profile picture, local display name truncated with ellipsis (`...`). Unread indicator acts as anchor for **Active Identity Switcher Flyout** to switch shell context to another active persona.
  * Pending Requests Panel: Displays requester display name and fingerprint preview with `Accept`, `Reject`, and `Burn` (permanent blacklist) actions.
  * Right Sidebar ($240\text{ px}$ collapsible): Contextual chats for selected transport (single 1-on-1 chat for direct peer; individual and group channels for relays). Avatar, display name, unread badge count, last message snippet. Zero route tags like `(Relayed 1-on-1)`. Right-click context menu: *"View Details"*, *"Mute Notifications"*, *"Clear History"*, *"Leave Group"*.
  * Profile & Connection Inspector ($300\text{ px}$ slide-over):
    * Direct Peer Inspector (Signal-inspired): Profile summary (avatar, remote name, copyable truncated Identity ID), Disappearing Messages selector presets (*Off, 30s, 5m, 1h, 1d, 1w, 4w*), Notification mute toggle presets (*1h, 8h, 1d, 7d, Always*), Contextual Safety Numbers panel (hidden by default; appears if handshake is incomplete or Identity Key Change Alert is triggered; displays 60-digit fingerprint with *"Mark as Verified"* toggle), connection route telemetry (Direct LAN, STUN, Relayed) and roundtrip ping, contact actions (*"Clear Chat History"*, *"Block & Burn Contact"*).
    * Relay / Group Inspector Variant: Relay URI, connection status, latency, hosted mailboxes. Group name, avatar, disappearing timer, member roster with Admin/Member badges.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Transports|FullyQualifiedName~Inspector"`
  * Inbound unsolicited handshake illuminates pending requests indicator without firing OS toasts. Accepting establishes chat; burning blacklists peer. Safety numbers panel remains hidden until key change is simulated.

---

### [DESK-06] Real-Time Chat, Volatile Drafts & Disappearing Messages
* **QMS Traceability**: `DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION` (allocates `SYS-VOLATILE-DRAFT-ISOLATION`), `DEP-DESKTOP-MEDIA-METADATA-SANITIZATION-INGRESS` (allocates `SYS-MEDIA-METADATA-SANITIZATION-INGRESS`)
* **Detailed Design**: `DEC-LRU-SESSION-SCOPES`, `DEC-DEBOUNCED-DROP-RELOAD`, `DEC-REACTIVE-EVENT-BUS-BRIDGE`, `INV-DESKTOP-PRESENTATION-MODELS`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-005]`, `[CB-001]`, `[CB-003]`, `[CB-004]`, `[CB-008]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-007]`, `[VM-010]`, `[VM-012]`, `[VM-014]`, `[R3-002]`, `[R3-003]`, `[R3-004]`, `[R3-005]`, `[R3-006]`, `[R3-007]`, `[R3-009]`
* **Target Deliverables**:
  * Create: `Features/Chats/Models/ChatMessageModel.cs` (properties: `MessageId`, `SessionId`, `SenderId`, `SenderDisplayName`, `ContentText`, `TimestampUtc`, `DeliveryStatus` [`Queued`, `Sending`, `Sent`, `Delivered`, `Read`, `Failed`], `IsOutgoing`, `IsEdited`, `IsDeleted`, `ExpiresAtUtc`, `Reactions` [`ObservableList<ReactionModel>`], `Attachments` [`ObservableList<MediaAttachmentModel>`])
  * Create: `Features/Chats/Models/ChatMessageSnapshot.cs` (immutable `record` for lock-free background reads, search indexing, and export)
  * Create: `Features/Chats/Models/ConversationSummaryModel.cs`
  * Create: `Features/Chats/Services/IChatStateService.cs` & `Features/Chats/Services/ChatStateService.cs`
  * Create: `Features/Chats/Services/IChatDraftStateService.cs` & `Features/Chats/Services/ChatDraftStateService.cs`
  * Create: `Features/Chats/Services/ChatReloadCoordinator.cs`
  * Create: `Features/Chats/Services/SessionScopeFactory.cs`
  * Create: `Features/Chats/Controls/ChatComposer.axaml` & `Features/Chats/Controls/ChatComposer.axaml.cs`
  * Create: `Features/Chats/Controls/ReactionPickerFlyout.axaml`
  * Create: `Features/Chats/Views/ChatView.axaml` & `Features/Chats/Views/ChatView.axaml.cs`
  * Create: `Features/Chats/ViewModels/ChatViewModel.cs`, `ChatMessageViewModel.cs`
  * Create: `Features/Chats/Views/GroupCreationDialog.axaml` & `Features/Chats/ViewModels/GroupCreationDialogViewModel.cs`
* **Acceptance Criteria**:
  * `ChatDraftStateService`: In-memory volatile store for unsent drafts, pending attachments, and conversation scroll offsets. Restores drafts instantly when switching between chats or identities without database re-fetching or schema pollution.
  * `SessionScopeFactory`: Manages child `IServiceScope` instances for active conversation tabs (LRU capacity = 5); opening 6th chat disposes least-recently used scope.
  * `ChatReloadCoordinator`: Debounces message arrivals and receipt updates by `50ms` with `AwaitOperation.Drop` to coalesce SQLite roundtrips.
  * `ChatComposer`: Auto-expanding `TextBox` (1 to 6 lines max). Keyboard handling: `Enter` sends message; `Shift+Enter` or `Ctrl+Enter` inserts newline. Attachment button triggering file picker flyout. Emoji reaction picker flyout.
  * `ChatView`: Virtualized message list with smooth scroll-to-bottom on new messages. Asymmetric message bubbles: Right-aligned accent bubble for outgoing messages; left-aligned surface bubble for incoming messages. Message status icons: Single tick (Sent), Double tick (Delivered), Colored double tick (Read), Exclamation (Failed). Inline message editor and tombstone (*"This message was deleted"*) placeholder. Reaction summary pills with click-to-toggle reaction. Disappearing Messages Reaper monitoring `ExpiresAtUtc` and purging expired messages.
  * `GroupCreationDialog`: Group creation UI requiring all members to be on a shared relay. Orchestrates pairwise relay invitations and awaits peer join confirmations before dispatching Groups V2 ZK invitations.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Chat|FullyQualifiedName~Scope"`
  * Typing an unsent message in Chat A, switching to Chat B, and returning restores draft text and scroll position. Opening 6 consecutive chats evicts the 1st chat's `IServiceScope`. 50ms debounced reload drops redundant query invocations. Disappearing message timer expiration triggers tombstone update.

---

### [DESK-07] Sovereign Peer Discovery & Swarm Telemetry
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE` (Desktop UI presentation layer for discovery telemetry)
* **Detailed Design**: `INV-DESKTOP-THREAD-DISCIPLINE`, `INV-DESKTOP-PRESENTATION-MODELS`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-003]`, `[CB-001]`, `[CB-003]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[R3-002]`, `[R3-004]`, `[R3-005]`, `[R3-007]`, `[R3-009]`, `[R3-010]`
* **Target Deliverables**:
  * Create: `Features/Discovery/Models/DiscoveredPeerModel.cs` (`PeerId`, `DisplayName`, `Endpoint`, `NatTraversalStatus` [`DirectPublic`, `StunTraversed`, `RelayedOnly`], `RoundtripLatencyMs`, `DiscoveredViaRelayId`, `DiscoveredAtUtc`)
  * Create: `Features/Discovery/Models/BlindedRendezvousModel.cs` (`CurrentEpochNumber`, `BlindedLocatorHex`, `NextEpochTransitionUtc`, `MatchedContactsCount`)
  * Create: `Features/Discovery/Models/SwarmTelemetryModel.cs` (`ActivePeersCount`, `KnownRelaysCount`, `AverageLatencyMs`, `BandwidthInBps`, `BandwidthOutBps`)
  * Create: `Features/Discovery/Services/IDiscoveryStateService.cs` & `Features/Discovery/Services/DiscoveryStateService.cs`
  * Create: `Features/Discovery/Views/DiscoveryView.axaml` & `Features/Discovery/Views/DiscoveryView.axaml.cs`
  * Create: `Features/Discovery/ViewModels/DiscoveryViewModel.cs`, `DiscoveredPeerItemViewModel.cs`
* **Acceptance Criteria**:
  * `DiscoveryStateService`: Bridges `INearbyPeerDiscoveryWorkflow`, `IPrivateRendezvousService`, and `IRelayPeerDirectory`. Throttles candidate event stream using `.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)` to protect the UI thread from high-frequency candidate packet bursts.
  * `DiscoveryView`: Candidate peer cards with public key preview, avatar hash, NAT status badge, and latency meter. One-click *"Connect / Handshake"* action triggering on-demand prekey retrieval and X3DH handshake. Blinded Rendezvous Panel: 24-hr UTC epoch countdown timer and matched sovereign contacts count. Sovereign Relay Selection dropdown allowing users to select which relay directory to query.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Discovery"`
  * High-frequency discovery stream (100 candidates/sec) is sampled down to 60fps without freezing UI thread. Clicking "Connect" initiates handshake. Latency meter updates reactively via `BindableReactiveProperty`.

---

### [DESK-08] High-Performance Swarm File Transfer Dashboard & In-Chat Cards
* **QMS Traceability**: `DEP-DESKTOP-MEDIA-METADATA-SANITIZATION-INGRESS` (allocates `SYS-MEDIA-METADATA-SANITIZATION-INGRESS`)
* **Detailed Design**: `DEC-DIRECT-CANVAS-BITFIELD`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[STYLE-003]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-006]`, `[CB-001]`, `[CB-006]`, `[CB-008]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[R3-002]`, `[R3-004]`, `[R3-005]`, `[R3-007]`, `[R3-012]`
* **Target Deliverables**:
  * Create: `Features/FileTransfer/Models/FileTransferTaskModel.cs` (`TransferId`, `FileName`, `TotalSizeBytes`, `TransferredSizeBytes`, `TransferProgress` [$0.0 \dots 1.0$], `TransferSpeedBps`, `EtaTimeSpan`, `SwarmPeersCount`, `Direction` [`Download`, `Upload`], `Status` [`Queued`, `Transferring`, `Verifying`, `Paused`, `Completed`, `Failed`, `Canceled`])
  * Create: `Features/FileTransfer/Models/TransferBitfieldSnapshot.cs` (immutable snapshot of piece bitfield array)
  * Create: `Features/FileTransfer/Services/IFileTransferStateService.cs` & `Features/FileTransfer/Services/FileTransferStateService.cs`
  * Create: `Features/FileTransfer/Controls/PieceMapControl.axaml` & `Features/FileTransfer/Controls/PieceMapControl.axaml.cs`
  * Create: `Features/FileTransfer/Views/FileTransferView.axaml` & `Features/FileTransfer/Views/FileTransferView.axaml.cs`
  * Create: `Features/FileTransfer/ViewModels/FileTransferViewModel.cs`, `TransferTaskItemViewModel.cs`
* **Acceptance Criteria**:
  * `FileTransferStateService`: Bridges `IFileTransferQueryService`, `ITransferSessionRepository`, and `DiskChunkAssembler`. Exposes `ObservableList<FileTransferTaskModel> ActiveTransfers` and `ObservableList<FileTransferTaskModel> CompletedTransfers`. Rate-samples progress updates via `.Sample(TimeSpan.FromMilliseconds(50), _timeProvider)`.
  * `PieceMapControl`: Custom Avalonia control rendering a graphical bitfield piece grid directly on an Avalonia `DrawingContext`. Visual states: Empty cell (missing), Accent fill (downloaded & verified), Pulse amber (in-flight request). Handles 10,000+ pieces efficiently via direct rectangle drawing operations without creating individual XAML visual elements. Reusable in both full dashboard and in-chat transfer cards.
  * `FileTransferView`: Active transfers list with file icon, transfer speed meter (KB/s, MB/s), ETA timer, and swarm peer count badge. Actions: Pause, Resume, Cancel, Open Containing Folder. Drag-and-drop target supporting Avalonia `DragDrop.DropEvent`. File picker button integrated with Avalonia `TopLevel.StorageProvider`.
  * Integration with `IDesktopFileStorage` (`PortableDesktopFileStore`): Immediate zero-byte file allocation upon transfer acceptance, `.percolator-part` staging, and safe atomic rename on verification completion. Path sanitization enforcing zero Win32 reserved device names (`CON`, `PRN`, `AUX`, `NUL`), zero NTFS alternate data streams (`:`), and Unicode Form C normalization.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~FileTransfer"`
  * Dropping a 50 MB file creates transfer manifest and stages upload. Inbound transfer renders `PieceMapControl` smoothly as chunks arrive without element inflation. Completed transfers rename `.percolator-part` staging files to target file names atomically.

---

### [DESK-09] Timeline Cards & PluginSdk Integration
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE` (Desktop UI presentation layer for plugin sinks)
* **Detailed Design**: `INV-DESKTOP-PRESENTATION-MODELS`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-003]`, `[CB-001]`, `[CB-003]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[R3-002]`, `[R3-004]`, `[R3-005]`, `[R3-013]`
* **Target Deliverables**:
  * Create: `Features/Timeline/Models/TimelineCardModel.cs` (`CardId`, `SourceAppId`, `Title`, `SummaryText`, `IconGlyph`, `TimestampUtc`, `Metadata` [`IReadOnlyDictionary<string, string>`], `Actions` [`ObservableList<TimelineActionModel>`])
  * Create: `Features/Timeline/Models/TimelineActionModel.cs` (`ActionId`, `ButtonText`, `CustomPayload`, `IsDestructive`, `Command`)
  * Create: `Features/Timeline/Services/ITimelineCardStateService.cs` & `Features/Timeline/Services/TimelineCardStateService.cs`
  * Create: `Features/Timeline/Services/DesktopTimelinePostSink.cs` (implements `Percolator.PluginSdk.ITimelinePostSink`)
  * Create: `Features/Timeline/Services/DesktopPluginActionHandler.cs` (implements `Percolator.PluginSdk.IPluginActionHandler`)
  * Create: `Features/Timeline/Views/TimelineView.axaml` & `Features/Timeline/Views/TimelineView.axaml.cs`
  * Create: `Features/Timeline/ViewModels/TimelineViewModel.cs`, `TimelineCardViewModel.cs`
* **Acceptance Criteria**:
  * `TimelineCardStateService`: Manages active timeline cards posted by plugins. Exposes `ObservableList<TimelineCardModel> Cards`. Concurrency-guarded methods for adding, updating, and dismissing cards.
  * Concrete Desktop Adapters for `Percolator.PluginSdk`:
    * `DesktopTimelinePostSink`: `PublishCardAsync` (translates DTO to `TimelineCardModel` and pushes to state service), `UpdateCardStatusAsync` (updates existing card reactively), `RemoveCardAsync` (dismisses card).
    * `DesktopPluginActionHandlerDispatcher`: Dispatches card action button clicks back to target plugin's registered action handler with custom payload.
  * `TimelineView`: Feed of interactive cards with header, source app icon, summary text, metadata chips, and action buttons. Dismiss button removing card from view and invoking `ITimelinePostSink.RemoveCardAsync`.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Timeline"`
  * Calling `PublishCardAsync` displays card. Clicking card action button executes command and invokes `HandleActionAsync`. Dismissing a card removes it from the UI and invokes `RemoveCardAsync` cleanly.

---

### [DESK-10] Desktop System Integration, Native Notifications & Quick-Switcher
* **QMS Traceability**: `DEP-DESKTOP-SESSION-LOCK-ZEROIZATION` (allocates `SYS-SESSION-LOCK-ZEROIZATION`), `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (allocates `SYS-CONTACT-CONSENT-GATE`)
* **Detailed Design**: `DEC-PULL-BASED-CONSENT-STAGING`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-004]`, `[VIEW-009]`, `[CB-001]`, `[CB-004]`, `[CB-008]`, `[CB-009]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-010]`, `[R3-004]`, `[R3-006]`
* **Target Deliverables**:
  * Create: `Features/Shell/Models/QuickSwitcherItemModel.cs`
  * Create: `Features/Shell/Views/QuickSwitcherDialog.axaml` & `Features/Shell/Views/QuickSwitcherDialog.axaml.cs`
  * Create: `Features/Shell/ViewModels/QuickSwitcherViewModel.cs`
  * Modify: `App.axaml` (cross-platform system tray icon configuration `TrayIcon`)
  * Create: `Common/Services/PlatformNotificationBridge.cs` (`IPlatformNotificationBridge`)
* **Acceptance Criteria**:
  * Global Quick-Switcher (`Ctrl+K` / `Ctrl+P`): Centered modal Command Palette (`QuickSwitcherDialog.axaml`). Fuzzy-search navigation across direct peers, relays, group chats, and commands (*"Add Connection"*, *"Lock Session"*). Selecting an item switches transport and conversation focus immediately.
  * System Tray Integration (`TrayIcon`): Cross-platform system tray icon configured in `App.axaml`. Context menu: *"Open Percolator"*, *"Lock Session (Stealth)"*, *"Exit"*. Minimize-to-tray on close when enabled in settings.
  * Desktop Notifications Bridge: Cross-platform in-app notification toasts (`FluentAvalonia.UI.Controls.InfoBar` banner). Platform bridge: WinRT Toast notifications on Windows, D-Bus notifications on Linux, Notification Center on macOS. **Inbound Handshake Silence Invariant Enforcement**: Inbound unsolicited handshakes never fire OS toasts or audio. Only messages in established chats or system security alerts trigger toasts.
  * Window State Preservation: Persist window position, width, height, and maximized state across restarts via `IShellWindowGeometryStore` (Stage-2 facade over `IVaultSettingsStore`, `VaultSettingKeys.ShellWindowGeometry` app-setting).
* **Verification Gate**:
  * Pressing `Ctrl+K` opens Quick-Switcher; fuzzy searching a peer and hitting Enter navigates directly to that conversation.
  * Closing window with "Minimize to tray" enabled hides window to notification area without terminating background services.
  * Inbound chat message triggers OS notification toast when window is not focused; inbound unsolicited handshake remains silent.

---

### [DESK-11] Headless UI & Concurrency Verification Test Suite
* **QMS Traceability**: Software Item Verification (IEC 62304 Clause 5.7 / FDA 21 CFR 820.30(f)), verifying `DEP-DESKTOP-PRE-ACTIVATION-STEALTH`, `DEP-DESKTOP-STARTUP-VAULT-AUTHENTICATION`, `DEP-DESKTOP-SESSION-LOCK-ZEROIZATION`, `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`, `DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION`, `DEP-DESKTOP-MEDIA-METADATA-SANITIZATION-INGRESS`
* **Detailed Design**: Verifies `INV-DESKTOP-TWO-STAGE-HANDOFF`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`, `INV-DESKTOP-PRESENTATION-MODELS`, `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-THREAD-DISCIPLINE`, `DEC-DEBOUNCED-DROP-RELOAD`, `DEC-LRU-SESSION-SCOPES`, `DEC-DIRECT-CANVAS-BITFIELD`, `DEC-REACTIVE-EVENT-BUS-BRIDGE`, `DEC-CANARY-ERROR26-UNWRAP`, `DEC-PULL-BASED-CONSENT-STAGING`
* **Governing Specs**: `[VM-008]`, `[VM-016]`, `[R3-009]`, `[CB-010]`
* **Target Deliverables**:
  * Scaffold / Enhance: `Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj` (Packages: `Avalonia.Headless.XUnit`, `Microsoft.NET.Test.Sdk`, `xunit`, `FluentAssertions`, `NSubstitute`, `R3`; headless test bootstrap: `[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`)
  * Create: `Percolator.Desktop.Tests/VaultGateTests.cs`
  * Create: `Percolator.Desktop.Tests/WindowHandoffTests.cs`
  * Create: `Percolator.Desktop.Tests/StealthModeTests.cs`
  * Create: `Percolator.Desktop.Tests/SessionScopeFactoryTests.cs`
  * Create: `Percolator.Desktop.Tests/ChatReloadCoordinatorTests.cs`
  * Create: `Percolator.Desktop.Tests/ChatDraftStateServiceTests.cs`
  * Create: `Percolator.Desktop.Tests/PieceMapControlTests.cs`
* **Acceptance Criteria**:
  * Vault Gate & Two-Stage Lifecycle Headless Tests:
    * Test `VaultSetupViewModel`: Assert `DetermineStatusAsync` returning `DatabaseSetupStatus.Uninitialized` presents setup view; assert empty password prompts confirmation dialog alert; assert duress password matching master password fails validation; assert successful initialization calls `IWindowHandoffCoordinator.TransitionToGateAsync`.
    * Test `VaultUnlockViewModel`: Assert `DetermineStatusAsync` returning `DatabaseSetupStatus.Locked` presents unlock view; assert invalid passphrase sets inline error message without crashing; assert valid passphrase calls `TransitionToGateAsync` (or `TransitionToMainShellAsync` if auto-start enabled); assert duress passphrase triggers decoy initialization and transitions into empty vault state without error popups.
    * Test `WindowHandoffCoordinator`: Assert `TransitionToMainShellAsync` starts hosted services, opens `MainWindow`, and closes `StartupWindow`; assert `LockSessionAsync` awaits `coordinator.LockAsync()`, closes `MainWindow`, and opens fresh `StartupWindow`.
  * Stealth Mode & Network Gate Verification Tests:
    * Assert zero network sockets are initialized upon test app startup. Sockets bind only after `IdentityGateViewModel.LaunchPersonasCommand.Execute()`. Locking session triggers `INetworkStealthGate.DeactivateAsync()`.
  * `SessionScopeFactory` LRU Cache Tests:
    * Simulate opening 6 consecutive chat sessions; verify the first session's `IServiceScope` is disposed and hot memory contains exactly 5 scopes.
  * `ChatReloadCoordinator` Debounce & Drop Tests:
    * Using `FakeTimeProvider`, simulate 100 rapid reload triggers in 10ms; assert database read service is invoked exactly once after 50ms advance.
  * `ChatDraftStateService` Volatile Caching Tests:
    * Verify draft text and attachment lists persist across conversation switches without touching SQLite.
  * Synchronized View Projection & Disposal Tests:
    * Verify `ObserveRemove` triggers `Dispose()` on projected ViewModel when item is removed from `ObservableList`.
  * Canvas Piece Map Bitfield Tests:
    * Verify bitfield state array calculation and direct canvas drawing performance for 10,000+ pieces.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj` executes cleanly in $< 5\text{ seconds}$ with 100% passing tests and zero cross-thread exceptions.

---

## 5. Port-to-Adapter Realization Matrix

This matrix maps all infrastructure ports and desktop adapters across their owning chunks, ensuring zero architectural disconnects between domain, infrastructure, plugins, and UI.

| Interface / Port | Layer / Source | Concrete Realization / Adapter | Chunk |
| :--- | :--- | :--- | :--- |
| `IDatabaseUnlockCoordinator` | Application Ports | `SqlCipherDatabaseUnlockCoordinator` (Infrastructure) | Chunk 2 (`[DESK-02]`) |
| `IAuthenticatedSession` | Application Ports | `AuthenticatedSession` (Infrastructure) | Chunk 4 (`[DESK-04]`) |
| `IWindowHandoffCoordinator` | Desktop Windowing | `WindowHandoffCoordinator` | Chunk 4 (`[DESK-04]`) |
| `INavigationService` | Desktop Shell | `NavigationService` | Chunk 1 (`[DESK-01]`) |
| `IWindowManager` | Desktop Shell | `WindowManager` | Chunk 1 (`[DESK-01]`) |
| `INetworkStealthGate` | Desktop Core | `NetworkStealthGate` | Chunk 3 (`[DESK-03]`) |
| `IPortReservationService` | Desktop Core | `PortReservationService` | Chunk 3 (`[DESK-03]`) |
| `IDesktopSettingsRepository`| Desktop Storage | `VaultDesktopSettingsRepository` (Stage-2) | Chunk 3 (`[DESK-03]`) |
| `IVaultSettingsStore` | Application Ports | `SqliteVaultSettingsStore` (Stage-2) | Chunk 3 (`[DESK-03]`) |
| `IShellWindowGeometryStore` | Desktop Windowing | `VaultShellWindowGeometryStore` (Stage-2) | Chunk 4 (`[DESK-04]`) |
| `IIdentityProfileRepository` | Domain Identities | `SqliteIdentityProfileRepository` (session scope) | Chunk 3 (`[DESK-03]`) |
| `ILocalIdentityKeyStore` | Domain Identities | `SqliteLocalIdentityKeyStore` (session scope) | Chunk 3 (`[DESK-03]`) |
| `IPeerConnectionStateService` | Desktop Transports | `PeerConnectionStateService` | Chunk 5 (`[DESK-05]`) |
| `IPeerContactQueryService` | Application Delivery | `SqlPeerContactQueryService` (Infrastructure) | Chunk 5 (`[DESK-05]`) |
| `IPendingHandshakeRepository`| Domain Delivery | `SqlitePendingHandshakeRepository` | Chunk 5 (`[DESK-05]`) |
| `IHandshakeService` | Application Handshake| `HandshakeService` | Chunk 5 (`[DESK-05]`) |
| `IChatStateService` | Desktop Chats | `ChatStateService` | Chunk 6 (`[DESK-06]`) |
| `IChatDraftStateService` | Desktop Chats | `ChatDraftStateService` | Chunk 6 (`[DESK-06]`) |
| `IChatService` | Apps Chat | `ChatService` | Chunk 6 (`[DESK-06]`) |
| `IChatMessageQueryService` | Apps Chat | `SqlChatMessageQueryService` (Infrastructure) | Chunk 6 (`[DESK-06]`) |
| `IConversationListQueryService`| Apps Chat | `SqlConversationListQueryService` (Infrastructure)| Chunk 6 (`[DESK-06]`) |
| `IDiscoveryStateService` | Desktop Discovery | `DiscoveryStateService` | Chunk 7 (`[DESK-07]`) |
| `INearbyPeerDiscoveryWorkflow` | Apps Discovery | `NearbyPeerDiscoveryWorkflow` | Chunk 7 (`[DESK-07]`) |
| `IPrivateRendezvousService` | Apps Discovery | `PrivateRendezvousService` | Chunk 7 (`[DESK-07]`) |
| `IRelayPeerDirectory` | Apps Discovery | `SqliteRelayPeerDirectory` (Infrastructure) | Chunk 7 (`[DESK-07]`) |
| `IFileTransferStateService` | Desktop FileTransfer| `FileTransferStateService` | Chunk 8 (`[DESK-08]`) |
| `IFileTransferQueryService` | Apps FileTransfer | `FileTransferQueryService` | Chunk 8 (`[DESK-08]`) |
| `ITransferSessionRepository` | Apps FileTransfer | `SqliteTransferSessionRepository` (Infrastructure)| Chunk 8 (`[DESK-08]`) |
| `IDesktopFileStorage` | Apps FileTransfer | `PortableDesktopFileStore` (Infrastructure) | Chunk 8 (`[DESK-08]`) |
| `ITimelineCardStateService` | Desktop Timeline | `TimelineCardStateService` | Chunk 9 (`[DESK-09]`) |
| `ITimelinePostSink` | PluginSdk | `DesktopTimelinePostSink` | Chunk 9 (`[DESK-09]`) |
| `IPluginActionHandler` | PluginSdk | `DesktopPluginActionHandlerDispatcher` | Chunk 9 (`[DESK-09]`) |
| `IDateTimeProvider` | Domain Common | `SystemDateTimeProvider` | Chunk 1 (`[DESK-01]`) |

---

## 6. Developer Tooling & Simulation Interoperability

Testing multi-peer network topologies, relays, and handshakes is handled by the standalone companion project **`Percolator.Simulator`** (see [`Percolator.Simulator/implementation-plan.md`](../Percolator.Simulator/implementation-plan.md)).

`Percolator.Desktop` contains **zero simulator code** or mock interceptors. To interoperate with the simulator during development, `Percolator.Desktop` supports a development configuration flag:
* `Development:AllowLocalhostPeers: true` (or `--allow-localhost` CLI argument).
* When set, loopback `127.0.0.1` targets are permitted as valid peer endpoints without triggering production WAN stealth security rejections.
