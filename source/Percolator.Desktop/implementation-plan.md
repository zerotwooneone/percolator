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
    * **Shared State (Owned by Stateful UI Services)**: Cross-cutting presentation state (active persona, configured relays, quarantined consent queue, conversation directory, and volatile drafts) is owned exclusively by singleton/scoped **Stateful UI Services** (`CurrentPersonaService`, `RelayStateService`, `InboundRequestStateService`, `ConversationStateService`, `ShellNavigationStateService`, `ChatDraftStateService`, `DiscoveryStateService`, `FileTransferStateService`, `TimelineCardStateService`).
   * Stateful UI Services are **100% UI-agnostic**—they operate on background worker threads, guard mutations with concurrency gates (`lock` / `SemaphoreSlim`), and expose canonical read-only synchronized views (`ISynchronizedView<TSource, TView>`) or `IReadOnlyList<T>` (`[UISS-002]`, `[UISS-005]`).
6. **Volatile Draft & LRU ViewModel Caching (`DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION`, `DEC-LRU-SESSION-SCOPES`, `DEC-DEBOUNCED-DROP-RELOAD`)**:
   * The desktop application maintains an in-memory `ChatDraftStateService` and an LRU cache of recently active `ChatViewModel` instances (capacity 3–5).
   * Switching between chats or personas restores unsent message drafts, scroll positions, and attachment states from volatile memory without database re-fetching or schema pollution.
7. **Strict R3 Reactive Programming Rules (`R3-001` through `R3-014`)**:
   * **Avalonia Dispatcher Integration (`R3-001`)**: Configured in `Program.cs` via `.UseR3()`, wiring `AvaloniaDispatcherTimeProvider` and `AvaloniaDispatcherFrameProvider` into `ObservableSystem`.
   * **Filter Early, Marshal Late (`R3-002`, `R3-011`)**: Perform filtering, deduplication, debounce, and sorting on background worker threads; only marshal to the UI thread at the terminal step via `.ObserveOnCurrentSynchronizationContext()` or `.ObserveOnUIThread()`.
   * **Throttle the Firehose (`R3-007`)**: High-frequency streaming events (network packet rates, DHT search hops, file piece bitfields) must be sampled (`.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)`) to protect the UI thread from 60fps+ inundation.
8. **CQRS Read Model Boundary & Domain Repository Prohibition (`[VM-006]`, `anti-shortcut-discipline` Rule 6)**:
   * **Strict CQRS Read Rule**: UI State Services and ViewModels MUST NEVER inject write-side domain aggregate repositories (`*Repository`) to load data for UI display.
   * **Mandatory Query Services**: All UI list populations and read projections MUST consume dedicated CQRS Query Services (`*QueryService`: `IPersonaRelayQueryService`, `IPeerContactQueryService`, `IConversationListQueryService`, `IChatMessageQueryService`, `IPeerProfileQueryService`) returning flat, immutable read models (`*ReadModel`). Domain repositories are reserved strictly for transactional write operations inside application workflows.
9. **Zero-Tolerance Anti-Shortcut Disciplines (`[VM-003]`, `[VM-018]`, `[VM-019]`, `[UISS-002]`, `[UISS-005]`, `[UISS-006]`)**:
   * **Mandatory Constructor DI (`[VM-003]`)**: All required dependencies must be non-nullable constructor parameters validated with `ArgumentNullException.ThrowIfNull(dep)`. Nullable defaults (`dep = null`) or default mock fallbacks to take shortcuts in tests are strictly prohibited.
   * **Prohibition Against Synthetic In-Memory Databases (`anti-shortcut-discipline` Rule 2)**: Storing mutable dictionaries of all messages or contacts in RAM as a duplicate database is strictly prohibited. Historical data resides in encrypted SQLite; UI State Services manage only shared *transient* presentation state.
   * **Safe Collection Event Dispatching (`[VM-019]`)**: When calling `view.ToNotifyCollectionChanged(...)`, conditionally pass `SynchronizationContextCollectionEventDispatcher.Current` only if `SynchronizationContext.Current != null`. Unconditional access throws `TypeInitializationException` in unit tests.
   * **Compiled Bindings Contract (`[VM-018]`)**: All Views and `DataTemplate` roots must specify explicit `x:DataType` to prevent runtime reflection fallback.

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
    subgraph Phase 1: Shell & Theming Bootstrap (Completed)
        C1["[DESK-01] Shell Token Foundation & Responsive 3-Pane Frame (Completed)"]
        C2["[DESK-02] Pre-Unlock Vault Gate (StartupWindow Screen 1A & 1B + Duress Decoy) (Completed)"]
        C3["[DESK-03] Multi-Persona Gate & Stealth Port Binding (Screen 2 + DesktopIdentitySettings) (Completed)"]
        C4["[DESK-04] Two-Stage DI & Window Handoff Coordinator (Lifecycle & Relocking) (Completed)"]
    end

    subgraph Phase 2: Transports, Navigation & Messaging (Discord Model)
        C5["[DESK-05] State Services Foundation & Left Rail Discord Architecture (Flows A & C Phase 1) (Completed)"]
        C6["[DESK-06] Direct P2P & Relayed Conversation Directory with Outbound Pending (Flows B & C Phase 2)"]
        C7["[DESK-07] Real-Time Messaging Experience, Volatile Drafts & Disappearing Messages (Apps.Chat)"]
        C7A["[DESK-07-A] Inbound Handshake Domain/Application Event Pipeline & Simulator Decoupling"]
        C8["[DESK-08] Right-Anchored Profile & Connection Inspector Drawer"]
    end

    subgraph Phase 3: P2P Swarm & Platform Services
        C9["[DESK-09] Sovereign Discovery & Swarm Telemetry (Apps.Discovery)"]
        C10["[DESK-10] Swarm File Transfer Dashboard & In-Chat PieceMap Canvas (Apps.FileTransfer)"]
        C11["[DESK-11] Timeline Cards & PluginSdk Integration (PluginSdk)"]
        C12["[DESK-12] Desktop System Integration, Native Notifications & Quick-Switcher (Ctrl+K)"]
        C13["[DESK-13] Headless UI & Comprehensive Verification Test Suite"]
    end

    C1 --> C2 --> C3 --> C4 --> C5 --> C6 --> C7 --> C7A --> C8 --> C9 --> C10 --> C11 --> C12 --> C13
