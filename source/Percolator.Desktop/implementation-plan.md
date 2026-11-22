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
   * **Canary Verification & Post-Unlock Schema Initialization (`DEC-CANARY-ERROR26-UNWRAP`)**:
     * Derives a 256-bit candidate key using Argon2id ($m=64\text{ MB}$, $t=3\text{ iterations}$, $p=4\text{ lanes}$, `DEC-ARGON2ID-PARAMETERS`) and evaluates AEAD slots.
     * Validates passphrase with an isolated canary query `SELECT count(*) FROM sqlite_master;`, explicitly trapping native error code `26` (`SQLITE_NOTADB`).
     * On success, `IDatabaseMigrator` applies the current EF Core model DDL cleanly via `GenerateCreateScript()`. During active `DESK-07-*` development, **no EF Core migrations or ad-hoc `ALTER TABLE` upgrade scripts are used**—modify entities and `PercolatorDbContext.OnModelCreating` directly and delete local `.db` files when schema changes.
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
   * **Ban `GlobalUsings.cs` Catch-All Shims**: Adding `global using` directives (`GlobalUsings.cs`) to mask missing `using` statements after moving/renaming types is strictly prohibited. Large symbol/namespace moves must be scoped as an isolated ticket (or isolated initial step) followed by a `dotnet build Percolator.sln` compiler-error fix loop updating explicit `using` directives across every source and test project.
   * **Compiler-Driven Refactoring & Deferred Whole-Solution Verification**: Deleting or relocating legacy symbols to expose all call sites via compiler errors is a valid refactoring workflow; full solution-wide test execution and simulator replay verification sit at the phase gate (`[DESK-07-F]`) rather than requiring every intermediate step to pass all cross-cutting suites.
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
        C6["[DESK-06] Direct P2P & Relayed Conversation Directory with Outbound Pending (Flows B & C Phase 2) (Completed)"]
        C7["[DESK-07] Real-Time Messaging Experience, Volatile Drafts & Disappearing Messages (Apps.Chat) (Completed)"]
        C7A["[DESK-07-A] Event Bus Promotion, SourceGen Fix, Outbox/Direct-Ingress Parity & CQRS Read Model (Completed)"]
        C7B["[DESK-07-B] Inbound Handshake Consent, Inbound DirectChannel Creation & Decorator Removal (Completed)"]
        C7C["[DESK-07-C] Bidirectional Handshake Finalization, Return-Route Carrying & Unified Outbound Workflow (Completed)"]
        C7D["[DESK-07-D] Shared Group Creation, Relay ACK, Invitation Lifecycle & Live Group Stream Worker"]
        C7E["[DESK-07-E] Real-Time Chat/Profile Event Pipeline, Live Desktop Transcript & Simulator De-Duplication"]
        C7F["[DESK-07-F] Remaining Simulator Business-Logic Cleanup & Cross-Peer E2E Verification"]
        C8["[DESK-08] Right-Anchored Profile & Connection Inspector Drawer"]
    end

    subgraph Phase 3: P2P Swarm & Platform Services
        C9["[DESK-09] Sovereign Discovery & Swarm Telemetry (Apps.Discovery)"]
        C10["[DESK-10] Swarm File Transfer Dashboard & In-Chat PieceMap Canvas (Apps.FileTransfer)"]
        C11["[DESK-11] Timeline Cards & PluginSdk Integration (PluginSdk)"]
        C12["[DESK-12] Desktop System Integration, Native Notifications & Quick-Switcher (Ctrl+K)"]
        C13["[DESK-13] Headless UI & Comprehensive Verification Test Suite"]
    end

    C1 --> C2 --> C3 --> C4 --> C5 --> C6 --> C7 --> C7A --> C7B --> C7C --> C7D --> C7E --> C7F --> C8 --> C9 --> C10 --> C11 --> C12 --> C13
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

