# Percolator.Desktop Implementation Plan

## 1. Executive Summary

`Percolator.Desktop` is the next-generation, cross-platform desktop UI for the Percolator peer-to-peer ecosystem, targeting **Windows, Linux, and macOS**. It replaces the legacy Windows-only `Desktop.Wpf` project.

**Privacy-First Core Invariant**: User privacy is a foundational design requirement. On application launch, `Percolator.Desktop` operates in **total network stealth**—it does not bind, listen, broadcast, or respond on any network port until the user explicitly selects and activates at least one cryptographic identity.

Built on:
* **Avalonia UI (11.2+)**: Native hardware-accelerated cross-platform UI.
* **FluentAvalonia (2.1+)**: Fluent Design System (Windows 11 controls, NavigationView, ContentDialog, Mica/Acrylic backdrops) rendering seamlessly across all platforms.
* **CommunityToolkit.Mvvm (8.4+)**: Source-generated boilerplate-free MVVM (`[ObservableProperty]`, `[RelayCommand]`).
* **R3 (1.3+) & ObservableCollections.R3 (3.3+)**: High-performance, zero-allocation reactive programming and synchronous collection projection.
* **.NET 10 (`net10.0`)**: Modern runtime performance and unified hosting (`Microsoft.Extensions.Hosting`).

---

## 2. Lessons Learned & Architectural Patterns from `Desktop.Wpf`

A comprehensive scan of `Desktop.Wpf` identified battle-tested architectural principles, along with WPF-specific pitfalls that the Avalonia implementation solves.

### 2.1 Domain Purity & Boundary Invariant
* **Strict Domain Isolation**: `Percolator.Domain` must **never** be polluted with UI-specific interfaces, methods, or concurrency constructs (e.g., no `.Freeze()`, no UI-specific snapshotting methods on domain entities). Domain models remain strictly pure, modeling core business rules, cryptographic invariants, and state transitions.
* **UI-Owned Presentation Models**: Any model required for UI reactivity, collection synchronization, or thread-safe UI snapshotting (such as `ChatMessageModel` with reactive status flags, or immutable `ChatMessageSnapshot` records) lives **strictly in the presentation project** (`Percolator.Desktop.Features.*.Models`).
* **Translation Boundary**: UI State Services receive pure domain events or query results from the Application layer, and construct/update UI-specific presentation models.

### 2.2 State Ownership: Local ViewModel State vs. Shared UI State Services
* **Local State (Owned by the ViewModel)**:
  * If state is private to a single view and is not shared with other views or components (e.g., text box input, local validation errors, transient dialog forms, expanded/collapsed accordion sections, local search filtering, scroll position), **the ViewModel is the authoritative owner of that state**.
  * ViewModels express local state cleanly using CommunityToolkit.Mvvm `[ObservableProperty]` or R3 `BindableReactiveProperty<T>`.
* **Shared State (Owned by Stateful UI Services)**:
  * When state is shared across multiple ViewModels, persists across navigation/tab transitions, or reflects cross-cutting application lifecycle (e.g., active identity, active peer sessions, global chat feeds, background swarm file transfers, network discovery tables), that state belongs to **Angular-like Stateful UI Services** (`ChatStateService`, `PeerConnectionStateService`, `DiscoveryStateService`, `FileTransferStateService`).
* **UI-Agnostic State Services**:
  * Stateful UI Services are long-lived singleton or scoped services that remain **100% UI-agnostic**. They never touch dispatchers, UI threads, sorting, or filtering.
  * They operate concurrently on background worker threads (gRPC events, DHT callbacks, network packets, SQLite sync) guarded by concurrency gates (`SemaphoreSlim` or lock gates).
  * Mutation Feeds: Services expose canonical `ObservableList<T>` / `ObservableDictionary<TKey, TValue>` containing UI presentation models, and emit fine-grained mutation signals (`Observable<Unit>` or typed event streams).

### 2.3 Strict MVVM & ViewModels as Projections of Shared State
* **Transient Projections for Shared State**: When projecting shared state owned by a service, ViewModels act as transient projections created when views open and disposed when views navigate away.
* **Projection Pipeline**:
  ```csharp
  // 1. Obtain raw, unsorted presentation model list from the authoritative State Service
  ObservableList<ChatMessageModel> presentationList = _chatState.GetOrAddSessionMessagesList(sessionId);

  // 2. Project presentation model -> viewmodel
  _messagesView = presentationList.CreateView(m => new ChatMessageViewModel(m)).AddTo(ref _bag);

  // 3. Bridge projection to the Avalonia UI collection
  Messages = _messagesView.ToNotifyCollectionChanged();
  ```