```

---

### [DESK-01] Shell Token Foundation & Responsive 3-Pane Frame (COMPLETED)
* **Summary**: Established the base FluentAvalonia theme, geometry/spacing tokens (`Spacing.axaml`, `CustomStyles.axaml`), embedded FiraCode Nerd Font, and the responsive 3-pane shell frame in `MainWindow.axaml` ($1200 \times 800\text{ px}$ target, $72\text{ px}$ left rail, $240\text{ px}$ right sidebar, content pane, and $300\text{ px}$ right-anchored inspector drawer). Implemented responsive layout breakpoints switching the inspector to overlay below $1000\text{ px}$ and the right sidebar into a slide-out drawer below $850\text{ px}$. Verified clean theme switching across Light, Dark, and High Contrast.

---

### [DESK-02] Pre-Unlock Vault Gate & Duress Decoy (COMPLETED)
* **Summary**: Implemented the unified $480 \times 600\text{ px}$ non-resizable `StartupWindow` hosting Screen 1A (`VaultSetupView`: master password + optional duress passphrase + empty password alert dialog) and Screen 1B (`VaultUnlockView`: Argon2id key derivation, native error 26 inline feedback, and twin-slot duress cryptographic wipe to decoy vault). Added canary verification trapping `SQLITE_NOTADB` and verified clean in-memory error transitions without crashing.

---

### [DESK-03] Multi-Persona Gate & Stealth Port Binding (COMPLETED)
* **Summary**: Implemented Screen 2 (`IdentityGateView`), multi-persona card presentation with identicon generation, persona creation (`CreatePersonaDialog`) and deletion (`DeletePersonaDialog`), and vault-backed `VaultDesktopSettingsRepository` storing launch settings inside the encrypted vault. Added dynamic port probing and conflict resolution across `5100`–`5299` via `PortReservationService` while enforcing total network stealth (zero open ports prior to explicit persona confirmation).

---

### [DESK-04] Two-Stage DI & Window Handoff Coordinator (COMPLETED)
* **Summary**: Established the two-stage dependency injection lifecycle (`BootstrapContainer` for Stage 1 pre-unlock vs. `AuthenticatedSession` for Stage 2 post-unlock) coordinated by `WindowHandoffCoordinator`. Implemented `TransitionToMainShellAsync` (port leasing, hosted services startup, geometry restoration) and `LockSessionAsync` (network listener teardown via `NetworkStealthGate`, SQLite connection pool clearing, master key zeroization via `CryptographicOperations.ZeroMemory`, and window swap back to `StartupWindow`). Established EF Core migration baseline and verified schema evolution.

---

### [DESK-05] State Services Foundation & Left Rail Discord Architecture (Flows A & C Phase 1) (COMPLETED)
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE`, `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (allocates `SYS-CONTACT-CONSENT-GATE`, `SYS-DIRECT-CONTACT-REQUEST-OUTBOX`)
* **Detailed Design**: `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-THREAD-DISCIPLINE`, `DEC-PULL-BASED-CONSENT-STAGING`
* **Governing Specs**: `[UISS-001]` to `[UISS-010]`, `[STYLE-002]`, `[STYLE-005]`, `[VIEW-001]`, `[VIEW-002]`, `[CB-001]`, `[CB-004]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-006]`, `[VM-017]`, `[VM-018]`, `[VM-019]`, `[R3-001]` to `[R3-014]`
* **Target Deliverables**:
  * Create: `Common/Navigation/LeftRailTarget.cs` (`DirectMessages` vs `Relay(RelayIdentityId)`)
  * Create: `Common/Navigation/IShellNavigationStateService.cs` & `ShellNavigationStateService.cs` (coordinates active Left Rail target, selected chat ID, and inspector drawer state)
  * Create: `Features/Transports/Models/RelayChannelModel.cs` (`RelayIdentityId`, `FriendlyName`, `Endpoint`, `ConnectionStatus`, `LatencyMs`, `UnreadCount`, `FolderId`, `DisplayOrder`)
  * Create: `Features/Transports/Services/IRelayStateService.cs` & `RelayStateService.cs` (manages active persona's configured relays, live ping/status, folders, unread badges, and vertical ordering)
  * Create: `Features/Transports/Services/IInboundRequestStateService.cs` & `InboundRequestStateService.cs` (quarantines unsolicited inbound contact requests, exposes `PendingCount` for indicator light, and provides `Accept`/`Reject`/`Block` actions)
  * Create: `Features/Transports/Views/TransportsSidebarView.axaml` & `Features/Transports/ViewModels/TransportsSidebarViewModel.cs` (Left Sidebar rail: DM button, dynamic relay collection, folders, `+`, pending requests button, avatar)
  * Modify: `Features/Shell/Views/MainWindow.axaml` & `Features/Shell/ViewModels/MainWindowViewModel.cs` (embed `TransportsSidebarView`, wire `AddConnectionDialogResult` into `RelayStateService`)
* **CQRS Query & Anti-Shortcut Boundaries**:
  * **Mandatory CQRS Read Port**: `RelayStateService` MUST inject `IPersonaRelayQueryService` to load `PersonaRelayLinkReadModel` list snapshots. `InboundRequestStateService` MUST inject `IPeerContactQueryService` to query `PeerContactSummaryReadModel` list snapshots.
  * **Prohibited Domain Repositories**: Implementing agents MUST NOT inject write-side domain repositories (`IRelayRepository`, `IPeerContactRepository`) to populate UI state or read models (`[VM-006]`, `anti-shortcut-discipline` Rule 6).
  * **Constructor Invariants**: All required service dependencies MUST be non-nullable constructor parameters (`ArgumentNullException.ThrowIfNull`) (`[VM-003]`).
  * **Compiled Bindings**: `TransportsSidebarView.axaml` must declare `x:DataType="vm:TransportsSidebarViewModel"`, and relay list `DataTemplate` must declare `x:DataType="models:RelayChannelModel"` (`[VM-018]`).
  * **Collection Dispatcher Fallback**: Collection bindings MUST use `SynchronizationContext.Current != null ? view.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current) : view.ToNotifyCollectionChanged()` (`[VM-019]`).
* **Acceptance Criteria**:
  * **Flow A (Add Relay Post Office)**:
    * User clicks `[+] Add Connection` in the Left Sidebar, enters a relay endpoint (e.g. `127.0.0.1:5100`), and submits.
    * `AddConnectionDialogViewModel` registers the relay in SQLite (`IRelayDirectory`), links identity, and calls `RelayStateService.AddRelayAsync(...)`.
    * Left Sidebar immediately renders the new Relay icon with friendly display name tooltip and connection status indicator.
  * **Flow C Phase 1 (Left Rail Navigation)**:
    * Top icon: Prominent `[Direct Messages]` button with aggregate direct P2P unread message badge. Clicking sets `ShellNavigationStateService.ActiveLeftRailTarget = LeftRailTarget.DirectMessages`.
    * Middle list: Configured Relays rendered as vertical icon stack with live ping status. Drag-and-drop vertical reordering persisted to `IDesktopSettingsRepository`. Collapsible relay folder support. Clicking a relay sets `ShellNavigationStateService.ActiveLeftRailTarget = LeftRailTarget.Relay(relayId)`.
    * Bottom Pending Requests: Subtle notification indicator light bound to `InboundRequestStateService.PendingCount`. Inbound unsolicited handshakes illuminate badge without OS toasts or audio (`INV-UNSOLICITED-CONNECTION-SILENCE`). Clicking opens `PendingRequestsPanelViewModel` backed by `InboundRequestStateService`.
  * **Free-Threaded & Thread-Safe (`UiStateService.spec.md`)**:
    * All state services are 100% free-threaded (`[UISS-002]`), mutate state under lock (`[UISS-003]`), publish events outside lock (`[UISS-004]`), and expose `ISynchronizedView` (`[UISS-005]`).
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~RelayState|FullyQualifiedName~Navigation|FullyQualifiedName~TransportsSidebar"`
  * Verifies adding relay dynamically renders icon in Left Rail; clicking relay updates navigation target; drag-and-drop reorders collection; inbound request increments pending count.