### [x] [DESK-07-A] Event Bus Promotion, SourceGen Fix, Outbox/Direct-Ingress Parity & CQRS Read Model
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE`, `DEP-DESKTOP-CRYPTO-BOUNDARY` (allocates `SYS-CRYPTO-BOUNDARY-ISOLATION`), `DEP-DESKTOP-DIRECT-TRANSPORT-INGRESS`
* **Detailed Design**: `INV-APP-EVENT-BUS-DECOUPLING`, `INV-RECORD-EVENT-ENFORCEMENT`, `DEC-SQLITE-WAL-PRAGMAS`, `INV-DESKTOP-STATEFUL-PROJECTIONS`
* **Governing Specs**: `[UISS-001]` to `[UISS-006]`, `[VM-003]`, `[VM-006]`, `anti-shortcut-discipline` Rules 1, 3, 8, 9, 11, 15, 16
* **Execution Policy**: Perform Deliverable 1 first as an isolated build-and-fix loop (`dotnet build Percolator.sln`) updating explicit `using` statements across all source and test projects (**zero `GlobalUsings.cs` shims**). Whole-solution test execution & simulator replay wait until `[DESK-07-F]`; intermediate tickets verify only their scoped test targets. Assume clean local `.db` deletion—zero EF Core migrations.
* **Target Deliverables**:
  * **1. Promote Event Bus Ports to `Percolator.PluginSdk` (Isolated First-Step Build-Fix Loop)**:
    * Move & re-namespace to `Percolator.PluginSdk.Events`:
      * `Percolator.PluginSdk/Events/IApplicationEvent.cs` (`Guid EventId { get; }`, `DateTimeOffset OccurredOnUtc { get; }`)
      * `Percolator.PluginSdk/Events/IEventPublisher.cs` (`ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IApplicationEvent;`)
      * `Percolator.PluginSdk/Events/IEventSubscriber.cs` (`IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : IApplicationEvent;`, `IDisposable RegisterFaultHandler(Action<EventDispatchError> handler);`)
      * `Percolator.PluginSdk/Events/IEventBus.cs` (`IEventBus : IEventPublisher, IEventSubscriber`)
      * `Percolator.PluginSdk/Events/EventDispatchError.cs`
    * Delete old copies from `Percolator.Application/Ports/` and **prohibit `GlobalUsings.cs` catch-alls** (`Percolator.Application/GlobalUsings.cs`, `Percolator.Infrastructure/GlobalUsings.cs`, `Percolator.Desktop.Tests/GlobalUsings.cs` must NOT contain `global using Percolator.PluginSdk.Events;`—delete empty/added `GlobalUsings.cs` files).
    * Run `dotnet build Percolator.sln` in a loop and explicitly update `using Percolator.Application.Ports;` → `using Percolator.PluginSdk.Events;` across every affected file in `Percolator.Application`, `Percolator.Infrastructure`, `Percolator.Desktop`, `Percolator.Simulator`, and all `*.Tests` projects (including `Percolator.Application.Tests/Delivery/DrainSchedulerTests.cs` and `ActiveIdentitySetTests.cs`).
    * Modify: `Percolator.Apps.Chat/Percolator.Apps.Chat.csproj` — add Analyzer reference:
      ```xml
      <ProjectReference Include="..\Percolator.SourceGenerators\Percolator.SourceGenerators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
      ```
  * **2. Fix `Percolator.SourceGenerators/DomainEventMapperGenerator.cs` Compile-Breaking Namespace Bug**:
    * Lines 333 and 337 currently emit stale `Percolator.Application2.Ports`.
    * Change emitted namespace to `Percolator.PluginSdk.Events` and return type to `global::Percolator.PluginSdk.Events.IApplicationEvent?`.
    * Update existing test strings in `Percolator.SourceGenerators.Tests/DomainEventMapperGeneratorTests.cs` AND **add a new dedicated unit test** asserting that when a partial record explicitly declares strongly-typed `[GuidId]` Value Object properties matching domain event properties, generated `FromDomain(...)` preserves those Value Objects without unboxing them to `Guid`.
  * **3. Create Shared Reactive Event Bus Helper for Simulator**:
    * Create: `Percolator.Simulator/Features/Shell/SimulatorEventBusExtensions.cs` implementing `public static Observable<TEvent> ObserveEvent<TEvent>(this IEventSubscriber subscriber) where TEvent : IApplicationEvent` using `Observable.Create<TEvent>` + `subscriber.Subscribe<TEvent>((evt, _) => { observer.OnNext(evt); return ValueTask.CompletedTask; })` (mirrors `Percolator.Desktop/Common/EventBusExtensions.cs`).
  * **4. Centralize Deterministic Direct Channel ID in Domain**:
    * Modify: `Percolator.Domain/Channels/ValueObjects/ChannelId.cs` — add static factory:
      ```csharp
      public static ChannelId DeterministicForDirect(PublicIdentityId a, PublicIdentityId b)
      ```
      using sorted 16-byte GUID comparison + `SHA256.HashData` first 16 bytes (`ChannelId.FromBytes(hash[..16])`).
    * Modify: `Percolator.Application/Services/PeerConnectionService.cs` — delete private `DeriveDirectChannelId`, call `ChannelId.DeterministicForDirect(_localIdentityId, targetPeerId)`.
    * Modify: `Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs` & `ScenarioRunnerService.cs` — delete `internal static class ChannelIdUtility`; replace all Simulator call sites with `ChannelId.DeterministicForDirect(...)`.
  * **5. Production `OutboxDispatchHost`, Atomic Outbox Claim & Complete Removal of Manual `FlushOutboxAsync`**:
    * Modify: `Percolator.Application/Delivery/Events/OutboxJobEnqueuedEvent.cs` — implement **strictly** `IApplicationEvent` (add `public Guid EventId { get; } = Guid.NewGuid();`, remove `IDomainEvent`—operational outbox wakeups are not domain events).
    * **Keep Persistence Pure (Zero `IEventPublisher` in `SqliteOutboxRepository`)**:
      * `SqliteOutboxRepository` MUST NOT inject `IEventPublisher` or publish events inside `SaveAsync`.
      * Inject non-nullable `IEventPublisher` into `Percolator.Application/Egress/OutboundEgressPipeline.cs` and `Percolator.Application/Handshake/HandshakeService.cs` (the only two Application enqueue points for `OutboxJob.Create`):
        * In `OutboundEgressPipeline.SendPayloadAsync`: publish `OutboxJobEnqueuedEvent` **only after Phase 5 completes if `job.Status == OutboxStatus.Pending`** (prevents racing Phase 5 stream write against `OutboxDispatchHost`).
        * In `HandshakeService.InitiateHandshakeAsync`: publish `OutboxJobEnqueuedEvent` immediately after `_outboxRepo.SaveAsync(job, ct)`.
    * **Atomic Outbox Claim (Prevent Double-Delivery Races)**:
      * Add `Task<IReadOnlyList<OutboxJob>> ClaimDuePendingJobsAsync(DateTimeOffset asOfUtc, int limit, CancellationToken ct = default)` to `IOutboxRepository` (and implement in `SqliteOutboxRepository` inside a SQLite transaction updating selected `Pending` rows to `InFlight` atomically before returning them). Update `OutboxWorker.ProcessBatchAsync` to call `ClaimDuePendingJobsAsync` so concurrent poll/signal/shutdown overlaps can never dispatch the same `OutboxJob` twice.
    * Create: `Percolator.Infrastructure/Transport/Workers/OutboxDispatchHost.cs` (`sealed class OutboxDispatchHost : BackgroundService`):
      * Constructor: `(IServiceScopeFactory scopeFactory, IEventSubscriber eventSubscriber, OutboxDispatchHostOptions options, ILogger<OutboxDispatchHost> logger)` (all non-nullable).
      * Maintains a `Channel<bool>` (bounded capacity 1, `DropWrite`) signaled on `OutboxJobEnqueuedEvent` via `IEventSubscriber.Subscribe<OutboxJobEnqueuedEvent>(...)`.
      * Loop awaits either wake signal or `options.PollInterval` (default `TimeSpan.FromSeconds(1)`), opens an `IServiceScope`, resolves `OutboxWorker`, and loops `await worker.ProcessBatchAsync(options.BatchSize, stoppingToken)` while `processed > 0`.
      * Unit test `OutboxDispatchHostTests` MUST use deterministic synchronization (`TaskCompletionSource` / `SemaphoreSlim` + `PollInterval = Timeout.InfiniteTimeSpan`), **never `await Task.Delay(...)`**.
    * Modify: `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (`AddTransportInfrastructure`):
      * Register `services.AddScoped<OutboxWorker>();`, `services.AddSingleton<OutboxDispatchHostOptions>();`, `services.AddHostedService<OutboxDispatchHost>();`.
    * **Delete Manual `FlushOutboxAsync` & Simulator Outbox Decorator Entirely** (`OutboxDispatchHost` now owns all dispatch):
      * Delete: `Percolator.Simulator/Features/Topology/SimulatorOutboxFlushingIngressPipelineDecorator.cs`.
      * Delete `FlushOutboxAsync` from `ISimulatedPersonaHost` and `SimulatedPersonaHost` (including `_outboxGate` and direct `OutboxWorker.ProcessBatchAsync` calls inside `DrainMailboxAsync` and `IngestGroupBroadcastAsync`), remove `SimulatorOutboxFlushingIngressPipelineDecorator` and manual `AddScoped<OutboxWorker>()` from `SimulatedPersonaHost.cs`.
      * Remove all manual `FlushOutboxAsync(...)` calls across `DirectHandshakeOrchestrator.cs`, `DirectChatCoordinator.cs`, `GroupChatCoordinator.cs`, `RelayProvisioningCoordinator.cs`, `ProfileSyncCoordinator.cs`, `RichChatActionCoordinator.cs`, `VirtualClockStateService.cs`, `SwarmSimulationCoordinator.cs`, `PersonaConnectPeerFlyoutViewModel.cs`, and `PersonaConnectPeerFlyoutViewModelTests.cs`.
  * **6. Fix Multi-Persona Direct P2P Ingress in `UnauthenticatedDeliveryEndpoint`**:
    * Modify: `Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs`:
      * Replace optional `PublicIdentityId? localRecipientId = null` with non-nullable `IActiveIdentitySet activeIdentities` in the primary persona-host constructor (preserve `ForRelay(...)` factory for standalone relay hosts).
      * In `DeliverDirect(...)`: iterate `_activeIdentities.Current`; attempt `_unwrapper.UnwrapDirect(request.EncryptedPackage.Memory, candidateId)` until one succeeds. Return a uniform opaque error (`"Failed to unwrap direct delivery package."`) on total failure without leaking per-identity crypto error details over unauthenticated gRPC.
    * Modify: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` — delete line `services.AddScoped(typeof(PublicIdentityId?), _ => PublicIdentityId);`.
    * Update `UnauthenticatedDeliveryEndpointTests` **and** `WindowsPeerGrpcTofuRoundTripTests` to supply `IActiveIdentitySet`, and add a `DeliverDirect` multi-identity fallback test where `MockEnvelopeUnwrapper` keys off `recipientId` (first candidate fails, second succeeds).
  * **7. Upgrade Channel Schema (No EF Migration) & CQRS `SqlConversationListQueryService` / `ConversationStateService`**:
    * Create: `Percolator.Domain/Channels/Model/DirectChannelStatus.cs`: `public enum DirectChannelStatus { Active = 0, PendingAcceptance = 1 }`.
    * Modify: `Percolator.Domain/Channels/Model/DirectChannel.cs` — add `public DirectChannelStatus Status { get; private set; }` and `public RelayEndpoint? RelayEndpoint { get; private set; }` (require explicit `DirectChannelStatus` in `Create` or default explicitly only where intentional, and keep `Rehydrate` backward-compatible).
    * Modify: `Percolator.Domain/Channels/Model/GroupChannel.cs` — add `public RelayEndpoint? RelayEndpoint { get; private set; }` (update `CreateGenesis` and `Rehydrate`).
    * Modify: `Percolator.Infrastructure/Persistence/Entities/ChannelEntities.cs` (`ChatChannelEntity`) & `PercolatorDbContext.OnModelCreating` — add `public int Status { get; set; }` (default `0`) and `public string? RelayEndpoint { get; set; }`.
    * **No EF Core Migration & No Ad-Hoc `ALTER TABLE`**: Do NOT create EF migrations under `Migrations/` and delete any `ApplyChannelSchemaUpgradesAsync` band-aid in `SqliteDatabaseMigrator.cs`. Delete local `.db` files so `GenerateCreateScript()` / `CreateTablesAsync()` applies the clean model schema.
    * Modify: `Percolator.Infrastructure/Persistence/Repositories/SqliteChannelRepository.cs` — persist and rehydrate `Status` and `RelayEndpoint` (safely guarding malformed `RelayEndpoint` strings instead of throwing unhandled exceptions during rehydration).
    * Modify: `Percolator.Apps.Chat/Features/Messaging/ConversationSummaryReadModel.cs` — expand flat CQRS DTO:
      ```csharp
      public enum ConversationChannelKind { Direct = 0, Group = 1 }

      public sealed record ConversationSummaryReadModel(
          ChannelId ChannelId,
          ConversationChannelKind Kind,
          PublicIdentityId? RemotePeerId,
          string Title,
          string LastMessageSnippet,
          ulong LastActivityTimestampUnixMs,
          int UnreadCount,
          PublicIdentityId? LastAuthorId,
          bool IsPendingAcceptance,
          string? RelayEndpoint,
          PublicIdentityId? RelayIdentityId);
      ```
    * Modify: `Percolator.Infrastructure/Persistence/Repositories/SqlConversationListQueryService.cs`:
      * **REMOVE** `if (latest == null) { continue; }` so 0-message Direct and Group channels are returned cleanly.
      * **Eliminate N+1 queries**: batch-query latest messages, unread counts, and attachment existence across the channel ID set (never issue 1–3 queries per channel inside `foreach`).
      * **Remove unauthorized `ChatMessages` fallback**: delete the `if (channels.Count == 0)` query that looked up channels solely from `ChatMessages` bypassing ownership/membership checks.
      * Compute `LastActivityTimestampUnixMs` as `Math.Max(latest.TimestampUnixMs, (ulong)channel.LastActivityUtc.ToUnixTimeMilliseconds())` when messages exist, and `(ulong)channel.LastActivityUtc.ToUnixTimeMilliseconds()` for 0-message channels.
      * Normalize `RelayEndpoint` comparison using `RelayEndpoint.Parse(...)` instead of raw string equality when joining `RelayRegistrations` for `RelayIdentityId`.
    * Modify: `Percolator.Desktop/Features/Chats/Services/ConversationStateService.cs`:
      * **DELETE** the synthetic `ChannelId.New()` fallback loop from `IPeerContactQueryService` and delete the guessed fallback `conv.LastAuthorId ?? personaId` on line 138; map `conv.ChannelId`, `conv.RemotePeerId`, `conv.RelayIdentityId`, and `conv.IsPendingAcceptance` (`DeliveryStatus` / `IsPendingOutbound`) directly.
* **CQRS Query & Anti-Shortcut Boundaries**:
  * Zero `GlobalUsings.cs` catch-alls; zero EF Core migrations; zero `= null` defaults on required dependencies; zero `IEventPublisher` inside persistence repositories; zero `Task.Delay` synchronization in tests.
  * `SqlConversationListQueryService` remains Singleton using `IDbContextFactory<PercolatorDbContext>` (`AsNoTracking()`, zero write-repo usage).
  * `OutboxDispatchHost` is the sole outbox dispatcher; manual `FlushOutboxAsync` is completely removed.
* **Acceptance Criteria**:
  * All explicit `using Percolator.PluginSdk.Events;` directives compile cleanly across every project and test assembly (`Percolator.SourceGenerators`, `Percolator.Infrastructure`, `Percolator.Application.Tests`, `Percolator.Infrastructure.Windows.Tests`) without `GlobalUsings.cs`.
  * Enqueuing an `OutboxJob` in Desktop or Simulator triggers `OutboxDispatchHost` cleanly with atomic claiming and zero manual `FlushOutboxAsync`.
  * Direct P2P gRPC delivery (`DeliverDirect`) succeeds against a multi-identity host using `IActiveIdentitySet`.
  * `SqlConversationListQueryService.GetConversationsAsync` returns newly created 0-message Direct and Group channels without N+1 queries, and `ConversationStateService` creates zero synthetic `ChannelId.New()` entries.
* **Verification Gate (Scoped Smoke Tests — Full Solution Gate runs at `[DESK-07-F]`)**:
  * `dotnet build Percolator.sln --no-restore` (confirms zero broken `using`s or missing DI updates from the move/removals)
  * `dotnet test Percolator.SourceGenerators.Tests/Percolator.SourceGenerators.Tests.csproj`
  * `dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SqlConversationListQueryService|FullyQualifiedName~OutboxDispatchHost|FullyQualifiedName~UnauthenticatedDeliveryEndpoint"`
  * `dotnet test Percolator.Infrastructure.Windows.Tests/Percolator.Infrastructure.Windows.Tests.csproj --filter "FullyQualifiedName~WindowsPeerGrpcTofuRoundTrip"`
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~ConversationState"`