* **No Manual Re-querying**: Never subscribe to a "state mutated" signal just to trigger UI re-renders or clear/re-add collections. The `ISynchronizedView` automatically propagates item additions, removals, and updates.
* **Sorting & Filtering**: Domain and presentation collections remain in arrival/causal order. Filtering and sorting are view-layer concerns handled via projected views or XAML collections.

### 2.4 R3 Reactive Programming Rules
* **Filter Early, Marshal Late**:
  Perform CPU-intensive operations (filtering, deduplication, JSON parsing, debounce, rate-limiting) on worker threads. Only marshal to the UI thread at the terminal step before binding:
  ```csharp
  sourceStream
      .Where(evt => evt.SessionId == currentSessionId)
      .Debounce(TimeSpan.FromMilliseconds(50), _timeProvider)
      .ObserveOn(AvaloniaSynchronizationContext.Current)
      .Subscribe(UpdateView)
      .AddTo(ref Bag);
  ```
* **Throttle the Firehose**: Real-time streaming events (network packet rates, DHT search hops, file chunk bitfields) must be sampled (e.g., `.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)`) to protect the UI thread from 60fps+ inundation.
* **Thread-Safety Invariant**: **Never enumerate UI-bound collections (`NotifyCollectionChangedSynchronizedViewList`) on background threads.** Iterating or querying UI-bound collections from a background thread will throw cross-thread exceptions. Background tasks must query the raw presentation collection or take a snapshot.

### 2.5 UI Presentation Snapshots
* Where background UI tasks (e.g., caching recent UI history, local window state, or export) require an immutable view of UI state, presentation models implement a `.ToSnapshot()` method returning an immutable `record` snapshot (e.g., `ChatMessageSnapshot`).
* Background operations consume these immutable records, keeping live reactive properties free from lock contention.

### 2.6 Disposal & Memory Leak Prevention
* **The `CreateView` Removal Trap**: When an item is removed from an `ObservableList`, `ISynchronizedView` removes it from the projected view, but **does not dispose** the projected ViewModel.
* **Mandatory Removal Subscription**:
  ```csharp
  _messagesView.ObserveRemove()
      .Subscribe(e => e.Value.View.Dispose())
      .AddTo(ref Bag);
  ```
* Every ViewModel holding reactive subscriptions or views must inherit from `ViewModelBase` and dispose its `DisposableBag`.

### 2.7 Virtualized Time Provider
* Replace all `Task.Delay` and real-time timers with `System.TimeProvider`.
* Enables instantaneous, deterministic unit and UI testing via `FakeTimeProvider` without waiting for real wall-clock delays.

### 2.8 Hybrid Iconography Architecture (Fluent Icons + Nerd Font)
* **The Challenge**: FluentAvalonia includes modern Windows 11 Segoe Fluent Icons (`SymbolIcon`, `SymbolIconSource`) for general desktop controls (Chat, Settings, Search, Attachments, Folders). However, decentralized peer-to-peer networks require specialized technical iconography:
  - Route topologies: Direct P2P Route (`0xF0B45`) vs. Relayed Route (`0xF50F`).
  - Swarm metrics: Network Mesh (`0xEF09`), Latency meter (`0xF0128`), Tree Graph routing (`0xF104A`), Cloud Lock (`0xF11F1`).
* **The Idiomatic Avalonia Solution**:
  1. **Cross-Platform Font Embedding**: Embed `FiraCodeNerdFontMono-Regular.ttf` as an Avalonia resource (`avares://Percolator.Desktop/Assets/Fonts/FiraCodeNerdFontMono-Regular.ttf#FiraCode Nerd Font Mono`). This guarantees identical rendering on Windows, Linux, and macOS without relying on host OS font installations.
  2. **Strongly Typed `NerdGlyph` Enum**: Replace error-prone raw hex strings (`\uF50F`) with a C# enum (`NerdGlyph.RelayedRoute`, `NerdGlyph.DirectRoute`, `NerdGlyph.Latency`).
  3. **Custom `NerdIcon` and `NerdIconSource`**: Subclass FluentAvalonia's `FontIcon` and `FontIconSource`. This seamlessly plugs into FluentAvalonia's `NavigationViewItem.IconSource`, `CommandBarButton.IconSource`, and `ContentDialog.IconSource`, while defaulting the font family to the embedded Nerd Font.
  4. **Hybrid Usage Rule**: Use FluentAvalonia `SymbolIcon` for universal UX actions; use `NerdIcon` for domain-specific P2P mesh status, routing cards, and swarm telemetry.