---

### [DESK-06] Direct P2P & Relayed Conversation Directory with Outbound Pending (Flows B & C Phase 2)
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE`, `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (allocates `SYS-CONTACT-CONSENT-GATE`, `SYS-DIRECT-CONTACT-REQUEST-OUTBOX`), `DEP-DESKTOP-PEER-BLOCK-UI-SILENCE` (allocates `SYS-PEER-BLOCK-SILENT-DROP`)
* **Detailed Design**: `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-PRESENTATION-MODELS`, `DEC-PULL-BASED-CONSENT-STAGING`
* **Governing Specs**: `[UISS-001]` to `[UISS-010]`, `[STYLE-002]`, `[STYLE-005]`, `[VIEW-001]`, `[VIEW-003]`, `[CB-001]`, `[CB-004]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-006]`, `[VM-017]`, `[VM-018]`, `[VM-019]`, `[R3-001]`, `[R3-008]`, `[R3-010]`
* **Target Deliverables**:
  * Delete / Replace: `Features/Transports/Services/IPeerConnectionStateService.cs` & `PeerConnectionStateService.cs` (replace orphan retry-only class with comprehensive conversation directory)
  * Create: `Features/Chats/Models/ConversationSummaryModel.cs` (`ConversationId`, `PeerOrGroupId`, `DisplayName`, `AvatarHash`, `RouteType` [`DirectP2P`, `Relayed`], `RelayId`, `LastMessageSnippet`, `LastActivityUtc`, `UnreadCount`, `IsPendingOutbound`, `DeliveryStatus`)
  * Create: `Features/Chats/Services/IConversationStateService.cs` & `ConversationStateService.cs` (manages active persona conversations, encapsulates `IOutboundPendingRetryWorker`, projects P2P DMs vs Relayed chats)
  * Create: `Features/Chats/Views/ChatsSidebarView.axaml` & `Features/Chats/Views/ChatsSidebarView.axaml.cs`
  * Create: `Features/Chats/ViewModels/ChatsSidebarViewModel.cs`
  * Modify: `Features/Transports/ViewModels/AddConnectionDialogViewModel.cs` (Tab 0 direct peer connections wired to `ConversationStateService`)
  * Modify: `Features/Shell/ViewModels/MainWindowViewModel.cs` & `MainWindow.axaml` (embed `ChatsSidebarView` in 240px right sidebar pane)
* **CQRS Query & Anti-Shortcut Boundaries**:
  * **Mandatory CQRS Read Ports**: `ConversationStateService` MUST inject `IConversationListQueryService` (loading `ConversationSummaryReadModel`) and `IPeerContactQueryService` (loading `PeerContactSummaryReadModel`).
  * **Prohibited Domain Repositories**: Implementing agents MUST NOT inject write-side domain repositories (`IChatRepository`, `IPeerContactRepository`) to query conversation lists or direct contacts (`[VM-006]`, `anti-shortcut-discipline` Rule 6).
  * **No Synthetic Message Databases**: `ConversationStateService` MUST NOT hold message histories or aggregate entity graphs in RAM (`anti-shortcut-discipline` Rule 2). Only active conversation summaries, presence, and outbound retry placeholders reside in presentation state.
  * **Compiled Bindings**: `ChatsSidebarView.axaml` must declare `x:DataType="vm:ChatsSidebarViewModel"`, and conversation list `DataTemplate` must declare `x:DataType="models:ConversationSummaryModel"` (`[VM-018]`).
  * **Collection Dispatcher Fallback**: Collection bindings MUST use `SynchronizationContext.Current != null ? view.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current) : view.ToNotifyCollectionChanged()` (`[VM-019]`).
* **Acceptance Criteria**:
  * **Flow B (Add Direct Connection & Outbound Pending)**:
    * User clicks `[+] Add Connection`, selects Tab 0 (*"Connect to Direct Peer"*), pastes peer invitation, clicks `Connect`.
    * If peer responds immediately: handshake establishes contact; `ConversationStateService` registers direct P2P conversation; appears in Right Sidebar immediately.
    * If peer is offline (`PendingOffline`): staged as outbound pending; `OutboundPendingRetryWorker` begins background retry; appears in Right Sidebar under `[Direct Messages]` with *"Pending..."* badge.
    * When background retry succeeds: `OutboundPendingRetryWorker` fires event; `ConversationStateService` automatically transitions item from *"Pending..."* to *"Connected"*.
  * **Flow C Phase 2 (Contextual Right Sidebar Population)**:
    * When `ShellNavigationStateService.ActiveLeftRailTarget` is `DirectMessages`: Right Sidebar displays all direct, peer-to-peer (LAN/STUN, no relay) 1:1 conversations, plus P2P outbound-pending items.
    * When `ShellNavigationStateService.ActiveLeftRailTarget` is `Relay(relayId)`: Right Sidebar displays all 1:1 direct chats routing through that relay, plus group chats hosted on that relay.
    * Selecting a chat updates `ShellNavigationStateService.SelectedConversationId`.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~ConversationState|FullyQualifiedName~ChatsSidebar"`
  * Verifies selecting Direct Messages displays P2P chats; selecting Relay displays relayed chats; offline outbound peer displays *"Pending..."* badge; retry success transitions badge to connected.

---