---

### [x] [DESK-07-B] Inbound Handshake Consent Pipeline, Inbound DirectChannel Creation & Decorator Removal (COMPLETED)
* **QMS Traceability**: `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (allocates `SYS-CONTACT-CONSENT-GATE`), `DEP-DESKTOP-PEER-BLOCK-UI-SILENCE` (allocates `SYS-PEER-BLOCK-SILENT-DROP`), `DEP-DESKTOP-CRYPTO-BOUNDARY`
* **Detailed Design**: `DEC-PULL-BASED-CONSENT-STAGING`, `INV-APP-EVENT-BUS-DECOUPLING`, `INV-RECORD-EVENT-ENFORCEMENT`, `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[UISS-001]` to `[UISS-005]`, `[VM-003]`, `[VM-006]`, `[R3-004]`, `[R3-005]`, `[R3-006]`, `anti-shortcut-discipline` Rules 1, 3, 6, 8, 10
* **Target Deliverables**:
  * **1. Domain Events & Aggregate Methods**:
    * Create: `Percolator.Domain/Identities/Events/InboundContactRequestReceivedDomainEvent.cs`
      ```csharp
      public sealed record InboundContactRequestReceivedDomainEvent(
          PublicIdentityId OwnerIdentityId,
          PublicIdentityId RemotePeerId,
          IdentityKey PrimaryPublicKey,
          DateTimeOffset OccurredOnUtc) : IDomainEvent
      {
          public Guid EventId { get; } = Guid.NewGuid();
      }
      ```
    * Create: `Percolator.Domain/Identities/Events/ContactRequestApprovedDomainEvent.cs`
      ```csharp
      public sealed record ContactRequestApprovedDomainEvent(
          PublicIdentityId OwnerIdentityId,
          PublicIdentityId RemotePeerId,
          PeerTrustLevel TrustLevel,
          DateTimeOffset OccurredOnUtc) : IDomainEvent
      {
          public Guid EventId { get; } = Guid.NewGuid();
      }
      ```
    * Create: `Percolator.Domain/Identities/Events/ContactRequestRejectedDomainEvent.cs`
      ```csharp
      public sealed record ContactRequestRejectedDomainEvent(
          PublicIdentityId OwnerIdentityId,
          PublicIdentityId RemotePeerId,
          bool IsBlocked,
          DateTimeOffset OccurredOnUtc) : IDomainEvent
      {
          public Guid EventId { get; } = Guid.NewGuid();
      }
      ```
    * Create: `Percolator.Domain/Channels/Events/DirectChannelCreatedDomainEvent.cs`
      ```csharp
      public sealed record DirectChannelCreatedDomainEvent(
          ChannelId ChannelId,
          PublicIdentityId OwnerIdentityId,
          PublicIdentityId RemotePeerId,
          DirectChannelStatus Status,
          RelayEndpoint? RelayEndpoint,
          DateTimeOffset OccurredOnUtc) : IDomainEvent
      {
          public Guid EventId { get; } = Guid.NewGuid();
      }
      ```
    * Modify: `Percolator.Domain/Identities/Model/PeerContact.cs`:
      * `CreateInboundRequest(...)`: emit `InboundContactRequestReceivedDomainEvent` before returning `Success`.
      * `ReopenInboundRequest(IdentityKey primaryPublicKey, string? proposedNickname, IDateTimeProvider timeProvider)`: if `TrustLevel == PeerTrustLevel.Blocked` fail with `CONTACT_BLOCKED`; if `State == ContactState.Rejected`, transition `State = ContactState.PendingApproval`, update `PrimaryPublicKey` & `Nickname`, emit `InboundContactRequestReceivedDomainEvent`. (Eliminates Simulator's raw `PercolatorDbContext.PeerContacts.Remove` hack.)
      * `Approve(PeerTrustLevel initialTrust, IDateTimeProvider timeProvider)` (**non-nullable `IDateTimeProvider`**, zero default `null`): emit `ContactRequestApprovedDomainEvent` on transition to `ContactState.Active`.
      * `Reject(bool block, IDateTimeProvider timeProvider)` (**non-nullable `IDateTimeProvider`**, zero default `null`): emit `ContactRequestRejectedDomainEvent` on transition to `ContactState.Rejected`.
      * Update all existing call sites in `Percolator.Domain.Tests`, `Percolator.Application`, and `Percolator.Application.Tests` to pass `IDateTimeProvider`.
    * Modify: `Percolator.Domain/Channels/Model/DirectChannel.cs`:
      * `Create(ChannelId id, PublicIdentityId ownerIdentityId, PublicIdentityId remotePeerId, IDateTimeProvider timeProvider, DirectChannelStatus status = DirectChannelStatus.Active, RelayEndpoint? relayEndpoint = null)`: append `channel.AddDomainEvent(new DirectChannelCreatedDomainEvent(id, ownerIdentityId, remotePeerId, status, relayEndpoint, timeProvider.UtcNow));`.
  * **2. Source-Generated `[MapFromDomainEvent]` Application Events**:
    * Create in `Percolator.Application/Profiles/Events/`:
      * `InboundContactRequestReceivedEvent.cs` (`[MapFromDomainEvent(typeof(InboundContactRequestReceivedDomainEvent))]`, explicit `PublicIdentityId OwnerIdentityId`, `PublicIdentityId RemotePeerId`, `IdentityKey PrimaryPublicKey` properties to preserve VO types).
      * `ContactRequestApprovedEvent.cs` (`[MapFromDomainEvent(typeof(ContactRequestApprovedDomainEvent))]`, explicit `PublicIdentityId OwnerIdentityId`, `PublicIdentityId RemotePeerId`, `PeerTrustLevel TrustLevel`).
      * `ContactRequestRejectedEvent.cs` (`[MapFromDomainEvent(typeof(ContactRequestRejectedDomainEvent))]`, explicit `PublicIdentityId OwnerIdentityId`, `PublicIdentityId RemotePeerId`, `bool IsBlocked`).
    * Create in `Percolator.Application/Channels/Events/`:
      * `DirectChannelCreatedEvent.cs` (`[MapFromDomainEvent(typeof(DirectChannelCreatedDomainEvent))]`, explicit `ChannelId ChannelId`, `PublicIdentityId OwnerIdentityId`, `PublicIdentityId RemotePeerId`, `DirectChannelStatus Status`, `RelayEndpoint? RelayEndpoint`).
  * **3. Application Layer Workflows**:
    * Modify: `Percolator.Application/Profiles/ContactRequestCoordinator.cs`:
      * Constructor: add non-nullable `IEventPublisher eventPublisher` (`ArgumentNullException.ThrowIfNull`).
      * `HandleInboundRequestAsync`: if `existing` is in `ContactState.Rejected` (and not blocked), call `existing.ReopenInboundRequest(peerPublicKey, proposedNickname, _timeProvider)`, save, and publish `InboundContactRequestReceivedEvent`. For newly created contacts, save then publish `InboundContactRequestReceivedEvent` and call `contact.ClearDomainEvents()`. Existing `PendingApproval` contacts return early (idempotent).
      * `ApproveRequestAsync` / `RejectRequestAsync`: pass `_timeProvider` to `contact.Approve`/`contact.Reject`; after `_contactRepo.SaveAsync(contact, ct)`, map & publish domain events via `_eventPublisher.PublishAsync(...)`, then `contact.ClearDomainEvents()`.
    * Modify: `Percolator.Application/Connections/InboundConnectionWorkflow.cs`:
      * Constructor: add non-nullable `IChannelRepository channelRepo`, `IPeerMailboxRoutingRepository routingRepo`, `IDateTimeProvider timeProvider`, `IEventPublisher eventPublisher`.
      * `AcceptRequestAsync(personaId, requesterId, ct)`:
        1. Call `_contactCoordinator.ApproveRequestAsync(personaId, requesterId, PeerTrustLevel.Tofu, ct)`.
        2. Call `_handshakeService.CompletePendingHandshakeAsync(personaId, requesterId, ct)`.
        3. Ensure `DirectChannel` exists: `var channelId = ChannelId.DeterministicForDirect(personaId, requesterId);`. If `await _channelRepo.GetDirectByIdAsync(channelId, ct)` is `null`, resolve `RelayEndpoint?` from `await _routingRepo.GetByPeerIdAsync(requesterId, ct)` (if relayed), call `DirectChannel.Create(channelId, personaId, requesterId, _timeProvider, DirectChannelStatus.Active, relayEndpoint)`, call `await _channelRepo.SaveDirectAsync(channel, ct)`, map & publish `DirectChannelCreatedEvent`, and call `channel.ClearDomainEvents()`.
    * Update Affected Application & Integration Tests:
      * `Percolator.Application.Tests/Handshake/HandshakeServiceTests.cs`
      * `Percolator.Application.Tests/Ingress/AppHostPipelineTests.cs`
      * `Percolator.Application.Tests/Profiles/ProfileAndContactTests.cs`
      * `Percolator.ApplicationIntegrationTests/Builders/TestIdentityNode.cs`
  * **4. Desktop Reactive State Services**:
    * Modify: `Percolator.Desktop/Features/Transports/Services/InboundRequestStateService.cs`:
      * Constructor: add non-nullable `IEventSubscriber eventSubscriber`.
      * Subscribe using exact `Func<TEvent, CancellationToken, ValueTask>` delegate (anchored via `.AddTo(ref _bag)`):
        ```csharp
        _eventSubscriber.Subscribe<InboundContactRequestReceivedEvent>((evt, ct) =>
        {
            if (evt.OwnerIdentityId == _activePersonaId) _ = LoadRequestsForPersonaAsync(evt.OwnerIdentityId);
            return ValueTask.CompletedTask;
        }).AddTo(ref _bag);
        ```
        Repeat for `ContactRequestApprovedEvent` and `ContactRequestRejectedEvent`.
    * Modify: `Percolator.Desktop/Features/Chats/Services/ConversationStateService.cs`:
      * Constructor: add non-nullable `IEventSubscriber eventSubscriber`.
      * Subscribe to `DirectChannelCreatedEvent` (filtered by `evt.OwnerIdentityId == _activePersonaId`) → invoke `LoadConversationsForPersonaAsync(evt.OwnerIdentityId)` (synthetic `ChannelId.New()` fallback was already deleted in `[DESK-07-A]`).
  * **5. Simulator Decorator Deletion & Thin Delegation**:
    * Delete: `Percolator.Simulator/Features/Topology/SimulatorNotifyingHandshakeDecorator.cs`.
    * Modify: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` — remove `SimulatorNotifyingHandshakeDecorator` registration; register `services.AddScoped<IHandshakeService, HandshakeService>();` and `services.AddScoped<IInboundConnectionWorkflow, InboundConnectionWorkflow>();`.
    * Modify: `Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs`:
      * `AcceptHandshakeAsync`: delegate core protocol work to `scope.ServiceProvider.GetRequiredService<IInboundConnectionWorkflow>().AcceptRequestAsync(target.PublicIdentityId, senderId, ct)`. Delete inline `DirectChannel.Create` (lines 196–202) and manual `_stateBus.Publish(PendingHandshakeChanged)` (lines 267–271).
      * `RejectHandshakeAsync`: delegate to `IInboundConnectionWorkflow.RejectRequestAsync` / `BlockRequestAsync`. Delete raw `PercolatorDbContext.PeerContacts.Remove` hack (lines 314–332) and manual `_stateBus.Publish(PendingHandshakeChanged)`.
    * Modify: `Percolator.Simulator/Features/Topology/PersonaCardViewModel.cs`:
      * Resolve `IEventSubscriber` from `_hostScope.ServiceProvider`.
      * Subscribe (anchored to `ref Disposables`) to `InboundContactRequestReceivedEvent`, `ContactRequestApprovedEvent`, `ContactRequestRejectedEvent` → `RefreshPendingHandshakes()`, and `DirectChannelCreatedEvent` → `RefreshConversations()`.
      * Remove `_stateBus.Signals` subscription for `SignalCategory.PendingHandshakeChanged` for direct handshakes.
    * Modify: `Percolator.Simulator/Features/Scenarios/ScenarioRunnerService.cs` (lines 558, 632, 1582):
      * Replace `_stateBus.Signals.Where(... PendingHandshakeChanged)` with `target.Services.GetRequiredService<IEventSubscriber>().ObserveEvent<InboundContactRequestReceivedEvent>().Where(e => e.OwnerIdentityId == target.PublicIdentityId).FirstAsync(cancellationToken: ct)` (using `SimulatorEventBusExtensions.ObserveEvent<T>` from `DESK-07-A`).
    * Update/Create Tests:
      * `Percolator.Desktop.Tests/Features/Transports/InboundRequestStateServiceTests.cs` (non-nullable `IEventSubscriber`, null-guard permutation, event-driven refresh tests).
      * `Percolator.Desktop.Tests/Features/Chats/ConversationStateServiceTests.cs` (verify `DirectChannelCreatedEvent` surfaces direct and relayed chats under the correct rail target).
      * `Percolator.Simulator.Tests/Topology/PersonaCardViewModelTests.cs` (verify `InboundContactRequestReceivedEvent`, `ContactRequestApprovedEvent`, `ContactRequestRejectedEvent`, and `DirectChannelCreatedEvent` drive pending banners and conversation list).