### 2.9 Custom Controls & UI Primitives Evaluation (Angular-Material vs. FluentAvalonia)
In `Desktop.Wpf`, custom Angular-Material-inspired controls (`MatButton`, `MatSlideToggle`, `MatInput`, `MatMenu`, `MatChip`, `InitialsAvatar`, `ChatComposer`) were built because classic WPF lacked modern controls. Moving to Avalonia + FluentAvalonia changes the landscape:

* **What Is Obsolete (FluentAvalonia provides natively)**:
  - `MatSlideToggle` → Replaced by built-in Avalonia `<ToggleSwitch />` (supports animated slide thumb, accessibility, `OnContent`/`OffContent`).
  - `MatInput` → Replaced by built-in Avalonia `<TextBox UseFloatingWatermark="True" Classes="clearButton" />` (built-in floating labels, clear buttons, inner icons).
  - `MatMenu` → Replaced by FluentAvalonia `MenuFlyout` and `CommandBarFlyout` (Win11 fluent flyouts, rounded geometry, shortcut support).
  - Standard button variants → Replaced by FluentAvalonia styles (`Classes="accent"`, hyper-link buttons, icon buttons).

* **High-Value Controls to Port and Refactor**:
  1. **`BadgedButton` / Notification Badge Attached Behavior**:
     - *WPF Behavior*: `MatButton` handled `NotificationCount`, `HasNotification`, and a `BadgePulseStoryboard` that smoothly pulsed an indicator dot when notifications arrived (e.g. pending handshake invitations or unread messages).
     - *Avalonia Port*: Implement as an attached behavior or templated control (`Badge.Count`, `Badge.Pulse`) applicable to any button or icon.
  2. **`PeerAvatar` (replacing `InitialsAvatar`)**:
     - *WPF Behavior*: Rendered initials with a presence dot (online/offline).
     - *Avalonia Port*: Extend with multi-state P2P presence (Online = green, Relayed = orange, Direct = cyan, Offline = gray) and deterministic background color palettes computed from peer public key hashes.
  3. **`StatusChip` (replacing `MatChip`)**:
     - *WPF Behavior*: Compact status pill combining an icon and text for connection routes.
     - *Avalonia Port*: Reusable templated chip with `IconSource`, `Text`, and status color brushes (Direct P2P, Relayed, STUN punch, Swarm healthy).
  4. **`ChatComposer`**:
     - *WPF Behavior*: Chat input box handling auto-expansion, Enter-to-send vs Shift+Enter-newline, and send commands.
     - *Avalonia Port*: Retain as a dedicated feature control encapsulating chat keyboard ergonomics and attachment flyouts.

### 2.10 Privacy-First Architecture & Stealth Mode Startup Invariant
Privacy and zero unauthorized disclosure are core architectural requirements:

* **Zero Network Fingerprinting On Startup (Total Stealth Mode)**:
  - When `Percolator.Desktop` starts, **it does not bind, listen on, broadcast to, or respond on any network port** (DHT UDP ports, STUN client probes, gRPC listener sockets, direct P2P TCP/QUIC transports).
  - The node is completely dark and invisible to LAN scanners, port sniffers, or WAN probes. Launching the application leaks zero network presence.
* **The First Screen: Identity Gatekeeper (`IdentityGateView`)**:
  - The first screen displayed upon application launch is the **Identity Gatekeeper**.
  - Users are presented with their local identities and can:
    1. Select which identity or identities to activate for this session.
    2. Create a new cryptographic identity (Ed25519/X25519 keypair) directly from the screen.
    3. Import an existing identity seed phrase.
    4. Unlock encrypted keystores using their passphrase/PIN.