### [DESK-07] Real-Time Messaging Experience, Volatile Drafts & Disappearing Messages (Apps.Chat)
* **QMS Traceability**: `DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION` (allocates `SYS-VOLATILE-DRAFT-ISOLATION`), `DEP-DESKTOP-MEDIA-METADATA-SANITIZATION-INGRESS` (allocates `SYS-MEDIA-METADATA-SANITIZATION-INGRESS`)
* **Detailed Design**: `DEC-LRU-SESSION-SCOPES`, `DEC-DEBOUNCED-DROP-RELOAD`, `DEC-REACTIVE-EVENT-BUS-BRIDGE`, `INV-DESKTOP-PRESENTATION-MODELS`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-005]`, `[CB-001]`, `[CB-003]`, `[CB-004]`, `[CB-008]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-007]`, `[VM-010]`, `[VM-012]`, `[VM-014]`, `[R3-002]`, `[R3-003]`, `[R3-004]`, `[R3-005]`, `[R3-006]`, `[R3-007]`, `[R3-009]`, `[R3-010]`, `[R3-013]`
* **Target Deliverables**:
  * Create: `Features/Chats/Models/ChatMessageModel.cs` & `ChatMessageSnapshot.cs`
  * Create: `Features/Chats/Services/IChatDraftStateService.cs` & `ChatDraftStateService.cs` (volatile in-memory drafts, cursor positions, and pending attachments)
  * Create: `Features/Chats/Services/ChatReloadCoordinator.cs` (debounced `50ms` SQLite query batching with `AwaitOperation.Drop`)
  * Create: `Features/Chats/Services/SessionScopeFactory.cs` (LRU child DI container cache for active chats, capacity 5)
  * Create: `Features/Chats/Controls/ChatComposer.axaml` & `ChatComposer.axaml.cs`
  * Create: `Features/Chats/Controls/ReactionPickerFlyout.axaml`
  * Create: `Features/Chats/Views/ChatView.axaml` & `Features/Chats/Views/ChatView.axaml.cs`
  * Create: `Features/Chats/ViewModels/ChatViewModel.cs`, `ChatMessageViewModel.cs`
  * Create: `Features/Chats/Views/GroupCreationDialog.axaml` & `Features/Chats/ViewModels/GroupCreationDialogViewModel.cs`
* **CQRS Query & Anti-Shortcut Boundaries**:
  * **Mandatory CQRS Read Port**: `ChatViewModel` MUST inject `IChatMessageQueryService` (loading `ChatMessageReadModel` pages from SQLite). Write operations (e.g. sending a message) MUST route through application port `IChatService.SendMessageAsync`.
  * **Prohibited Domain Repositories**: Implementing agents MUST NOT inject write-side domain repositories (`IChatMessageRepository`, `IConversationRepository`) into `ChatViewModel` (`[VM-006]`).
  * **No Synthetic Message Databases**: `ChatViewModel` must not hold unbounded historical message collections in RAM; paging queries load on demand.
  * **Compiled Bindings**: `ChatView.axaml` must declare `x:DataType="vm:ChatViewModel"`, and message item template must declare `x:DataType="vm:ChatMessageViewModel"` (`[VM-018]`).
* **Acceptance Criteria**:
  * `ChatView`: Displays selected conversation header, virtualized message list, asymmetric bubbles (right-aligned accent for sent, left-aligned surface for received), delivery ticks (Sent, Delivered, Read, Failed), and emoji reaction chips.
  * Pending Connection Banner: When viewing an outbound-pending chat, displays informational banner: *"Waiting for peer to come online. Messages will be queued once connected."* Composer is disabled until handshake completes.
  * `ChatDraftStateService`: Switching between chats or Left Rail modes preserves unsent message drafts, scroll positions, and attachment lists in volatile RAM without database writes.
  * `GroupCreationDialog`: Group creation UI restricted to members sharing a common relay. Orchestrates pairwise relay invitations and awaits peer join confirmations before dispatching Groups V2 ZK invitations.
  * Disappearing Messages: Messages with `ExpiresAtUtc` render countdown indicator; reaper timer updates tombstones (*"This message was deleted"*) upon expiration.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Chat|FullyQualifiedName~Scope"`
  * Verifies draft restoration on chat switch; 50ms debounced SQLite reload; pending banner behavior; disappearing message tombstone transitions.

---