* **CQRS Query & Anti-Shortcut Boundaries**:
  * Zero `IDateTimeProvider? = null` defaults; zero `PercolatorDbContext` mutations in Simulator; zero inline `DirectChannel.Create` outside `InboundConnectionWorkflow` / `PeerConnectionService`; UI layers read strictly via `IPeerContactQueryService` and `IConversationListQueryService`.
* **Acceptance Criteria**:
  * Inbound direct or relayed handshake triggers `InboundContactRequestReceivedEvent` and illuminates Pending Requests in Desktop and Accept/Reject banner in Simulator without `SimulatorNotifyingHandshakeDecorator`.
  * Accepting an inbound handshake in either Desktop or Simulator persists `DirectChannel`, publishes `DirectChannelCreatedEvent`, clears the pending banner, and immediately displays the active chat in the appropriate chat list (Direct Messages or hosting Relay).
  * Rejecting (non-block) allows the same peer to re-request cleanly via domain logic without raw EF deletions; blocking drops future handshakes silently.
* **Verification Gate (Scoped Smoke Tests — Full Solution Gate runs at `[DESK-07-F]`)**:
  * `dotnet test Percolator.Domain.Tests/Percolator.Domain.Tests.csproj --filter "FullyQualifiedName~PeerContact|FullyQualifiedName~DirectChannel"`
  * `dotnet test Percolator.Application.Tests/Percolator.Application.Tests.csproj --filter "FullyQualifiedName~ContactRequestCoordinator|FullyQualifiedName~HandshakeService|FullyQualifiedName~InboundConnectionWorkflow"`
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~InboundRequestStateService|FullyQualifiedName~ConversationState"`
  * `dotnet test Percolator.Simulator.Tests/Percolator.Simulator.Tests.csproj --filter "FullyQualifiedName~PersonaCardViewModel"`

---

### [x] [DESK-07-C] Signal 0-RTT Profile Return-Route Carrying, Ratchet Turn Activation & Unified Outbound Workflow (COMPLETED)
* **QMS Traceability**: `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`, `DEP-DESKTOP-CRYPTO-BOUNDARY`, `DEP-DESKTOP-ROUTE-PRIVACY` (allocates `SYS-DIRECT-CONTACT-REQUEST-OUTBOX`, `SYS-SEALED-SENDER-ROUTING`, `SYS-RELAY-ONLY-CLOAKING`, `SYS-PAYLOAD-LENGTH-PADDING`)
* **Detailed Design**: `INV-APP-EVENT-BUS-DECOUPLING`, `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-SIGNAL-X3DH-PARITY`
* **Governing Specs**: `[UISS-001]` to `[UISS-005]`, `[VM-003]`, `[VM-006]`, `anti-shortcut-discipline` Rules 1, 2, 3, 11, 13, 23, 24, 25
* **Target Deliverables**:
  * **1. Eliminate Ad-Hoc Wire Protocols (`AppId.HandshakeControl`) & Enforce Zero-JSON Policy**:
    * Remove `AppId.HandshakeControl` (`0x04`) from `Percolator.PluginSdk/AppId.cs`.
    * Delete `Percolator.Application/Handshake/HandshakeControlDto.cs`, `HandshakeControlPayloadHandler.cs`, `HandshakeControlPayloadHandlerTests.cs`, and `FakePayloadSerializer.cs`.
    * Remove `HandshakeControlPayloadHandler` DI registration from `InfrastructureServiceCollectionExtensions.cs`.
  * **2. Signal 0-RTT Padded `ProfileCapability` Return-Route Protocol (AppId.Chat + SteppedPadding)**:
    * Create: `Percolator.PluginSdk/Profiles/IInitialHandshakePayloadProvider.cs` (`ValueTask<(AppId AppId, ReadOnlyMemory<byte> Payload)?> CreateInitialPayloadAsync(PublicIdentityId localIdentityId, bool allowDirectEndpoint, CancellationToken ct = default);`).
    * Implement in `Percolator.Apps.Chat/Features/Profiles/ChatInitialPayloadProvider.cs`: serializes `ProfileCapabilityDto` using `ProtobufPayloadSerializer` wrapped in `SteppedPadding.CreatePaddedEnvelopeBytes(ChatPayloadType.ProfileCapability, dto)` (`AppId.Chat`).
    * Enforce `SYS-RELAY-ONLY-CLOAKING`:
      * In `PeerConnectionService.ConnectToDirectPeerAsync`, allow direct endpoint only when route is direct. In `ConnectToPeerAsync` (relayed), pass `allowDirectEndpoint: false`, emitting zero local IP/port.
      * In `ChatPayloadHandler.HandleProfileCapabilityAsync`, register direct endpoint only when `SourceType == DirectP2P` and direct P2P is allowed. Relayed capability routes strictly populate `PeerMailboxRoutingCapability` via derived `DeliveryToken`.
      * In `RoutingCoordinator.ResolveRouteAsync`, guard direct endpoint resolution with local `RelayOnlyRouting` policy.
  * **3. Double Ratchet Turn Activation & Domain State Transitions**:
    * Modify: `Percolator.Domain/Identities/Model/PeerContact.cs`:
      * `PendingOutbound = 3` on `ContactState`.
      * `CreateOutboundRequest(...)` sets `PendingOutbound`.
      * `MarkOutboundAccepted(...)` transitions `PendingOutbound` → `Active`.
    * Modify: `Percolator.Domain/Channels/Model/DirectChannel.cs`:
      * `Activate(IDateTimeProvider timeProvider, RelayEndpoint? relayEndpoint = null)` emits `DirectChannelActivatedDomainEvent`.
    * Create: `Percolator.Application/Channels/Events/DirectChannelActivatedEvent.cs` (`[MapFromDomainEvent(typeof(DirectChannelActivatedDomainEvent))]`).
    * Modify: `Percolator.Application/Ingress/InboundIngressPipeline.cs` (`ProcessDirectAsync`):
      * When an authenticated Double Ratchet frame from Bob is decrypted, if Alice's local `DirectChannel` is in `PendingAcceptance` or `PeerContact` is `PendingOutbound`, transition both to `Active`, save, and publish `DirectChannelActivatedEvent`. Bob decrypting Alice's X3DH and sending any response proves channel establishment cryptographically without synthetic ACK packets.
  * **4. Unified Outbound Connection Workflow & Invitation Codec Fixes**:
    * Modify: `Percolator.Application/Invitations/PeerInvitationCodec.cs`:
      * Add `&relay=` query parameter encoding/decoding and versioned binary byte support for `RelayEndpoint`.
    * Modify: `Percolator.Application/Services/PeerConnectionService.cs`:
      * In `ConnectToDirectPeerAsync`, ensure `EnsureDirectChannelAsync` receives `relayEndpoint: null`.
    * Modify: `Percolator.Desktop/Common/Services/DesktopNetworkEndpointDiscoveryPort.cs`:
      * Return `https://127.0.0.1:{boundPort}` instead of invalid `percolator://` scheme.
    * Modify: `Percolator.Application/Connections/InboundConnectionWorkflow.cs`:
      * Remove `HandshakeControlEnvelopeDto(HandshakeAccepted)` transmission.
      * Keep pure domain acceptance, pending handshake completion, and direct channel activation.
  * **5. Simulator WorldState Scope Elimination**:
    * Modify: `Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs`:
      * In `AcceptHandshakeAsync`, delete `_worldState.FindPersonaHostByIdentity(senderId)` and `AutoShareProfileOnConnectAsync(target, senderHost)`. Operate exclusively on target persona's DI scope and local stores.