* **Gated Network Activation (`NetworkStealthGate`)**:
  - Network subsystems and listener transports remain strictly uninitialized until the user has chosen and confirmed **at least one active identity**.
  - Only upon explicit user confirmation does the application:
    1. Activate the selected identity context (`ActiveIdentityContext`).
    2. Bind the network listeners and bootstrap DHT discovery for that identity.
    3. Transition the UI into the main shell (`MainWindow`).
* **Session Teardown & Lock**:
  - If the user explicitly locks the client or deactivates all active identities, all listening sockets immediately close and the client reverts to total stealth mode.

### 2.11 LRU-Cached DI Session Scopes (`SessionScopeFactory`)
* **The Problem**: In a messenger with dozens or hundreds of peer chats, creating a global singleton `ChatViewModel` for every conversation consumes excessive RAM and keeps hundreds of background reactive streams active. Conversely, recreating the entire ViewModel from scratch every time a user switches tabs wipes uncommitted message drafts and scroll positions.
* **The Solution**: An LRU-cached session scope manager (`SessionScopeFactory` with capacity 3–5):
  - When switching between active chats (Alice → Bob → Alice), the child `IServiceScope`, its `SessionContext`, and its `ChatViewModel` remain hot in memory, preserving drafts, scroll offsets, and active observables.
  - When a 6th conversation is opened, the least recently used session scope is evicted and disposed (`scope.Dispose()`), freeing memory and unsubscribing its `DisposableBag` cleanly.

### 2.12 Reactive Debounced Reload Coordinators (`ChatReloadCoordinator`)
* **The Problem**: Rapid bursts of incoming events (multiple chat messages in a second, delivery receipts, read confirmations, background sync ticks) can trigger rapid-fire database queries, causing thread pool contention, SQLite lock collisions, and UI stutter.
* **The Solution**: The Reload Coordinator pattern:
  ```csharp
  _reloadTrigger
      .Debounce(TimeSpan.FromMilliseconds(50), _timeProvider)
      .SelectAwait(async (trigger, ct) =>
      {
          await ReloadFromDatabaseAsync(trigger.SessionId, ct);
          return Unit.Default;
      }, AwaitOperation.Drop)
      .Subscribe()
      .AddTo(ref _bag);
  ```
  - `AwaitOperation.Drop` guarantees that while a database read is currently in-flight, redundant intermediate requests are dropped. Once the current read completes, the latest debounced trigger captures all recent database commits in a single roundtrip.

### 2.13 Connection & Handshake Lifecycle Orchestration
* `Desktop.Wpf` established complete workflows for P2P connection establishment:
  1. **Out-of-Band Invitation Tokens**: Sharing base64/QR code tokens containing signed identity keys, decoded via `DecodeAndQueueInviteCommand`.
  2. **Multi-Route Handshake Initiation**: Selecting between Direct IP/Port vs. establishing a tunnel through a chosen Relay Host (`ConnectViaNetworkCommand`).
  3. **Pending Inbound Invitations Queue**: Inbound handshake requests appear in a pending menu (`PendingHandshakesMenuViewModel`) allowing the user to Accept, Reject, or Burn invitations, returning structured results (`Accepted`, `RejectedExpired`, `RejectedInvalid`).
  4. **Dynamic Relay Discovery**: Discovered peers that support acting as relay nodes are dynamically queried and populate the relay selection dropdown.

### 2.14 Window Lifecycle & Child DI Containers (`WindowManager`)
* Secondary windows (Settings, Diagnostics, Connection Management) must not be treated as haphazard modal popups:
  - **Single-Instance Focus**: Calling `ShowFor<TViewModel>()` checks if an existing window of that type is already open and brings it to the foreground rather than opening duplicate windows.
  - **Child DI Scope Isolation**: Each window is spawned within its own child `IServiceScope`. When the window is closed, its DI scope is immediately disposed, preventing memory leaks from lingering window services.

### 2.15 Global Reactive Exception Plumbing
* In R3, unhandled exceptions inside reactive streams do not always surface on the standard dispatcher thread; if unobserved, they crash the host process.
* `ObservableSystem.RegisterUnhandledExceptionHandler` must be registered in `App.axaml.cs` alongside `TaskScheduler.UnobservedTaskException` and `AppDomain.CurrentDomain.UnhandledException`, piping errors into `ILogger` for diagnostic capture.

---

## 3. Technology Stack & Component Architecture