### [DESK-07-A] Inbound Handshake Domain/Application Event Pipeline & Simulator Decoupling
* **QMS Traceability**: `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (allocates `SYS-CONTACT-CONSENT-GATE`), `DEP-DESKTOP-CRYPTO-BOUNDARY` (allocates `SYS-CRYPTO-BOUNDARY-ISOLATION`)
* **Detailed Design**: `DEC-PULL-BASED-CONSENT-STAGING`, `INV-APP-EVENT-BUS-DECOUPLING`, `INV-RECORD-EVENT-ENFORCEMENT`, `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[VM-014]`, `[R3-004]`, `[R3-005]`, `[R3-006]`, `[UISS-001]`, `[UISS-002]`
* **Target Deliverables**:
  * Create: `Percolator.Domain/Identities/Events/InboundContactRequestReceivedDomainEvent.cs`
    * Record: `public sealed record InboundContactRequestReceivedDomainEvent(PublicIdentityId OwnerIdentityId, PublicIdentityId RemotePeerId, IdentityKey PrimaryPublicKey, DateTimeOffset OccurredOnUtc) : IDomainEvent` with `public Guid EventId { get; } = Guid.NewGuid();`
  * Create: `Percolator.Domain/Identities/Events/ContactRequestApprovedDomainEvent.cs`
    * Record: `public sealed record ContactRequestApprovedDomainEvent(PublicIdentityId OwnerIdentityId, PublicIdentityId RemotePeerId, PeerTrustLevel TrustLevel, DateTimeOffset OccurredOnUtc) : IDomainEvent` with `public Guid EventId { get; } = Guid.NewGuid();`
  * Create: `Percolator.Domain/Identities/Events/ContactRequestRejectedDomainEvent.cs`
    * Record: `public sealed record ContactRequestRejectedDomainEvent(PublicIdentityId OwnerIdentityId, PublicIdentityId RemotePeerId, bool IsBlocked, DateTimeOffset OccurredOnUtc) : IDomainEvent` with `public Guid EventId { get; } = Guid.NewGuid();`
  * Modify: `Percolator.Domain/Identities/Model/PeerContact.cs`
    * `CreateInboundRequest(...)`: Append `contact.AddDomainEvent(new InboundContactRequestReceivedDomainEvent(ownerIdentityId, remotePeerId, primaryPublicKey, timeProvider.UtcNow));` before returning success.
    * `Approve(...)`: Update signature to `public DomainResult Approve(PeerTrustLevel initialTrust = PeerTrustLevel.Tofu, IDateTimeProvider? timeProvider = null)`. Append `AddDomainEvent(new ContactRequestApprovedDomainEvent(OwnerIdentityId, RemotePeerId, TrustLevel, timeProvider?.UtcNow ?? DateTimeOffset.UtcNow));` on transition to `ContactState.Active`.
    * `Reject(...)`: Update signature to `public DomainResult Reject(bool block = false, IDateTimeProvider? timeProvider = null)`. Append `AddDomainEvent(new ContactRequestRejectedDomainEvent(OwnerIdentityId, RemotePeerId, block, timeProvider?.UtcNow ?? DateTimeOffset.UtcNow));` on transition to `ContactState.Rejected`.
  * Create: `Percolator.Application/Profiles/Events/InboundContactRequestReceivedEvent.cs`
    * Explicit Property Types (prevents primitive Value Object unboxing to naked Guids):
      ```csharp
      namespace Percolator.Application.Profiles.Events;
      [Percolator.SourceGenerators.MapFromDomainEvent(typeof(Percolator.Domain.Identities.Events.InboundContactRequestReceivedDomainEvent))]
      public sealed partial record InboundContactRequestReceivedEvent : Percolator.Application.Ports.IApplicationEvent
      {
          public Percolator.Domain.Identities.PublicIdentityId OwnerIdentityId { get; init; }
          public Percolator.Domain.Identities.PublicIdentityId RemotePeerId { get; init; }
          public Percolator.Domain.Identities.ValueObjects.IdentityKey PrimaryPublicKey { get; init; }
      }
      ```
  * Create: `Percolator.Application/Profiles/Events/ContactRequestApprovedEvent.cs`
    * Explicit Property Types:
      ```csharp
      namespace Percolator.Application.Profiles.Events;
      [Percolator.SourceGenerators.MapFromDomainEvent(typeof(Percolator.Domain.Identities.Events.ContactRequestApprovedDomainEvent))]
      public sealed partial record ContactRequestApprovedEvent : Percolator.Application.Ports.IApplicationEvent
      {
          public Percolator.Domain.Identities.PublicIdentityId OwnerIdentityId { get; init; }
          public Percolator.Domain.Identities.PublicIdentityId RemotePeerId { get; init; }
          public Percolator.Domain.Identities.Model.PeerTrustLevel TrustLevel { get; init; }
      }
      ```
  * Create: `Percolator.Application/Profiles/Events/ContactRequestRejectedEvent.cs`
    * Explicit Property Types:
      ```csharp
      namespace Percolator.Application.Profiles.Events;
      [Percolator.SourceGenerators.MapFromDomainEvent(typeof(Percolator.Domain.Identities.Events.ContactRequestRejectedDomainEvent))]
      public sealed partial record ContactRequestRejectedEvent : Percolator.Application.Ports.IApplicationEvent
      {
          public Percolator.Domain.Identities.PublicIdentityId OwnerIdentityId { get; init; }
          public Percolator.Domain.Identities.PublicIdentityId RemotePeerId { get; init; }
          public bool IsBlocked { get; init; }
      }
      ```
  * Modify: `Percolator.Application/Profiles/ContactRequestCoordinator.cs`
    * Constructor: Add non-nullable `IEventPublisher eventPublisher` parameter (`_eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));`).
    * `HandleInboundRequestAsync(...)`: After `await _contactRepo.SaveAsync(contact, ct);`, iterate `contact.DomainEvents`, map `InboundContactRequestReceivedDomainEvent` to `InboundContactRequestReceivedEvent.FromDomain(e)`, publish via `await _eventPublisher.PublishAsync(appEvent, ct);`, then `contact.ClearDomainEvents();`. (Note: Existing contacts in `PendingApproval` state exit early without raising events, preserving idempotency).
    * `ApproveRequestAsync(...)`: Pass `_timeProvider` to `contact.Approve(initialTrust, _timeProvider)`. After `await _contactRepo.SaveAsync(contact, ct);`, iterate `contact.DomainEvents`, map `ContactRequestApprovedDomainEvent` to `ContactRequestApprovedEvent.FromDomain(e)`, publish via `await _eventPublisher.PublishAsync(appEvent, ct);`, then `contact.ClearDomainEvents();`.
    * `RejectRequestAsync(...)`: Pass `_timeProvider` to `contact.Reject(block, _timeProvider)`. After `await _contactRepo.SaveAsync(contact, ct);`, iterate `contact.DomainEvents`, map `ContactRequestRejectedDomainEvent` to `ContactRequestRejectedEvent.FromDomain(e)`, publish via `await _eventPublisher.PublishAsync(appEvent, ct);`, then `contact.ClearDomainEvents();`.
  * Update Affected Application & Integration Tests for `ContactRequestCoordinator` ctor change:
    * `Percolator.Application.Tests/Handshake/HandshakeServiceTests.cs` (supply mock `IEventPublisher` or `InMemoryEventBus`)
    * `Percolator.Application.Tests/Ingress/AppHostPipelineTests.cs` (supply mock `IEventPublisher`)
    * `Percolator.Application.Tests/Profiles/ProfileAndContactTests.cs` (supply mock `IEventPublisher` and verify published events)
    * `Percolator.ApplicationIntegrationTests/Builders/TestIdentityNode.cs` (supply host `IEventPublisher`)
  * Modify: `Percolator.Desktop/Features/Transports/Services/InboundRequestStateService.cs`
    * Constructor: Add non-nullable `IEventSubscriber eventSubscriber` parameter (`_eventSubscriber = eventSubscriber ?? throw new ArgumentNullException(nameof(eventSubscriber));`).
    * Event Subscriptions:
      * `eventSubscriber.Subscribe<InboundContactRequestReceivedEvent>(evt => { if (evt.OwnerIdentityId == _activePersonaId) _ = LoadRequestsForPersonaAsync(evt.OwnerIdentityId); }).AddTo(ref _bag);`
      * `eventSubscriber.Subscribe<ContactRequestApprovedEvent>(evt => { if (evt.OwnerIdentityId == _activePersonaId) _ = LoadRequestsForPersonaAsync(evt.OwnerIdentityId); }).AddTo(ref _bag);`
      * `eventSubscriber.Subscribe<ContactRequestRejectedEvent>(evt => { if (evt.OwnerIdentityId == _activePersonaId) _ = LoadRequestsForPersonaAsync(evt.OwnerIdentityId); }).AddTo(ref _bag);`
  * Update: `Percolator.Desktop.Tests/Features/Transports/InboundRequestStateServiceTests.cs`
    * Update `CreateService()` to supply mock `IEventSubscriber`.
    * Update `Constructor_WithNullDependencies_ThrowsArgumentNullException` to include permutation check for null `IEventSubscriber`.
    * Add unit tests verifying `InboundContactRequestReceivedEvent` and `ContactRequestApprovedEvent` trigger `LoadRequestsForPersonaAsync`.
  * Delete: `Percolator.Simulator/Features/Topology/SimulatorNotifyingHandshakeDecorator.cs`
  * Modify: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs`
    * Remove registration and constructor use of `SimulatorNotifyingHandshakeDecorator`. Register `services.AddScoped<IHandshakeService, HandshakeService>();` directly.
  * Modify: `Percolator.Simulator/Features/Topology/PersonaCardViewModel.cs`
    * Remove `_stateBus.Signals.Where(s => s.Category == SignalCategory.PendingHandshakeChanged)` listener for direct handshakes.
    * When `_host != null`, resolve `IEventSubscriber` from `_hostScope.ServiceProvider`.
    * Subscribe to `InboundContactRequestReceivedEvent` and `ContactRequestApprovedEvent` filtered by `evt.OwnerIdentityId == _host.PublicIdentityId`, invoking `RefreshPendingHandshakes()`, and registering via `.AddTo(ref Disposables)`.
  * Modify: `Percolator.Simulator/Features/Scenarios/ScenarioRunnerService.cs`
    * Replace `_stateBus.Signals.Where(s => s.NodeId == target.Id && s.Category == SignalCategory.PendingHandshakeChanged)` in lines 558, 632, 1582 with reactive event subscription on the target host's event subscriber: `target.Services.GetRequiredService<IEventSubscriber>().ObserveEvent<InboundContactRequestReceivedEvent>().Where(e => e.OwnerIdentityId == target.PublicIdentityId).FirstAsync(cancellationToken: ct)`.
  * Create: `Percolator.Simulator.Tests/Topology/PersonaCardViewModelTests.cs`
    * Verify that publishing `InboundContactRequestReceivedEvent` on the host event bus sets `HasPendingHandshake = true` and updates `PendingHandshakeText`.
    * Verify that publishing `ContactRequestApprovedEvent` clears `HasPendingHandshake = false` and empties `PendingHandshakeText`.