* **CQRS Query & Anti-Shortcut Boundaries**:
  * Zero ad-hoc unpadded control `AppId`s (`anti-shortcut-discipline` Rule 24).
  * Zero JSON serialization in protocol wire payloads or test doubles (Rule 23).
  * Zero `_worldState` peer peeking in coordinator acceptance loops (Rule 25).
  * Direct P2P channels have `RelayEndpoint = null`.
* **Acceptance Criteria**:
  * Direct and relayed handshakes stage as Pending Acceptance on initiator with zero local IP leakage on relayed paths.
  * Inbound acceptance decrypts X3DH, stages active channel, and responder profile capability or first chat message automatically activates the initiator via cryptographic Double Ratchet turn.
  * Both Desktop and Simulator update channel lists cleanly without synthetic ACK packets or `_worldState` bypasses.
* **Verification Gate (Scoped Smoke Tests — Full Solution Gate runs at `[DESK-07-F]`)**:
  * `dotnet test Percolator.Domain.Tests/Percolator.Domain.Tests.csproj --filter "FullyQualifiedName~DirectChannel|FullyQualifiedName~PeerContact"`
  * `dotnet test Percolator.Application.Tests/Percolator.Application.Tests.csproj --filter "FullyQualifiedName~PeerConnectionService|FullyQualifiedName~InboundConnectionWorkflow|FullyQualifiedName~PeerInvitationCodec"`
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~ConversationState|FullyQualifiedName~AddConnection"`

---