| Layer | Component | Description |
|---|---|---|
| **Presentation Framework** | Avalonia UI 11.2 | Hardware-accelerated, cross-platform XAML engine |
| **Theme & Controls** | FluentAvaloniaUI 2.1 | Win11 NavigationView, ContentDialog, CommandBar, BreadcrumbBar |
| **Privacy & Security** | `NetworkStealthGate` | Gated listener activation ensuring zero open ports until identity selection |
| **Iconography** | Segoe Fluent Icons + FiraCode Nerd Font | Hybrid: Fluent icons for standard UX, embedded Nerd Font for P2P mesh & route metrics |
| **Custom UI Primitives** | `PeerAvatar`, `StatusChip`, `BadgedButton`, `ChatComposer` | Domain-specific controls for peer presence, route badges, notification pulse |
| **Session Scoping** | `SessionScopeFactory` | LRU-cached child DI scopes for active conversation ViewModels |
| **Data Synchronization** | `ReloadCoordinator` | Debounced reactive query batching with `AwaitOperation.Drop` |
| **MVVM Tooling** | CommunityToolkit.Mvvm 8.4 | Source-generated `[ObservableProperty]`, `[RelayCommand]` |
| **Reactive Extensions** | R3 (v1.3) + ObservableCollections (v3.3) | Next-generation reactive pipelines and synchronized view lists |
| **App Shell & DI** | `Microsoft.Extensions.Hosting` | Dependency injection, configuration, logging, and hosted services |
| **Domain & App Core** | `Percolator.Domain`, `Percolator.Application`, `Percolator.PluginSdk` | Pure core messaging, cryptographic identities, session managers |
| **Platform Bridges** | `Percolator.Infrastructure.Windows` | Conditionally resolved at runtime for Windows-specific toast/winrt features |

---

## 4. UI Architecture & Directory Structure

```
Percolator.Desktop/
├── Assets/                        # Platform icons, branding
│   └── Fonts/                     # FiraCodeNerdFontMono-Regular.ttf
├── Common/
│   ├── Behaviors/                 # BadgeAttachedBehavior.cs (notification badges & pulse)
│   ├── Controls/                  # NerdIcon.cs, NerdIconSource.cs, PeerAvatar.axaml, StatusChip.axaml
│   ├── Converters/                # Value converters (AvatarColorConverter, StatusColor, RelativeTime)
│   ├── Extensions/                # R3 and Avalonia binding extensions
│   ├── Navigation/                # INavigationService.cs, NavigationService.cs
│   ├── Services/                  # DialogService, NetworkStealthGate
│   └── Windowing/                 # IWindowManager.cs, WindowManager.cs (child-scoped window lifecycle)
├── Features/
│   ├── IdentityGate/              # Privacy Gatekeeper: First screen before any network activation
│   │   ├── IdentityGateViewModel.cs
│   │   ├── IdentityGateView.axaml
│   │   └── IdentityCreationDialog.axaml
│   ├── Shell/                     # Main app shell (loaded only after identity is activated)
│   │   ├── ShellViewModel.cs
│   │   └── ShellView.axaml
│   ├── Chat/                      # Direct Messaging & Group Channels
│   │   ├── Controls/              # ChatComposer.axaml (auto-expand, keyboard send behavior)
│   │   ├── State/                 # ChatStateService, ChatReloadCoordinator.cs
│   │   ├── Models/                # ChatMessageModel, ChatMessageSnapshot (UI-only)
│   │   ├── ViewModels/            # ChatViewModel, ChatMessageViewModel
│   │   └── Views/                 # ChatView.axaml, MessageBubbleView.axaml
│   ├── Sessions/                  # Session routing, connection management, LRU scopes
│   │   ├── Scopes/                # ISessionScopeFactory.cs, SessionScopeFactory.cs (LRU cache)
│   │   ├── Dialogs/               # ConnectionManagementDialogView.axaml, ConnectionManagementDialogViewModel.cs
│   │   ├── Menus/                 # PendingHandshakesMenuView.axaml, PendingHandshakesMenuViewModel.cs
│   │   ├── State/                 # PeerConnectionStateService.cs, PeerConnectionReloadCoordinator.cs
│   │   └── ViewModels/            # SessionSidebarViewModel.cs, PeerConnectionListItemViewModel.cs
│   ├── Discovery/                 # Peer Discovery & Swarm Inspector
│   │   ├── State/                 # DiscoveryStateService
│   │   ├── Models/                # PeerNodeModel (UI-only)
│   │   ├── ViewModels/            # DiscoveryViewModel, PeerNodeItemViewModel
│   │   └── Views/                 # DiscoveryView.axaml
│   ├── FileTransfer/              # P2P Swarm File Sharing
│   │   ├── State/                 # FileTransferStateService
│   │   ├── Models/                # TransferProgressModel (UI-only)
│   │   ├── ViewModels/            # FileTransferViewModel, TransferItemViewModel
│   │   └── Views/                 # FileTransferView.axaml, BitfieldProgressControl.axaml
│   └── Settings/                  # Identity, Network, and Theme settings
│       ├── ViewModels/            # SettingsViewModel, IdentityProfileViewModel
│       └── Views/                 # SettingsView.axaml
├── ViewModels/
│   ├── ViewModelBase.cs           # ObservableObject + DisposableBag base
│   └── MainWindowViewModel.cs     # Shell window view model
├── Views/
│   ├── MainWindow.axaml           # FluentWindow shell with NavigationView
│   └── MainWindow.axaml.cs
├── App.axaml                      # Application styles and FluentAvalonia theme
├── App.axaml.cs                   # App bootstrap, R3 unhandled exception plumbing, DI host
├── Program.cs                     # Avalonia entry point
└── Percolator.Desktop.csproj
```