* **CQRS Query & Anti-Shortcut Boundaries**:
  * **Mandatory CQRS Read Port**: UI components (`InboundRequestStateService` and `PersonaCardViewModel`) MUST query pending requests strictly through `IPeerContactQueryService` (`[VM-006]`). Direct injection or invocation of write-side repositories (`IPeerContactRepository`, `IPendingHandshakeRepository`) in UI/ViewModel layers is prohibited.
  * **Non-Nullable Required Dependencies**: All injected services (`IEventPublisher`, `IEventSubscriber`, `IPeerContactQueryService`) must be non-nullable, injected via constructor, and guarded with `ArgumentNullException.ThrowIfNull` (Anti-Shortcut Rule 1).
  * **Strict Hexagonal Event Translation**: Domain emits `IDomainEvent`; Application maps via source-generated `[MapFromDomainEvent]` to `IApplicationEvent` records; Application publishes via `IEventPublisher`; UI subscribes via `IEventSubscriber`. The domain layer MUST NOT reference application pub/sub, and the UI layer MUST NOT subscribe directly to domain events.
  * **Deterministic Subscription Teardown**: All event bus subscriptions must be explicitly attached to `_bag` (`DisposableBag`) or `Disposables` (`CompositeDisposable`) to eliminate memory leaks on persona deactivation/disposal.
* **Acceptance Criteria**:
  * Direct and relayed inbound handshake envelopes processed by `HandshakeService.ReceiveInvitationAsync` persist to `IPendingHandshakeRepository` and `IPeerContactRepository`, raising `InboundContactRequestReceivedDomainEvent` and publishing `InboundContactRequestReceivedEvent`.
  * Desktop `InboundRequestStateService` reacts dynamically to `InboundContactRequestReceivedEvent` for the active persona, updating `PendingCount` and `Requests` collection without manual reload or persona toggle.
  * Simulator `PersonaCardViewModel` reacts directly to `InboundContactRequestReceivedEvent` from the host's event bus, displaying the pending handshake notification banner and Accept/Reject buttons in real time.
  * Handshake completion/approval removes the pending request and clears the notification banner across both Desktop and Simulator via `ContactRequestApprovedEvent`.
  * `SimulatorNotifyingHandshakeDecorator` is deleted; no side-channel simulator state bus signals are used for handshake notification.
* **Verification Gate**:
  * `dotnet test Percolator.Domain.Tests/Percolator.Domain.Tests.csproj --filter "FullyQualifiedName~PeerContact"`
  * `dotnet test Percolator.Application.Tests/Percolator.Application.Tests.csproj --filter "FullyQualifiedName~ContactRequestCoordinator|FullyQualifiedName~HandshakeService"`
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~InboundRequestStateService"`
  * `dotnet test Percolator.Simulator.Tests/Percolator.Simulator.Tests.csproj --filter "FullyQualifiedName~PersonaCardViewModel"`

---

### [DESK-08] Right-Anchored Profile & Connection Inspector Drawer
* **QMS Traceability**: `DEP-DESKTOP-OUT-OF-BAND-VERIFICATION` (allocates `SYS-OUT-OF-BAND-SECURITY-CODE`), `DEP-DESKTOP-KEY-CHANGE-WARNING` (allocates `SYS-KEY-CHANGE-TRUST-HALT`), `DEP-DESKTOP-PEER-BLOCK-UI-SILENCE` (allocates `SYS-PEER-BLOCK-SILENT-DROP`)
* **Detailed Design**: `INV-DESKTOP-PRESENTATION-MODELS`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-003]`, `[CB-001]`, `[CB-004]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-010]`, `[VM-014]`, `[R3-002]`, `[R3-004]`, `[R3-005]`
* **Target Deliverables**:
  * Create: `Features/Inspector/Models/PeerProfileInspectorModel.cs`, `RelayInspectorModel.cs`
  * Create: `Features/Inspector/Views/ProfileInspectorView.axaml` & `Features/Inspector/Views/ProfileInspectorView.axaml.cs`
  * Create: `Features/Inspector/ViewModels/ProfileInspectorViewModel.cs`
  * Modify: `Features/Shell/Views/MainWindow.axaml` (bind inspector `SplitView.Pane` to `ProfileInspectorView`)
* **Acceptance Criteria**:
  * $300\text{ px}$ slide-over inspector drawer toggled via chat header, context menu, or keyboard shortcut (`Ctrl+I`).
  * Direct Peer Inspector Variant:
    * Profile summary: large avatar/identicon, remote display name, copyable truncated Identity ID.
    * Disappearing Messages timer selector (*Off, 30s, 5m, 1h, 1d, 1w, 4w*).
    * Notification mute toggle presets (*1h, 8h, 1d, 7d, Always*).
    * Contextual Safety Numbers panel: Hidden by default; visible only when handshake is unverified or an Identity Key Change Alert is triggered; displays 60-digit fingerprint with *"Mark as Verified"* toggle.
    * Route Telemetry: Clean route pill (Direct LAN, STUN, or Relayed via Relay Name) and live roundtrip ping.
    * Contact Actions: *"Clear Chat History"* and *"Block Contact"* (`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`).
  * Relay Inspector Variant: Relay URI, connection status, latency, hosted mailboxes, and member roster.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Inspector"`
  * Verifies safety numbers hidden by default and displayed on key change; route telemetry reflects direct vs relayed path; blocking contact destroys session and removes conversation.

---

### [DESK-09] Sovereign Peer Discovery & Swarm Telemetry (Apps.Discovery)
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE` (Desktop presentation layer for discovery telemetry), `DEP-DESKTOP-PRE-ACTIVATION-STEALTH`
* **Detailed Design**: `INV-DESKTOP-THREAD-DISCIPLINE`, `INV-DESKTOP-PRESENTATION-MODELS`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-003]`, `[CB-001]`, `[CB-003]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[R3-002]`, `[R3-004]`, `[R3-005]`, `[R3-007]`, `[R3-010]`
* **Target Deliverables**:
  * Create: `Features/Discovery/Models/DiscoveredPeerModel.cs`, `BlindedRendezvousModel.cs`, `SwarmTelemetryModel.cs`
  * Create: `Features/Discovery/Services/IDiscoveryStateService.cs` & `DiscoveryStateService.cs`
  * Create: `Features/Discovery/Views/DiscoveryView.axaml` & `Features/Discovery/Views/DiscoveryView.axaml.cs`
  * Create: `Features/Discovery/ViewModels/DiscoveryViewModel.cs`, `DiscoveredPeerItemViewModel.cs`