### [DESK-07-D] Shared Group Creation, Relay ACK, Invitation Lifecycle & Live Group Stream Worker
* **QMS Traceability**: `DEP-DESKTOP-SEGREGATION-ROLE`, `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`, `DEP-DESKTOP-CRYPTO-BOUNDARY` (allocates `SYS-ANONYMOUS-GROUP-RELAY`, `SYS-GROUP-SENDER-KEY-FORWARD-SECRECY`)
* **Detailed Design**: `INV-APP-EVENT-BUS-DECOUPLING`, `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-RECORD-EVENT-ENFORCEMENT`
* **Governing Specs**: `[UISS-001]` to `[UISS-005]`, `[VM-003]`, `[VM-006]`, `[VM-018]`, `anti-shortcut-discipline` Rules 1, 2, 8, 11, 13, 14
* **Target Deliverables**:
  * **1. Domain Events on `GroupChannel`**:
    * Create: `Percolator.Domain/Channels/Events/GroupChannelCreatedDomainEvent.cs`:
      ```csharp
      public sealed record GroupChannelCreatedDomainEvent(
          ChannelId ChannelId,
          PublicIdentityId CreatorId,
          string Name,
          RelayEndpoint? RelayEndpoint,
          DateTimeOffset OccurredOnUtc) : IDomainEvent
      {
          public Guid EventId { get; } = Guid.NewGuid();
      }
      ```
    * Modify: `Percolator.Domain/Channels/Model/GroupChannel.cs`:
      * `CreateGenesis(ChannelId id, PublicIdentityId creatorId, string name, IDateTimeProvider timeProvider, RelayEndpoint? relayEndpoint = null)`: emit `GroupChannelCreatedDomainEvent`.
  * **2. Application & Chat Events**:
    * Create in `Percolator.Application/Channels/Events/`:
      * `GroupChannelCreatedEvent.cs` (`[MapFromDomainEvent(typeof(GroupChannelCreatedDomainEvent))]`, explicit `ChannelId`, `PublicIdentityId CreatorId`, `string Name`, `RelayEndpoint? RelayEndpoint`).
      * `GroupMemberJoinedEvent.cs` (`[MapFromDomainEvent(typeof(MemberJoinedEvent))]`, explicit `ChannelId`, `PublicIdentityId MemberId`, `ChannelRole Role`, `EpochNumber NewEpoch`).
      * `GroupMemberRemovedEvent.cs` (`[MapFromDomainEvent(typeof(MemberRemovedEvent))]`, explicit `ChannelId`, `PublicIdentityId MemberId`, `EpochNumber NewEpoch`).
    * Create in `Percolator.Apps.Chat/Features/GroupInvitations/Events/` (implementing `Percolator.PluginSdk.Events.IApplicationEvent`):
      * `GroupInvitationReceivedEvent(InvitationId InvitationId, PublicIdentityId RecipientIdentityId, ChannelId TargetGroupChannelId, PublicIdentityId InviterId, string GroupName, string? RelayEndpoint, DateTimeOffset OccurredOnUtc)`.
      * `GroupInvitationResolvedEvent(InvitationId InvitationId, PublicIdentityId RecipientIdentityId, ChannelId TargetGroupChannelId, GroupInvitationStatus NewStatus, DateTimeOffset OccurredOnUtc)`.
  * **3. Driven Relay Port, PluginSdk Invitation Port & Shared `IGroupChannelWorkflow` (Moves 1,000+ Lines Out of Simulator Without Violating Project Boundaries)**:
    * *Dependency boundary*: `Percolator.Application` references only `Percolator.Domain` + `Percolator.PluginSdk` (NEVER `Percolator.Apps.Chat`). Bridge group-invitation and system-notice actions cleanly via `Percolator.PluginSdk`:
    * Create: `Percolator.PluginSdk/Groups/IGroupInvitationPort.cs` (implemented by `GroupInvitationService` in `Percolator.Apps.Chat`):
      ```csharp
      public interface IGroupInvitationPort
      {
          Task<DomainResult<InvitationId>> SendInvitationAsync(
              ChannelId targetGroupChannelId, string groupName, PublicIdentityId inviterId, PublicIdentityId inviteeId,
              ChannelId directChannelId, string? relayEndpoint, DeliveryRoute route, IReadOnlyList<PublicIdentityId>? initialMembers = null, CancellationToken ct = default);
          Task<DomainResult<GroupChannel>> AcceptInvitationAsync(
              InvitationId invitationId, PublicIdentityId responderId, ChannelId directChannelId, DeliveryRoute responseRoute, CancellationToken ct = default);
          Task<DomainResult> DeclineInvitationAsync(
              InvitationId invitationId, PublicIdentityId responderId, ChannelId directChannelId, DeliveryRoute responseRoute, CancellationToken ct = default);
          Task<DomainResult> SendSystemNoticeAsync(
              ChannelId directChannelId, PublicIdentityId senderId, PublicIdentityId recipientId, ChannelId targetGroupChannelId, string noticeText, DeliveryRoute route, CancellationToken ct = default);
      }
      ```
    * Create: `Percolator.Application/Ports/IGroupRelayClient.cs`:
      ```csharp
      public interface IGroupRelayClient
      {
          Task<DomainResult<bool>> CreateGroupAsync(
              RelayEndpoint relayEndpoint,
              ChannelId channelId,
              ReadOnlyMemory<byte> encryptedGenesisBlob,
              ReadOnlyMemory<byte> zkPublicParams,
              IReadOnlyList<BlindedRoutingToken> initialRoutingTokens,
              CancellationToken ct = default);

          Task<DomainResult<uint>> CommitRosterMutationAsync(
              RelayEndpoint relayEndpoint,
              ChannelId channelId,
              IReadOnlyCollection<BlindedRoutingToken> authorizedRoutingTokens,
              CancellationToken ct = default);

          IAsyncEnumerable<InboundGroupEnvelope> SubscribeGroupStreamAsync(
              RelayEndpoint relayEndpoint,
              ChannelId channelId,
              PublicIdentityId subscriberIdentityId,
              BlindedRoutingToken subscriberRoutingToken,
              CancellationToken ct = default);
      }
      ```
    * Create: `Percolator.Infrastructure/Transport/GrpcGroupRelayClient.cs` implementing `IGroupRelayClient` over `IPeerGrpcChannelFactory` + `AnonymousGroupRelayService.AnonymousGroupRelayServiceClient` (real gRPC TLS, zero in-process relay DB peeking).
    * Create: `Percolator.Application/Groups/IGroupChannelWorkflow.cs` & `Percolator.Application/Groups/GroupChannelWorkflow.cs` (shared by BOTH Desktop and Simulator; depends strictly on Domain, PluginSdk, and Application ports):
      * `CreateGroupAsync(PublicIdentityId creatorId, RelayEndpoint relayEndpoint, string groupName, IReadOnlyList<PublicIdentityId> memberIds, CancellationToken ct)`:
        1. Resolve routing tokens for creator (from `IRelayDirectory`) and invited members (from `IPeerMailboxRoutingRepository` — zero cross-host RAM peeking!).
        2. Call `_groupRelayClient.CreateGroupAsync(...)` and **require explicit Relay ACK (`Created == true`) before persisting or publishing**.
        3. Call `GroupChannel.CreateGenesis(channelId, creatorId, groupName, _timeProvider, relayEndpoint)`, add members, save via `IChannelRepository.SaveGroupAsync`.
        4. Persist genesis `GroupCredentials` (`IGroupCredentialsRepository`) and register `IChannelRelayResolver.RegisterRelayForChannel(channelId, relayUri)`.
        5. Distribute creator SenderKey via `IGroupKeyDistributionService.DistributeSenderKeyAsync`.
        6. Send pairwise invitations to each invited member via `IGroupInvitationPort.SendInvitationAsync` (resolving each peer's route via `IRoutingCoordinator` and deterministic `ChannelId.DeterministicForDirect`).
        7. Map & publish `GroupChannelCreatedEvent` via `IEventPublisher` and clear domain events.
      * `AcceptGroupInvitationAsync(PublicIdentityId personaId, InvitationId invitationId, CancellationToken ct)`:
        1. Call `_groupInvitationPort.AcceptInvitationAsync(...)` (which creates/updates local `GroupChannel` with `RelayEndpoint`).
        2. Register `IChannelRelayResolver` and initialize `GroupCredentials` if absent.
        3. Distribute invitee SenderKey via `IGroupKeyDistributionService.DistributeSenderKeyAsync` to all other group members.
        4. Publish `GroupChannelCreatedEvent` and `GroupInvitationResolvedEvent`.
      * `DeclineGroupInvitationAsync(PublicIdentityId personaId, InvitationId invitationId, CancellationToken ct)`:
        1. Call `_groupInvitationPort.DeclineInvitationAsync(...)` (declines, persists, sends `GroupInvitationResponseDto(Accepted: false)`, and publishes `GroupInvitationResolvedEvent`).
      * `AddGroupMemberAsync(...)` & `EvictGroupMemberAsync(...)`:
        * Moves roster mutation, `IGroupRelayClient.CommitRosterMutationAsync`, `IGroupInvitationPort.SendSystemNoticeAsync` pairwise broadcast, forward-secrecy SenderKey rotation, and `GroupMemberJoinedEvent`/`GroupMemberRemovedEvent` publishing into `GroupChannelWorkflow`.
    * Multi-Persona Group Invitation Persistence & Query Service:
      * Modify: `Percolator.Apps.Chat/Features/GroupInvitations/PendingGroupInvitation.cs`, `Percolator.Infrastructure/Persistence/Entities/ChatEntities.cs` (`ChatGroupInvitationEntity`), `PercolatorDbContext.OnModelCreating`, and `SqlGroupInvitationRepository.cs`: add `PublicIdentityId RecipientIdentityId` (`byte[]` column—no EF Core migration; assume clean `.db` deletion) so multi-persona Desktop vaults isolate group invitations per persona (`DEP-DESKTOP-PERSONA-ISOLATION`).
      * Modify: `Percolator.Apps.Chat/Features/GroupInvitations/IGroupInvitationQueryService.cs` & `Percolator.Infrastructure/Persistence/Repositories/SqlGroupInvitationQueryService.cs`: refactor from Scoped `PercolatorDbContext` to **Singleton** over `IDbContextFactory<PercolatorDbContext>` (Rule 16) and filter `GetPendingInvitationsAsync(PublicIdentityId recipientId, CancellationToken ct)` by `RecipientIdentityId`.
    * Modify: `Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs`:
      * Inject non-nullable `IEventPublisher`.
      * `HandleGroupInvitationAsync`: populate `RecipientIdentityId = context.RecipientIdentityId`; after `await _invitationRepo.AddAsync(invitation, ct)`, publish `GroupInvitationReceivedEvent`.
      * `HandleGroupInvitationResponseAsync`: after updating invitation & group membership, publish `GroupInvitationResolvedEvent` (and `GroupMemberJoinedEvent` if added).
    * Modify: `Percolator.Apps.Chat/Features/GroupInvitations/GroupInvitationService.cs`:
      * Implement `IGroupInvitationPort`; inject non-nullable `IEventPublisher`; add `DeclineInvitationAsync(...)` and `SendSystemNoticeAsync(...)`; persist `RelayEndpoint` on `GroupChannel.CreateGenesis`. Register `services.AddScoped<IGroupInvitationPort>(sp => sp.GetRequiredService<GroupInvitationService>());` in `AddChatApp()`.
  * **4. Real `GroupStreamCoordinator` Wired into `RelayGroupStreamWorker`**:
    * Create: `Percolator.Infrastructure/Transport/Workers/GroupStreamCoordinator.cs` (`IHostedService` / runner for `RelayGroupStreamWorker`):
      * On startup, reconciles all active personas (`IActiveIdentitySet`) × persisted joined groups (`IChannelRepository.GetAllGroupsForOwnerAsync`), registers `IChannelRelayResolver` from persisted `GroupChannel.RelayEndpoint`, and opens one supervised gRPC `SubscribeGroupStreamAsync` task per `(personaId, channelId)` feeding inbound `InboundGroupEnvelope`s into `IInboundIngressPipeline.ProcessInboundAsync`.
      * Subscribes to `GroupChannelCreatedEvent`, `GroupMemberRemovedEvent`, `PersonaActivatedEvent`, `PersonaDeactivatedEvent` via `IEventSubscriber` to dynamically start/stop streams without restart!
    * Modify: `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`:
      * Register `IGroupRelayClient → GrpcGroupRelayClient`, `IGroupChannelWorkflow → GroupChannelWorkflow` (Scoped), `IGroupInvitationQueryService → SqlGroupInvitationQueryService` (**Singleton**), and replace the no-op `Task.Delay(Timeout.InfiniteTimeSpan)` `RelayGroupStreamWorker` stub (line 307) with `GroupStreamCoordinator`.
  * **5. Desktop UI Real Group Integration**:
    * Create: `Percolator.Desktop/Features/Chats/Services/DesktopGroupChannelWorkflow.cs` (Singleton scope-creating facade for `IGroupChannelWorkflow`, mirroring `DesktopInboundConnectionWorkflow`).
    * Modify: `Percolator.Desktop/Features/Chats/ViewModels/GroupCreationDialogViewModel.cs`:
      * Replace `IConversationStateService` with non-nullable `IGroupChannelWorkflow`.
      * Change `CreateGroup()` to `async Task CreateGroupAsync()` invoking `await _groupChannelWorkflow.CreateGroupAsync(_activePersonaId, SelectedRelay.Endpoint, groupName, selectedMemberIds, ct)`. Delete fake `_conversationStateService.RegisterRelayedConversation(...)` stub!
    * Modify: `Percolator.Desktop/Features/Transports/Services/InboundRequestStateService.cs` & `PendingRequestsPanelViewModel.cs`:
      * Inject non-nullable `IGroupInvitationQueryService` (Singleton) and `IGroupChannelWorkflow` (Singleton facade).
      * Subscribe to `GroupInvitationReceivedEvent` and `GroupInvitationResolvedEvent` (filtered by `evt.RecipientIdentityId == _activePersonaId`) to surface inbound pending group invites alongside direct contact requests (with Accept/Decline actions delegating to `IGroupChannelWorkflow`).
    * Modify: `Percolator.Desktop/Features/Chats/Services/ConversationStateService.cs`:
      * Subscribe to `GroupChannelCreatedEvent` → reload via `IConversationListQueryService` so newly created or accepted groups appear immediately under the hosting Relay in `ChatsSidebarViewModel`!
  * **6. Simulator `GroupChatCoordinator` Reduction**:
    * Modify: `Percolator.Simulator/Features/GroupMessaging/GroupChatCoordinator.cs`:
      * Delete `_streamSubscriptions`, `SubscribePersonaToGroup`, `UnsubscribePersonaFromGroup`, `CommitRosterMutationAsync`, `RotateAndDistributeHostSenderKeyAsync`, inline `GroupChannel.CreateGenesis`, and manual `_stateBus.Publish(GroupStateChanged / PendingHandshakeChanged)`.
      * Delegate `CreateGroupOnRelayAsync`, `AcceptGroupInviteAsync`, `DeclineGroupInviteAsync`, `AddGroupMemberAsync`, `EvictGroupMemberAsync` directly to `IGroupChannelWorkflow` on the target persona host.
    * Modify: `Percolator.Simulator/Features/Topology/PersonaCardViewModel.cs`:
      * Subscribe to `GroupInvitationReceivedEvent`, `GroupInvitationResolvedEvent`, `GroupChannelCreatedEvent`, `GroupMemberJoinedEvent`, `GroupMemberRemovedEvent` via `IEventSubscriber` to drive `HasPendingGroupInvite` banner and `AvailableConversations`.
* **CQRS Query & Anti-Shortcut Boundaries**:
  * Relay creation ACK is strictly verified over gRPC (`IGroupRelayClient.CreateGroupAsync`) before local persistence or UI event emission.
  * Strict hexagonal dependency direction preserved: `Percolator.Application` never references `Percolator.Apps.Chat` (all cross-boundary calls flow through `Percolator.PluginSdk` ports).
  * Zero `_worldState.FindPersonaHostByIdentity(m.Id)?.MailboxRoutingToken` RAM peeking in group workflows (`anti-shortcut-discipline` Rules 2, 13, 14).
  * `SqlGroupInvitationQueryService` is Singleton over `IDbContextFactory<PercolatorDbContext>` (Rule 16) with strict `RecipientIdentityId` persona filtering.
* **Acceptance Criteria**:
  * Creating a group in Desktop or Simulator calls the Relay over gRPC, receives ACK, persists `GroupChannel` + `RelayEndpoint`, opens live group stream via `GroupStreamCoordinator`, sends pairwise invites, and immediately renders the group chat under the hosting Relay in Desktop and Simulator.
  * Receiving a group invite in Desktop or Simulator surfaces the pending invite via `GroupInvitationReceivedEvent`; accepting it persists `GroupChannel`, subscribes to the relay group stream, distributes SenderKeys, and immediately shows the group chat in both apps.
* **Verification Gate (Scoped Smoke Tests — Full Solution Gate runs at `[DESK-07-F]`)**:
  * `dotnet test Percolator.Domain.Tests/Percolator.Domain.Tests.csproj --filter "FullyQualifiedName~GroupChannel"`
  * `dotnet test Percolator.Apps.Chat.Tests/Percolator.Apps.Chat.Tests.csproj --filter "FullyQualifiedName~GroupInvitation"`
  * `dotnet test Percolator.Application.Tests/Percolator.Application.Tests.csproj --filter "FullyQualifiedName~GroupChannelWorkflow"`
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~GroupCreation|FullyQualifiedName~ConversationState|FullyQualifiedName~InboundRequestState"`

---

### [DESK-07-E] Real-Time Chat/Profile Event Pipeline, Live Desktop Transcript & Simulator De-Duplication
* **QMS Traceability**: `DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION`, `DEP-DESKTOP-SEGREGATION-ROLE`, `DEP-DESKTOP-MEDIA-METADATA-SANITIZATION-INGRESS`
* **Detailed Design**: `DEC-DEBOUNCED-DROP-RELOAD`, `DEC-REACTIVE-EVENT-BUS-BRIDGE`, `INV-APP-EVENT-BUS-DECOUPLING`, `INV-DESKTOP-STATEFUL-PROJECTIONS`
* **Governing Specs**: `[R3-002]` to `[R3-007]`, `[VM-003]`, `[VM-006]`, `[VM-010]`, `anti-shortcut-discipline` Rules 1, 2, 4, 8, 10
* **Target Deliverables**:
  * **1. Chat & Profile Application Events (`Percolator.Apps.Chat/Events/`, implementing `Percolator.PluginSdk.Events.IApplicationEvent`)**:
    * `ChatMessageSentEvent(ChannelId ChannelId, PayloadId MessageId, PublicIdentityId AuthorId, DateTimeOffset OccurredOnUtc)`
    * `ChatMessageReceivedEvent(ChannelId ChannelId, PayloadId MessageId, PublicIdentityId AuthorId, DateTimeOffset OccurredOnUtc)`
    * `ChatMessageEditedEvent(ChannelId ChannelId, PayloadId MessageId, DateTimeOffset OccurredOnUtc)`
    * `ChatMessageDeletedEvent(ChannelId ChannelId, PayloadId MessageId, DateTimeOffset OccurredOnUtc)`
    * `ChatReactionUpdatedEvent(ChannelId ChannelId, PayloadId MessageId, DateTimeOffset OccurredOnUtc)`
    * `ChatReceiptReceivedEvent(ChannelId ChannelId, PayloadId UpToMessageId, bool IsReadReceipt, DateTimeOffset OccurredOnUtc)`
    * `PeerProfileUpdatedEvent(PublicIdentityId PeerIdentityId, string DisplayName, uint Revision, DateTimeOffset OccurredOnUtc)`
    * Modify: `Percolator.Apps.Chat/Features/Messaging/ChatService.cs` — inject non-nullable `IEventPublisher`; publish `ChatMessageSentEvent`, `ChatMessageEditedEvent`, `ChatMessageDeletedEvent`, `ChatReactionUpdatedEvent` after repository persistence commits.
    * Modify: `Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs` — publish `ChatMessageReceivedEvent`, `ChatMessageEditedEvent`, `ChatMessageDeletedEvent`, `ChatReactionUpdatedEvent`, `ChatReceiptReceivedEvent`, `PeerProfileUpdatedEvent` after repository persistence commits.
  * **2. Shared Application Profile Sync Workflow (Eliminates Simulator `ProfileSyncCoordinator` Business Logic)**:
    * Modify: `Percolator.PluginSdk/Profiles/IProfilePorts.cs` — add `IProfileCapabilitySender` exposing `ShareProfileCapabilityAsync(...)` (implemented by `ChatService` in `Percolator.Apps.Chat`, registered via `services.AddScoped<IProfileCapabilitySender>(sp => sp.GetRequiredService<ChatService>());` in `AddChatApp()`) so `Percolator.Application` never references `Percolator.Apps.Chat`.
    * Create: `Percolator.Application/Profiles/IProfileSyncWorkflow.cs` & `Percolator.Application/Profiles/ProfileSyncWorkflow.cs`:
      * `ShareProfileWithPeerAsync(PublicIdentityId ownerId, PublicIdentityId peerId, CancellationToken ct)` — uses `ILocalIdentityKeyStore`, `IIdentityProfileRepository`, `IRelayDirectory`, `IRoutingCoordinator`, and `IProfileCapabilitySender.ShareProfileCapabilityAsync` (zero `_worldState` peeking!).
      * `BroadcastProfileToActiveContactsAsync(PublicIdentityId ownerId, CancellationToken ct)` — iterates `IPeerContactRepository.GetAllForOwnerAsync` (`ContactState.Active`) and calls `ShareProfileWithPeerAsync`.
    * Create: `Percolator.Infrastructure/Transport/Workers/ProfileSyncEventListener.cs` (`BackgroundService` subscribed to `DirectChannelCreatedEvent` and `DirectChannelActivatedEvent`): automatically invokes `IProfileSyncWorkflow.ShareProfileWithPeerAsync` when active persona policy enables auto-share.
  * **3. Live Desktop Messaging & Sidebar Updates**:
    * Modify: `Percolator.Desktop/Features/Chats/ViewModels/ChatViewModel.cs`:
      * Inject non-nullable `IEventSubscriber eventSubscriber` (update `SessionScopeFactory` & `ChatViewModelTests`).
      * Subscribe (anchored to `ref Bag`) to `ChatMessageReceivedEvent`, `ChatMessageSentEvent`, `ChatMessageEditedEvent`, `ChatMessageDeletedEvent`, `ChatReactionUpdatedEvent`, `ChatReceiptReceivedEvent` filtered by `evt.ChannelId == _channelId` → invoke `_reloadCoordinator.RequestReload()`.
    * Modify: `Percolator.Desktop/Features/Chats/Services/ConversationStateService.cs`:
      * Subscribe to `ChatMessageReceivedEvent`, `ChatMessageSentEvent`, and `PeerProfileUpdatedEvent` → refresh active persona conversation summaries (updating snippet, timestamp, unread badge, and synced display name).
  * **4. Simulator Complete Handshake & Chat De-Duplication**:
    * Delete: `Percolator.Simulator/Features/Topology/SimulatorNotifyingAppRouterDecorator.cs`.
    * Modify: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` — remove `SimulatorNotifyingAppRouterDecorator` registration; register production `IAppRouter` via `AddTransportInfrastructure`.
    * Modify: `Percolator.Simulator/Features/Topology/PersonaCardViewModel.cs`:
      * **DELETE** `_worldState.Personas` RAM-peeking loop (lines 578–589).
      * Populate `AvailableConversations` strictly from production `IConversationListQueryService.GetConversationsAsync(...)` (removing `ISimulatorChatQueryService.GetJoinedGroupsAsync` dependency).
      * Subscribe to `ChatMessageReceivedEvent`, `ChatMessageSentEvent`, `ChatMessageEditedEvent`, `ChatMessageDeletedEvent`, `ChatReactionUpdatedEvent`, `ChatReceiptReceivedEvent`, `PeerProfileUpdatedEvent` via `IEventSubscriber` → invoke `RefreshChatTranscript()` / `RefreshConversations()`.
      * Remove `SignalCategory.ChatTranscriptChanged`, `SignalCategory.GroupStateChanged`, `SignalCategory.PendingHandshakeChanged`, and `SignalCategory.RatchetSessionChanged` subscriptions from `PersonaCardViewModel`.
    * Modify: `Percolator.Simulator/Features/DirectChat/DirectChatCoordinator.cs`:
      * Replace `ISimulatorChatQueryService.GetTranscriptAsync` and direct write-repo reads (`IPeerContactRepository`, `IPeerProfileRepository`) with production `IChatMessageQueryService`, `IPeerContactQueryService`, and `IPeerProfileQueryService`.
      * Remove manual `DrainMailboxAsync` and manual `_stateBus.Publish(ChatTranscriptChanged)` (`FlushOutboxAsync` already deleted in `[DESK-07-A]`).
    * Modify: `Percolator.Simulator/Features/RichChatAndProfiles/ProfileSyncCoordinator.cs`:
      * Gut `ShareProfileCapabilityInternalAsync` and `AutoShareProfileOnConnectAsync`; delegate `ShareProfileAsync` and `RerollProfileAsync` broadcast directly to `IProfileSyncWorkflow`.
    * Modify: `Percolator.Simulator/Features/DirectChat/SimulatorChatQueryService.cs`:
      * Remove duplicate UI read methods (`GetTranscriptAsync`, `GetJoinedGroupsAsync`), retaining only headless scenario-assertion predicates (`HasMessageContainingAsync`, `FindMessageAsync`, `HasReactionAsync`, etc.) or renaming to `ScenarioAssertionQueryService`.
* **CQRS Query & Anti-Shortcut Boundaries**:
  * Zero `_worldState.Personas` or `peerHost.DisplayName` RAM peeking in Simulator conversation lists or transcripts (`anti-shortcut-discipline` Rules 2, 4).
  * Zero `IPeerContactRepository` / `IPeerProfileRepository` write-repo reads in UI coordinators (Rule 8).
  * Zero manual `ISimulatorStateBus` pokes for domain/chat/handshake state changes.
* **Acceptance Criteria**:
  * Sending a message from Simulator to Desktop (or Desktop to Simulator) in either Direct P2P, Relayed 1:1, or Relayed Group chat renders live in Desktop `ChatView` and Simulator `PersonaCardView` via `IEventSubscriber` without manual refresh or persona switching.
  * Edits, tombstones, emoji reactions, delivery/read receipts, and profile display-name updates propagate live to both Desktop and Simulator UIs.
  * `SimulatorNotifyingAppRouterDecorator.cs` is deleted.
* **Verification Gate (Scoped Smoke Tests — Full Solution Gate runs at `[DESK-07-F]`)**:
  * `dotnet test Percolator.Apps.Chat.Tests/Percolator.Apps.Chat.Tests.csproj`
  * `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj --filter "FullyQualifiedName~ChatViewModel|FullyQualifiedName~ConversationState"`
  * `dotnet test Percolator.Simulator.Tests/Percolator.Simulator.Tests.csproj`

---

### [DESK-07-G] Solution-Wide Zero-JSON Serialization Remediation & Protobuf Schema Parity
* **QMS Traceability**: `DEP-DESKTOP-CRYPTO-BOUNDARY`, Software Item Integration & Verification (IEC 62304 / FDA 21 CFR 820.30)
* **Detailed Design**: `INV-PAYLOAD-OPACITY`, `INV-RECORD-EVENT-ENFORCEMENT`
* **Governing Specs**: `anti-shortcut-discipline` Rule 23
* **Target Deliverables**:
  * **1. Eliminate Wire Protocol JSON in `RelaySwarmTrackerClient`**:
    * Define `SwarmCandidateProto` / `SwarmCandidateListProto` in `file_transfer.proto`.
    * Update `RelaySwarmTrackerClient.cs` to serialize and parse candidate lists via Protobuf bytes, removing `JsonSerializer.Serialize` / `Deserialize<List<CandidateDto>>`.
  * **2. Eliminate SQLite JSON Columns in Persistence**:
    * Audit and replace JSON columns with relational foreign key rows or binary Protobuf blobs:
      * `SqliteOutboxRepository.cs`: replace `DeliveryRouteDto` JSON serialization with compact relational columns (`RouteType`, `DirectEndpoint`, `RoutingToken`, `RelayEndpoint`, `DeliveryToken`).
      * `SqlitePeerProfileRepository.cs`: replace `AvatarReferenceJson` / `MetadataDto` JSON with relational columns or `BlobReferenceProto` byte blob.
      * `SqliteFilterPolicyRepository.cs`: replace `BlockedPeersJson` / `BlockedRelaysJson` with relational `BlockedPeerEntity` / `BlockedRelayEntity` tables.
      * `SqliteRelayPreKeyRepository.cs` & `SimulatorRelayQueryService.cs`: replace `OneTimePreKeysJson` with relational OPK rows or binary protobuf packing.
      * `SqliteTransferSessionRepository.cs` & `SqlFileTransferQueryService.cs`: replace `SelectedFilesJson` / `RecipientsJson` with relational entities.
  * **3. Eliminate JSON Test Doubles**:
    * Delete `TestPayloadSerializer.cs` in `Percolator.Apps.Chat.Tests` and `Percolator.ApplicationIntegrationTests`.
    * Standardize all test suites on production `ProtobufPayloadSerializer` or strongly-typed binary test doubles so missing wire schemas are caught during compilation and unit tests.
* **CQRS Query & Anti-Shortcut Boundaries**:
  * Zero `System.Text.Json` or `JsonSerializer` in wire pipelines, relational storage, or protocol test doubles.
* **Acceptance Criteria**:
  * Solution search for `JsonSerializer` returns matches only in non-protocol UI geometry (`VaultShellWindowGeometryStore.cs`) and human-readable Simulator scenario files (`.json`).
  * 100% of test suites execute against real Protobuf serialization without regressions.
* **Verification Gate**:
  * `dotnet test` (all test suites pass with zero JSON protocol serializers).

---

### [DESK-07-F] Remaining Simulator Business-Logic Cleanup & Cross-Peer E2E Verification
* **QMS Traceability**: Software Item Integration & Verification (IEC 62304 Clauses 5.6–5.7 / FDA 21 CFR 820.30(f)), `DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`, `DEP-DESKTOP-PEER-BLOCK-UI-SILENCE`
* **Detailed Design**: `INV-APP-EVENT-BUS-DECOUPLING`, `INV-DESKTOP-STATEFUL-PROJECTIONS`, `INV-DESKTOP-THREAD-DISCIPLINE`
* **Governing Specs**: `[VM-010]`, `[VM-011]`, `anti-shortcut-discipline` Rules 1–16
* **Target Deliverables**:
  * **1. Remaining Simulator-to-Application Parity Cleanups**:
    * File Transfer Events: Add `FileTransferOfferReceivedEvent` and `FileTransferProgressChangedEvent` (`IApplicationEvent`) in `Percolator.Apps.FileTransfer`; publish from `FileTransferPayloadHandler` / `TransferSessionService` and subscribe in Simulator `PersonaCardViewModel` / `SwarmPanelViewModel` (eliminates final reason `SimulatorNotifyingAppRouterDecorator` existed).
    * Modify: `Percolator.Simulator/Features/RichChatAndProfiles/RichChatActionCoordinator.cs` — remove manual `DrainMailboxAsync` and manual `_stateBus.Publish(ChatTranscriptChanged)` calls (`FlushOutboxAsync` already deleted in `[DESK-07-A]`); delegate strictly to `IChatService`.
    * Audit & Remove Remaining Simulator RAM Peeking:
      * `Percolator.Simulator/Features/RelayedChat/RelayProvisioningCoordinator.cs`: replace `relay.QueryService.HasActivePreKeyAsync` (in-process relay peeking) with real client gRPC prekey status check (`IPreKeyMaintenanceService` / `IRelayMailboxProvisioner`).
      * `Percolator.Simulator/Features/Topology/PersonaConnectPeerFlyoutViewModel.cs` & `RelayRegistrationFlyoutViewModel.cs`: verify 100% token/endpoint-only connection flow with zero "in-world persona" / "in-world relay" bypasses.
      * Confirm `ISimulatorStateBus` is used **exclusively** for simulator-only harness mechanics (Chaos fault toggles, Node start/stop topology cards, Virtual Clock ticks, Flight Recorder UI), with zero domain/protocol event duplication.
  * **2. Cross-Peer Desktop-Host ↔ Simulator-Host End-to-End Integration Suite**:
    * Create: `Percolator.ApplicationIntegrationTests/CrossPeerHandshakeAndChatParityTests.cs` running real loopback Kestrel TLS + SQLite hosts composed with the exact production DI slices (`AddSessionCoreInfrastructure`, `AddVaultPersistenceInfrastructure`, `AddTransportInfrastructure`, `AddChatInfrastructure`, `AddChatApp`):
      1. **Direct P2P Handshake Round-Trip**: Initiator sends invitation → Responder receives `InboundContactRequestReceivedEvent` → Responder accepts → Responder creates `DirectChannel` & sends encrypted `HandshakeAccepted` → Initiator receives `DirectChannelActivatedEvent` → both query `IConversationListQueryService` (0-message active chat present) → bidirectional chat messages arrive live via `ChatMessageReceivedEvent`.
      2. **Relayed 1:1 Handshake Round-Trip**: Both peers register on a real Kestrel Relay → Initiator connects via invitation token → Relay mailbox queues handshake → Responder drains, receives `InboundContactRequestReceivedEvent`, accepts → encrypted `HandshakeAccepted` returns via Relay mailbox → Initiator receives `DirectChannelActivatedEvent` → both query `IConversationListQueryService` with `RelayIdentityId` populated → bidirectional relayed messages & receipts deliver live.
      3. **Group Creation & Invitation Round-Trip**: Creator invokes `IGroupChannelWorkflow.CreateGroupAsync` → real Relay ACKs `CreateGroupAsync` → `GroupChannelCreatedEvent` fires → Invitee receives `GroupInvitationReceivedEvent`, accepts via `IGroupChannelWorkflow.AcceptGroupInvitationAsync` → `GroupStreamCoordinator` connects both peers to `AnonymousGroupRelayService.SubscribeGroupStream` → group broadcast messages deliver live to all members.
      4. **Reject vs Block Parity**: Rejecting deletes pending handshake and allows clean re-request via `ReopenInboundRequest`; blocking silently drops subsequent handshakes with zero events emitted (`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`).
  * **3. Full Simulator Scenario Replay & Regression Audit**:
    * Update any remaining scenario steps in `Percolator.Simulator/Features/Scenarios/ScenarioRunnerService.cs` to await deterministic `IEventSubscriber.ObserveEvent<T>()` signals (zero `Task.Delay` sleeps, zero deprecated `SignalCategory` waits).
* **Acceptance Criteria**:
  * Zero Simulator-owned decorators for handshake, app-router, or outbox flushing remain (`SimulatorNotifyingHandshakeDecorator`, `SimulatorNotifyingAppRouterDecorator`, `SimulatorOutboxFlushingIngressPipelineDecorator` all deleted).
  * Desktop and Simulator share 100% of handshake, routing, group lifecycle, profile sync, and real-time messaging code paths with zero duplicate business logic.
  * All 4 cross-peer E2E integration tests pass over real loopback TLS gRPC + SQLite.
  * All Simulator headless replay scenarios and solution-wide unit/integration tests pass with zero warnings.
* **Full Whole-Solution Verification Gate (Phase Completion)**:
  * `dotnet build Percolator.sln --no-restore` (0 errors, zero `GlobalUsings.cs` shims, zero EF Core migrations)
  * `dotnet test Percolator.ApplicationIntegrationTests/Percolator.ApplicationIntegrationTests.csproj --filter "FullyQualifiedName~CrossPeerHandshakeAndChatParityTests"`
  * `dotnet run --project Percolator.Simulator\Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator\Scenarios`
  * `dotnet test` (100% pass across all solution test projects)
  * `pwsh -File scripts/validate-qms-compliance.ps1`

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
| `IEventPublisher` | PluginSdk Events | `InMemoryEventBus` (Infrastructure) | Chunk 7A (`[DESK-07-A]`) |
| `IEventSubscriber` | PluginSdk Events | `InMemoryEventBus` (Infrastructure) | Chunk 7A (`[DESK-07-A]`) |
| `IHostedService` (Outbox) | Infrastructure Transport | `OutboxDispatchHost` (Infrastructure) | Chunk 7A (`[DESK-07-A]`) |
| `IConversationListQueryService`| Apps Chat | `SqlConversationListQueryService` (Infrastructure, Singleton) | Chunk 7 / 7A (`[DESK-07-A]`) |
| `IContactRequestCoordinator` | Application Profiles | `ContactRequestCoordinator` | Chunk 7B (`[DESK-07-B]`) |
| `IInboundConnectionWorkflow` | Application Connections | `InboundConnectionWorkflow` / `DesktopInboundConnectionWorkflow` | Chunk 7B (`[DESK-07-B]`) |
| `IAppPayloadHandler` (Control) | PluginSdk | `HandshakeControlPayloadHandler` (Application) | Chunk 7C (`[DESK-07-C]`) |
| `IOutboundConnectionWorkflow` | Application Connections | `OutboundConnectionWorkflow` | Chunk 7C (`[DESK-07-C]`) |
| `IGroupRelayClient` | Application Ports | `GrpcGroupRelayClient` (Infrastructure) | Chunk 7D (`[DESK-07-D]`) |
| `IGroupInvitationPort` | PluginSdk Groups | `GroupInvitationService` (Apps Chat) | Chunk 7D (`[DESK-07-D]`) |
| `IGroupInvitationQueryService` | Apps Chat | `SqlGroupInvitationQueryService` (Infrastructure, Singleton) | Chunk 7D (`[DESK-07-D]`) |
| `IGroupChannelWorkflow` | Application Groups | `GroupChannelWorkflow` / `DesktopGroupChannelWorkflow` | Chunk 7D (`[DESK-07-D]`) |
| `IHostedService` (Group Streams) | Infrastructure Transport | `GroupStreamCoordinator` / `RelayGroupStreamWorker` | Chunk 7D (`[DESK-07-D]`) |
| `IProfileCapabilitySender` | PluginSdk Profiles | `ChatService` (Apps Chat) | Chunk 7E (`[DESK-07-E]`) |
| `IProfileSyncWorkflow` | Application Profiles | `ProfileSyncWorkflow` / `ProfileSyncEventListener` | Chunk 7E (`[DESK-07-E]`) |
| `IChatDraftStateService` | Desktop Chats | `ChatDraftStateService` | Chunk 7 (`[DESK-07]`) |
| `IChatService` | Apps Chat | `ChatService` | Chunk 7 / 7E (`[DESK-07-E]`) |
| `IChatMessageQueryService` | Apps Chat | `SqlChatMessageQueryService` (Infrastructure) | Chunk 7 (`[DESK-07]`) |
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