---

## 5. Implementation Roadmap & Milestones

### Milestone 1: Stealth Mode Startup, Identity Gatekeeper & Shell Lifecycle (Sprint 1)
- [x] Scaffold `Percolator.Desktop` with Avalonia, FluentAvalonia, CommunityToolkit.Mvvm, R3, ObservableCollections.
- [x] Configure `Program.cs`, `App.axaml`, and FluentAvalonia theme.
- [ ] Implement `ObservableSystem.RegisterUnhandledExceptionHandler` in `App.axaml.cs` to prevent silent reactive crashes.
- [ ] Implement `NetworkStealthGate` ensuring all network listener sockets remain unbound until identity confirmation.
- [ ] Implement `IdentityGateView.axaml` and `IdentityGateViewModel.cs` as the initial startup window:
  - Display available local identities with activation checkboxes.
  - "Create New Identity" wizard (generating Ed25519/X25519 keys via `Percolator.Domain`).
  - Passphrase unlock prompt for encrypted identity vaults.
- [ ] Wire user confirmation: only upon selecting >=1 active identity do network listeners bind and the main `MainWindow` shell open.
- [ ] Link `FiraCodeNerdFontMono-Regular.ttf` as an embedded resource and declare `NerdFontFamily`.
- [ ] Implement `NerdGlyph` enum, `NerdIcon` control, and `NerdIconSource` extending FluentAvalonia `FontIcon`.
- [ ] Implement `StatusChip` and `PeerAvatar` controls with presence dot and hash-color palette.
- [ ] Implement `BadgeAttachedBehavior` (notification count & pulse animation on buttons).
- [ ] Implement `IHost` configuration with `Microsoft.Extensions.Hosting` in `App.axaml.cs`.
- [ ] Implement `IWindowManager` with single-instance activation and child DI scope lifecycle.
- [ ] Configure `NavigationView` navigation routing in `MainWindow` with `Frame` page switching.
- [ ] Set up Dark/Light/HighContrast theme switching and OS accent color auto-detection.

### Milestone 2: Identity Management, Handshakes & Session Shell (Sprint 2)
- [ ] Port `IdentityStateService` and `ActiveIdentityContext` into UI state layer.
- [ ] Implement Identity Switching & Lock:
  - Allow adding or removing active identities from the session on the fly.
  - "Lock Client" action which tears down network sockets immediately and returns to `IdentityGateView`.
- [ ] Implement `SessionScopeFactory` with LRU eviction policy (capacity 3–5) for bounded memory usage.
- [ ] Implement `ConnectionManagementDialog`:
  - Out-of-band invitation token import/export (base64/QR code) via `DecodeAndQueueInviteCommand`.
  - Direct connection initiation vs. Relay host dropdown selection.
- [ ] Implement `PendingHandshakesMenu`:
  - Inbound invitation queue with Accept, Reject, and Burn actions.
- [ ] Implement `PeerConnectionReloadCoordinator` with debounced sync.
- [ ] Implement Contacts & Session list in the left navigation sidebar using `PeerAvatar` and `StatusChip`.