* **Acceptance Criteria**:
  * `DiscoveryStateService`: Bridges `INearbyPeerDiscoveryWorkflow`, `IPrivateRendezvousService`, and `IRelayPeerDirectory`. Samples high-frequency discovery candidate bursts via `.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)` to guarantee 60fps UI performance without thread-pool starvation.
  * `DiscoveryView`: Candidate peer cards with public key preview, NAT traversal status badge (DirectPublic, StunTraversed, RelayedOnly), roundtrip latency, and one-click *"Connect"* initiating X3DH handshake. Blinded Rendezvous 24-hr UTC epoch countdown timer and matched contacts count.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Discovery"`
  * Verifies high-frequency packet sampling; clicking Connect triggers handshake; epoch timer updates reactively.

---

### [DESK-10] Swarm File Transfer Dashboard & In-Chat PieceMap Canvas (Apps.FileTransfer)
* **QMS Traceability**: `DEP-DESKTOP-MEDIA-METADATA-SANITIZATION-INGRESS` (allocates `SYS-MEDIA-METADATA-SANITIZATION-INGRESS`)
* **Detailed Design**: `DEC-DIRECT-CANVAS-BITFIELD`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[STYLE-003]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-006]`, `[CB-001]`, `[CB-006]`, `[CB-008]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[R3-002]`, `[R3-004]`, `[R3-005]`, `[R3-007]`, `[R3-012]`
* **Target Deliverables**:
  * Create: `Features/FileTransfer/Models/FileTransferTaskModel.cs`, `TransferBitfieldSnapshot.cs`
  * Create: `Features/FileTransfer/Services/IFileTransferStateService.cs` & `FileTransferStateService.cs`
  * Create: `Features/FileTransfer/Controls/PieceMapControl.axaml` & `PieceMapControl.axaml.cs` (direct `DrawingContext` bitfield rendering)
  * Create: `Features/FileTransfer/Views/FileTransferView.axaml` & `Features/FileTransfer/Views/FileTransferView.axaml.cs`
  * Create: `Features/FileTransfer/ViewModels/FileTransferViewModel.cs`, `TransferTaskItemViewModel.cs`
* **Acceptance Criteria**:
  * `PieceMapControl`: Direct graphical rendering of piece bitfields onto an Avalonia `DrawingContext` (zero individual visual element allocations), rendering 10,000+ pieces with $< 1\text{ ms}$ layout pass. Visual states: Missing (surface), Downloaded/Verified (accent), In-Flight (amber pulse). Reusable in full dashboard and in-chat transfer cards.
  * `FileTransferStateService`: Bridges `IFileTransferQueryService` and `DiskChunkAssembler`. Exposes active and completed transfers, sampled every $50\text{ ms}$.
  * File storage containment: Zero-byte allocation upon transfer start, `.percolator-part` staging, and atomic rename upon verification completion. Enforces Win32 reserved name and Unicode Form C path sanitization.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~FileTransfer"`
  * Verifies bitfield canvas rendering; drag-and-drop file staging; atomic rename on completion.

---

### [DESK-11] Timeline Cards & PluginSdk Integration (PluginSdk)
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE` (Desktop presentation layer for plugin sinks)
* **Detailed Design**: `INV-DESKTOP-PRESENTATION-MODELS`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-003]`, `[CB-001]`, `[CB-003]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-004]`, `[VM-005]`, `[VM-006]`, `[VM-010]`, `[R3-002]`, `[R3-004]`, `[R3-005]`, `[R3-013]`
* **Target Deliverables**:
  * Create: `Features/Timeline/Models/TimelineCardModel.cs`, `TimelineActionModel.cs`
  * Create: `Features/Timeline/Services/ITimelineCardStateService.cs` & `TimelineCardStateService.cs`
  * Create: `Features/Timeline/Services/DesktopTimelinePostSink.cs` (implements `ITimelinePostSink`)
  * Create: `Features/Timeline/Services/DesktopPluginActionHandler.cs` (implements `IPluginActionHandler`)
  * Create: `Features/Timeline/Views/TimelineView.axaml` & `Features/Timeline/Views/TimelineView.axaml.cs`
  * Create: `Features/Timeline/ViewModels/TimelineViewModel.cs`, `TimelineCardViewModel.cs`
* **Acceptance Criteria**:
  * `TimelineCardStateService`: Concurrency-guarded state service managing interactive plugin cards.
  * Adapters for `PluginSdk`: `PublishCardAsync`, `UpdateCardStatusAsync`, `RemoveCardAsync`, and action click dispatching to target plugins.
  * `TimelineView`: Interactive card feed with metadata chips, dismiss gestures, and action buttons.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~Timeline"`
  * Verifies card publishing, reactive status updates, and action dispatching.

---

### [DESK-12] Desktop System Integration, Native Notifications & Quick-Switcher (Ctrl+K)
* **QMS Traceability**: `DEP-DESKTOP-SESSION-LOCK-ZEROIZATION` (allocates `SYS-SESSION-LOCK-ZEROIZATION`), `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (allocates `SYS-CONTACT-CONSENT-GATE`)
* **Detailed Design**: `DEC-PULL-BASED-CONSENT-STAGING`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`
* **Governing Specs**: `[STYLE-002]`, `[STYLE-005]`, `[STYLE-006]`, `[VIEW-001]`, `[VIEW-004]`, `[VIEW-009]`, `[CB-001]`, `[CB-004]`, `[CB-008]`, `[CB-009]`, `[VM-001]`, `[VM-002]`, `[VM-003]`, `[VM-005]`, `[VM-010]`, `[R3-004]`, `[R3-006]`
* **Target Deliverables**:
  * Create: `Features/Shell/Models/QuickSwitcherItemModel.cs`
  * Create: `Features/Shell/Views/QuickSwitcherDialog.axaml` & `Features/Shell/Views/QuickSwitcherDialog.axaml.cs`
  * Create: `Features/Shell/ViewModels/QuickSwitcherViewModel.cs`
  * Modify: `App.axaml` (system tray `TrayIcon` configuration)
  * Create: `Common/Services/PlatformNotificationBridge.cs` (`IPlatformNotificationBridge`)
* **Acceptance Criteria**:
  * Global Quick-Switcher (`Ctrl+K` / `Ctrl+P`): Centered modal Command Palette with fuzzy searching across relays, direct P2P chats, relayed channels, and quick commands (*"Add Connection"*, *"Lock Session"*). Selecting an item navigates immediately via `ShellNavigationStateService`.
  * System Tray Integration: Menu with *"Open Percolator"*, *"Lock Session (Stealth)"*, and *"Exit"*. Optional minimize-to-tray on window close.
  * Native Notifications: Cross-platform toasts (WinRT on Windows, D-Bus on Linux, Notification Center on macOS) and in-app banners. Strictly enforces inbound handshake silence: only messages in established chats fire toasts.
  * Window Geometry: Restores window bounds, position, and maximized state across sessions via `IShellWindowGeometryStore`.
* **Verification Gate**:
  * Pressing `Ctrl+K` opens Quick-Switcher; selecting item navigates shell; closing with tray active minimizes silently; inbound chat triggers notification while unsolicited handshake remains silent.

---

### [DESK-13] Headless UI & Comprehensive Verification Test Suite
* **QMS Traceability**: Software Item Verification (IEC 62304 Clause 5.7 / FDA 21 CFR 820.30(f))
* **Detailed Design**: Verifies `INV-DESKTOP-TWO-STAGE-HANDOFF`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`, `INV-DESKTOP-PRESENTATION-MODELS`, `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-THREAD-DISCIPLINE`, `DEC-DEBOUNCED-DROP-RELOAD`, `DEC-LRU-SESSION-SCOPES`, `DEC-DIRECT-CANVAS-BITFIELD`
* **Governing Specs**: `[VM-008]`, `[VM-010]`, `[VM-011]`, `[VM-012]`, `[VM-019]`, `[R3-009]`, `[CB-010]`
* **Target Deliverables**:
  * Enhance: `Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj`
  * Create: Comprehensive test suites across `VaultGateTests.cs`, `WindowHandoffTests.cs`, `RailNavigationTests.cs`, `ConversationStateTests.cs`, `OutboundRetryTests.cs`, `PieceMapControlTests.cs`.
* **Acceptance Criteria**:
  * 100% automated test verification without `Task.Delay` or brittle interaction mocking (`[VM-010]`, `[VM-011]`).
  * End-to-end verification of Flows A, B, and C across in-memory fixtures.
* **Verification Gate**:
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj` executes cleanly in $< 5\text{ seconds}$ with 100% passing tests and zero warnings.

---

## 5. Port-to-Adapter Realization Matrix

This matrix maps all infrastructure ports and desktop adapters across their owning chunks, ensuring zero architectural disconnects between domain, infrastructure, plugins, and UI.

| Interface / Port | Layer / Source | Concrete Realization / Adapter | Chunk |
| :--- | :--- | :--- | :--- |
| `IDatabaseUnlockCoordinator` | Application Ports | `SqlCipherDatabaseUnlockCoordinator` (Infrastructure) | Chunk 2 (`[DESK-02]`) |
| `IAuthenticatedSession` | Application Ports | `AuthenticatedSession` (Infrastructure) | Chunk 4 (`[DESK-04]`) |
| `IWindowHandoffCoordinator` | Desktop Windowing | `WindowHandoffCoordinator` | Chunk 4 (`[DESK-04]`) |
| `IShellNavigationStateService` | Desktop Navigation | `ShellNavigationStateService` | Chunk 5 (`[DESK-05]`) |
| `IRelayStateService` | Desktop Transports | `RelayStateService` | Chunk 5 (`[DESK-05]`) |
| `IInboundRequestStateService` | Desktop Transports | `InboundRequestStateService` | Chunk 5 (`[DESK-05]`) |
| `IConversationStateService` | Desktop Chats | `ConversationStateService` | Chunk 6 (`[DESK-06]`) |
| `IOutboundPendingRetryWorker` | Desktop Transports | `OutboundPendingRetryWorker` | Chunk 6 (`[DESK-06]`) |
| `IPeerContactQueryService` | Application Delivery | `SqlPeerContactQueryService` (Infrastructure) | Chunk 6 (`[DESK-06]`) |
| `IPendingHandshakeRepository`| Domain Delivery | `SqlitePendingHandshakeRepository` | Chunk 6 (`[DESK-06]`) |
| `IContactRequestCoordinator` | Application Profiles | `ContactRequestCoordinator` | Chunk 7A (`[DESK-07-A]`) |
| `IEventPublisher` | Application Ports | `InMemoryEventBus` (Infrastructure) | Chunk 7A (`[DESK-07-A]`) |
| `IEventSubscriber` | Application Ports | `InMemoryEventBus` (Infrastructure) | Chunk 7A (`[DESK-07-A]`) |
| `IChatDraftStateService` | Desktop Chats | `ChatDraftStateService` | Chunk 7 (`[DESK-07]`) |
| `IChatService` | Apps Chat | `ChatService` | Chunk 7 (`[DESK-07]`) |
| `IChatMessageQueryService` | Apps Chat | `SqlChatMessageQueryService` (Infrastructure) | Chunk 7 (`[DESK-07]`) |
| `IConversationListQueryService`| Apps Chat | `SqlConversationListQueryService` (Infrastructure)| Chunk 7 (`[DESK-07]`) |
| `IDiscoveryStateService` | Desktop Discovery | `DiscoveryStateService` | Chunk 9 (`[DESK-09]`) |
| `INearbyPeerDiscoveryWorkflow` | Apps Discovery | `NearbyPeerDiscoveryWorkflow` | Chunk 9 (`[DESK-09]`) |
| `IPrivateRendezvousService` | Apps Discovery | `PrivateRendezvousService` | Chunk 9 (`[DESK-09]`) |
| `IRelayPeerDirectory` | Apps Discovery | `SqliteRelayPeerDirectory` (Infrastructure) | Chunk 9 (`[DESK-09]`) |
| `IFileTransferStateService` | Desktop FileTransfer| `FileTransferStateService` | Chunk 10 (`[DESK-10]`) |
| `IFileTransferQueryService` | Apps FileTransfer | `FileTransferQueryService` | Chunk 10 (`[DESK-10]`) |
| `ITransferSessionRepository` | Apps FileTransfer | `SqliteTransferSessionRepository` (Infrastructure)| Chunk 10 (`[DESK-10]`) |
| `IDesktopFileStorage` | Apps FileTransfer | `PortableDesktopFileStore` (Infrastructure) | Chunk 10 (`[DESK-10]`) |
| `ITimelineCardStateService` | Desktop Timeline | `TimelineCardStateService` | Chunk 11 (`[DESK-11]`) |
| `ITimelinePostSink` | PluginSdk | `DesktopTimelinePostSink` | Chunk 11 (`[DESK-11]`) |
| `IPluginActionHandler` | PluginSdk | `DesktopPluginActionHandlerDispatcher` | Chunk 11 (`[DESK-11]`) |
| `IPlatformNotificationBridge` | Desktop Common | `PlatformNotificationBridge` | Chunk 12 (`[DESK-12]`) |
| `IShellWindowGeometryStore` | Desktop Windowing | `VaultShellWindowGeometryStore` (Stage-2) | Chunk 12 (`[DESK-12]`) |
| `IDateTimeProvider` | Domain Common | `SystemDateTimeProvider` | Chunk 1 (`[DESK-01]`) |

---

## 6. Developer Tooling & Simulation Interoperability

Testing multi-peer network topologies, relays, and handshakes is handled by the standalone companion project **`Percolator.Simulator`** (see [`Percolator.Simulator/implementation-plan.md`](../Percolator.Simulator/implementation-plan.md)).

`Percolator.Desktop` contains **zero simulator code** or mock interceptors. To interoperate with the simulator during development, `Percolator.Desktop` supports a development configuration flag:
* `Development:AllowLocalhostPeers: true` (or `--allow-localhost` CLI argument).
* When set, loopback `127.0.0.1` targets are permitted as valid peer endpoints without triggering production WAN stealth security rejections.