### Milestone 3: Real-Time Chat Experience (Sprint 3)
- [ ] Port `ChatStateService` managing session message queues and message history.
- [ ] Implement `ChatReloadCoordinator` with `50ms` debounce and `AwaitOperation.Drop` for burst SQLite writes.
- [ ] Build `ChatView.axaml` with virtualized message list (`ItemsRepeater` or `ListBox`).
- [ ] Implement `ChatComposer.axaml` control with auto-expand, Enter-to-send, and attachment hooks.
- [ ] Implement message projection with `_messagesView = domainList.CreateView(m => new ChatMessageViewModel(m))`.
- [ ] Add disposal handler `_messagesView.ObserveRemove().Subscribe(...)`.
- [ ] Add route indicator badge using `NerdIcon` & `StatusChip` (Direct P2P `DirectRoute` vs. Relayed via DHT node `RelayedRoute`).

### Milestone 4: Discovery & Network Swarm Inspector (Sprint 4)
- [ ] Port `DiscoveryStateService` subscribing to DHT lookup events.
- [ ] Implement live peer inspector displaying:
  - Discovered peer public keys, endpoints, and NAT traversal status (STUN/UPnP).
  - Active connection latency (ping roundtrip RTT with `NerdIcon Symbol="Latency"`).
  - DHT routing table bucket visualization.
- [ ] Throttled UI projection using `.Sample(16ms)` to handle DHT burst traffic without UI lag.

### Milestone 5: P2P File Transfer Dashboard (Sprint 5)
- [ ] Integrate with `Percolator.Apps.FileTransfer`.
- [ ] Build swarm transfer manager:
  - Active downloads and uploads list.
  - Chunk piece-map visualization (custom Avalonia control rendering piece bitfields).
  - Transfer speed meter (KB/s, ETA, Swarm peer count).
- [ ] Cross-platform drag-and-drop file sending using Avalonia `DragDrop` events.
- [ ] Native file picker integration using Avalonia `TopLevel.StorageProvider`.

### Milestone 6: Platform Integration & Polish (Sprint 6)
- [ ] System tray icon support via Avalonia `TrayIcon` (minimized to tray, background listening).
- [ ] Desktop notifications:
  - On Linux/macOS: Native Avalonia notification manager or FreeDesktop/D-Bus notifications.
  - On Windows: Conditionally link `Percolator.Infrastructure.Windows` for WinRT Toast notifications.
- [ ] Comprehensive UI test suite using `Avalonia.Headless.XUnit` and `FakeTimeProvider`.
- [ ] Performance profiling with JetBrains DotTrace / Avalonia Diagnostic tools.

---

## 6. Testing Strategy

1. **Unit Testing ViewModels**:
   * Test ViewModels headlessly without spinning up UI threads.
   * Inject `FakeTimeProvider` to test debounces, timers, and retry policies instantaneously.
   * Assert collection projection and removal disposal cleanly unregisters subscribers.
2. **Stealth Mode & Network Gate Verification**:
   * Verify that launching the application in test harnesses initiates zero TCP/UDP socket bindings before `IdentityGateViewModel.ConfirmActivation()` is invoked.
   * Verify immediate socket teardown upon session lock.
3. **Session LRU Scope Tests**:
   * Test that navigating across >5 conversations evicts and disposes the least recently used child scopes while keeping the newest 5 hot in memory.
4. **Headless UI Testing**:
   * Use `Avalonia.Headless.XUnit` to verify layout, command triggering, and input validation without opening native windows.
5. **State Service Concurrency Tests**:
   * Multi-threaded producer tests simulating concurrent gRPC and network message arrivals to guarantee no collection corruption.

---

## 7. Developer Tooling & Simulation Interoperability

Testing multi-peer network topologies, relays, and handshakes is handled by the standalone companion project **`Percolator.Simulator`** (see [`Percolator.Simulator/implementation-plan.md`](../Percolator.Simulator/implementation-plan.md)).

`Percolator.Desktop` contains **zero simulator code** or mock interceptors. To interoperate with the simulator during development, `Percolator.Desktop` supports a development flag:
* `Development:AllowLocalhostPeers: true` (or `--allow-localhost` CLI argument).
* When set, loopback `127.0.0.1` targets are permitted as valid peer endpoints without triggering production WAN stealth security rejections.
