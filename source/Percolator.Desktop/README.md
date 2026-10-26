# Percolator Desktop (`Percolator.Desktop`)

## 1. Overview & Architectural Scope

`Percolator.Desktop` is the cross-platform desktop client for the Percolator sovereign peer-to-peer messaging and file-sharing ecosystem, targeting **Windows, Linux, and macOS**. It replaces the legacy Windows-only WPF client (`Desktop.Wpf`) with a modern, hardware-accelerated presentation layer built on:

* **Avalonia UI (11.2+)**: Hardware-accelerated, cross-platform XAML layout and rendering engine.
* **FluentAvalonia (2.1+)**: Fluent Design System implementation providing native-feeling Windows 11 controls (`NavigationView`, `ContentDialog`, `CommandBar`, `MenuFlyout`, Acrylic/Mica backdrops) across all operating systems.
* **CommunityToolkit.Mvvm (8.4+)**: Source-generated, boilerplate-free MVVM (`[ObservableProperty]`, `[RelayCommand]`).
* **R3 (1.3.1+) & R3Extensions.Avalonia (1.3.1+)**: Zero-allocation reactive pipelines, custom Avalonia dispatcher time/frame providers via `.UseR3()`, and two-way UI bindings.
* **ObservableCollections.R3 (3.3.4+)**: High-throughput collection structures (`ObservableList<T>`, `ObservableDictionary<TKey, TValue>`) with synchronous collection projections for UI data binding.
* **.NET 10 (`net10.0`)**: Modern runtime performance and unified application hosting via `Microsoft.Extensions.Hosting`.

---

## 2. Core Architectural & Privacy Invariants

### 2.1 Two-Stage Container Lifecycle & Database Encryption Gate
Privacy and cryptographic isolation at rest are foundational to the client:
* **Unified Encrypted SQLite (`percolator.db`)**: All application data (identities, prekeys, ratchets, channels, messages, transfers, outbox) resides in a single database file encrypted with SQLCipher AES-256-CBC.
* **Two-Stage Container Architecture**:
  * **Bootstrap Container (Root DI)**: Configured at application startup in `App.axaml.cs`. Hosts only presentation and utility services (logging, window management, crypto engine, virtualized time, and `IDatabaseUnlockCoordinator`). **Zero SQLite DbContexts, repositories, or background sync workers exist in memory.**
  * **Authenticated Host (Child `IHost`)**: Instantiated strictly upon successful database decryption. Hosts `PercolatorDbContext`, connection pools, all 57 domain repositories/query services, and background workers.
* **Mandatory Initial Unlock (`UnlockWindow`)**:
  * The first screen presented on application launch.
  * **First-Time Setup**: If `percolator.db` does not exist, guides the user to establish a master password. A blank password (`""`) is supported, but triggers an explicit UI confirmation warning before database creation.
  * **Subsequent Launches (Explicit Unlock)**: Always presents an empty password field requiring the user to explicitly click "Unlock" (even when configured with a blank password).
* **Canary Verification & Schema Migration**:
  * Password derivation runs via Argon2id (3 iterations, 64 MB RAM, 4 parallelism lanes).
  * Key validity is verified via canary query (`SELECT count(*) FROM sqlite_master;`).
  * On verification success, pending EF Core / SQLite migrations (`Database.MigrateAsync()`) execute immediately before any repositories are resolved.
* **Background Worker Auto-Start**:
  * The authenticated session encapsulates a .NET `IHost`. Calling `await session.StartAsync()` automatically discovers and starts all registered `IHostedService` implementations (message outbox dispatchers, swarm transfer workers, DHT pollers).
* **Window Handoff**:
  * On unlock, `UnlockWindow` closes and `MainWindow` is resolved from `session.Services` and shown.
* **Emergency Duress Passphrase & Decoy State**:
  * Users can optionally establish a non-empty emergency duress passphrase distinct from the master password.
  * Entering this passphrase triggers cryptographic shredding of the database and twin-slot header, followed by a seamless transition to a clean, uninitialized first-launch setup state (decoy state) with zero forensic alerts or crash signatures.
* **In-Session Secure Lock & Relock**:
  * A prominent "Lock" button in the shell header allows the user to lock the session at any time.
  * Locking performs an orderly security teardown: stops `IHost` (`IHostedService` workers terminate gracefully), drops all SQLite connection pools (`SqliteConnection.ClearAllPools()`), zeroizes the 256-bit master key span in memory via `CryptographicOperations.ZeroMemory`, disposes the authenticated `IHost`, closes `MainWindow`, and reopens `UnlockWindow` from the root bootstrap container.

### 2.2 Total Network Stealth Startup Invariant
Privacy and zero unauthorized network disclosure are strictly enforced:
* **Zero Network Fingerprinting on Startup**: When `Percolator.Desktop` launches, **it does not bind, listen on, broadcast to, or probe any network port** (no UDP DHT packets, no STUN client probes, no gRPC listener sockets, no P2P TCP/QUIC transports). The application remains completely dark and invisible to LAN scanners, port sniffers, and WAN adversaries until identity selection.
* **Identity Selection Gate (`IdentityGateWindow`)**: Once the database is unlocked, users select which local cryptographic persona(s) to activate or create new Ed25519/X25519 identity keypairs.
* **Gated Network Activation (`INetworkStealthGate`)**: Network subsystems, gRPC listeners, and DHT bootstrapping bind **strictly after** the user confirms identity activation.
* **Instant Stealth Session Lock**: Locking the session or deactivating all active identities immediately terminates all listening sockets, tears down active peer streams, and returns the application to total network stealth.

### 2.3 Domain Purity & UI Presentation Isolation
* **Strict Domain Boundary**: `Percolator.Domain` is strictly isolated from UI dependencies. It contains zero UI interfaces, zero dispatcher invocations, and zero UI-specific snapshotting or freezing methods. Domain aggregates enforce pure business rules and cryptographic transitions.
* **Presentation-Owned Reactive Models**: All models required for UI reactivity, collection synchronization, and thread-safe binding (`ChatMessageModel`, `PeerSessionSummaryModel`, `DiscoveredPeerModel`, `FileTransferTaskModel`, `TimelineCardModel`) live exclusively in `Percolator.Desktop.Features.*.Models`.\n* **Immutable UI Presentation Snapshots**: Where background tasks (search indexing, local history caching, export) require UI state, presentation models implement `.ToSnapshot()` returning immutable C# `record` types (`ChatMessageSnapshot`, `TransferBitfieldSnapshot`), eliminating lock contention on live reactive properties.

### 2.4 State Ownership: Local ViewModel State vs. Shared UI State Services
* **Local State (Owned by ViewModels)**:
  * State private to a single view (e.g., composer text input, transient dialog forms, validation errors, accordion expansion, local search queries, scroll position) is authoritatively owned by the local ViewModel using `[ObservableProperty]` or `BindableReactiveProperty<T>`.
* **Shared State (Owned by Stateful UI Services)**:
  * Cross-cutting application state (active identity context, peer sessions, message queues, DHT discovery tables, active file transfer swarms) is owned exclusively by singleton/scoped **Stateful UI Services** (`IdentityStateService`, `PeerConnectionStateService`, `ChatStateService`, `DiscoveryStateService`, `FileTransferStateService`, `TimelineCardStateService`).
* **UI-Agnostic State Services**:
  * Stateful UI Services are **100% UI-agnostic**—they never interact with dispatchers, UI synchronization contexts, sorting, or filtering.
  * They execute mutations concurrently on background worker threads guarded by concurrency gates (`SemaphoreSlim`).
  * Services expose canonical `ObservableList<T>` / `ObservableDictionary<TKey, TValue>` collections and emit fine-grained mutation signals (`Observable<Unit>` or typed event streams).

### 2.5 Strict MVVM & Synchronized Collection Projections
* **ViewModels as Transient Projections**: ViewModels projecting shared state are transient constructs instantiated when views open and disposed when views close.
* **Projection Pipeline**:
  ```csharp
  // 1. Obtain raw, unsorted presentation list from the authoritative State Service
  ObservableList<ChatMessageModel> presentationList = _chatState.GetOrAddSessionMessagesList(sessionId);

  // 2. Project presentation model -> ViewModel
  _messagesView = presentationList.CreateView(m => new ChatMessageViewModel(m)).AddTo(ref Bag);

  // 3. Bridge projection to the Avalonia UI collection
  Messages = _messagesView.ToNotifyCollectionChanged();
  ```
* **No Manual Re-Querying**: Never subscribe to a generic "state changed" event to clear and re-populate collections. `ISynchronizedView` automatically propagates additions, removals, and replacements.
* **Sorting & Filtering**: Domain and presentation lists maintain canonical arrival/causal order. Filtering and sorting are view-layer concerns handled via projected views or XAML collections.

### 2.6 R3 Reactive Programming Rules & Avalonia Integration
* **`R3Extensions.Avalonia` Dispatcher Integration**:
  In `Program.cs`, the application calls `.UseR3()` on `AppBuilder`:
  ```csharp
  public static AppBuilder BuildAvaloniaApp()
      => AppBuilder.Configure<App>()
          .UsePlatformDetect()
          .WithInterFont()
          .LogToTrace()
          .UseR3(); // Configures AvaloniaDispatcherTimeProvider & AvaloniaDispatcherFrameProvider
  ```
  This automatically ties R3's default scheduling and frame-based loops to Avalonia's Dispatcher loop.
* **Filter Early, Marshal Late**:
  Perform CPU-intensive work (filtering, deduplication, JSON parsing, debounce, rate-limiting) on worker threads. Only marshal to the UI thread at the terminal step before binding:
  ```csharp
  sourceStream
      .Where(evt => evt.SessionId == currentSessionId)
      .Debounce(TimeSpan.FromMilliseconds(50), _timeProvider)
      .ObserveOn(AvaloniaSynchronizationContext.Current)
      .Subscribe(UpdateView)
      .AddTo(ref Bag);
  ```
* **Throttle the Firehose**: Real-time streaming feeds (DHT candidate pings, network packet metrics, swarm piece bitfields) must be sampled (`.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)`) to protect the UI thread from 60fps+ inundation.
* **Thread-Safety Invariant**: **Never enumerate UI-bound collections (`NotifyCollectionChangedSynchronizedViewList`) on background threads.** Iterating UI-bound collections from worker threads throws cross-thread exceptions. Background operations must query the raw `ObservableList<T>` or take an immutable snapshot.
* **Removal Disposal Pattern (`CreateView` Trap)**: When an item is removed from an `ObservableList`, `ISynchronizedView` removes it from the projected view but **does not dispose** the projected ViewModel. Subscriptions must explicitly observe removal:
  ```csharp
  _messagesView.ObserveRemove()
      .Subscribe(e => e.Value.View.Dispose())
      .AddTo(ref Bag);
  ```

### 2.7 Virtualized Time Provider
* Direct calls to `Task.Delay` and real-time timers are replaced with `System.TimeProvider`.
* Enables instantaneous, deterministic unit and UI testing via `FakeTimeProvider` without wall-clock sleep delays.

### 2.8 LRU-Cached DI Session Scoping (`SessionScopeFactory`)
* **Problem**: In a client with dozens of peer conversations, creating a global singleton `ChatViewModel` for every chat consumes excessive RAM and keeps hundreds of background reactive streams alive. Conversely, recreating ViewModels from scratch on tab switches destroys message drafts and scroll positions.
* **Solution**: An LRU-cached session scope manager (`SessionScopeFactory` with capacity 3–5):
  * Navigating between active chats (Alice → Bob → Alice) keeps the child `IServiceScope`, `SessionContext`, and `ChatViewModel` hot in memory.
  * When a 6th conversation is opened, the least recently used scope is evicted and disposed (`scope.Dispose()`), freeing memory and unsubscribing its `DisposableBag` cleanly.

### 2.9 Reactive Debounced Reload Coordinators (`ChatReloadCoordinator`)
* Rapid bursts of inbound events (multiple messages per second, read receipts, delivery confirmations, sync ticks) can trigger rapid-fire database queries, resulting in SQLite lock contention and UI stutter.
* The Reload Coordinator pattern debounces triggers and uses `AwaitOperation.Drop`:
  ```csharp
  _reloadTrigger
      .Debounce(TimeSpan.FromMilliseconds(50), _timeProvider)
      .SelectAwait(async (trigger, ct) =>
      {
          await ReloadFromDatabaseAsync(trigger.SessionId, ct);
          return Unit.Default;
      }, AwaitOperation.Drop)
      .Subscribe()
      .AddTo(ref Bag);
  ```
  `AwaitOperation.Drop` ensures that while a database read is in flight, redundant intermediate triggers are dropped. When the read completes, the latest debounced trigger captures all recent commits in a single roundtrip.

### 2.10 Window Lifecycle & Child DI Containers (`WindowManager`)
* Secondary windows (Settings, Diagnostics, Connection Management) enforce single-instance focus: calling `Show<TViewModel>()` activates an existing window rather than spawning duplicates.
* Each window is instantiated within its own child `IServiceScope`. When the window is closed, its DI scope is immediately disposed to prevent memory leaks.

### 2.11 Global Reactive Exception Plumbing
* Unhandled exceptions inside R3 reactive pipelines do not automatically surface on the UI thread; if unobserved, they crash the host process.
* `ObservableSystem.RegisterUnhandledExceptionHandler` is registered in `App.axaml.cs` alongside `TaskScheduler.UnobservedTaskException` and `AppDomain.CurrentDomain.UnhandledException`, piping errors into `ILogger`.

---

## 3. UI Primitives, Iconography & Control Architecture

### 3.1 Custom Controls & UI Primitives Evaluation (WPF vs. Avalonia)

Moving from classic WPF to Avalonia + FluentAvalonia simplifies the custom control footprint by adopting native platform capabilities:

| Legacy WPF Custom Control | Modern Avalonia / FluentAvalonia Equivalent | Architectural Rationale |
|---|---|---|
| `MatSlideToggle` | Built-in Avalonia `<ToggleSwitch />` | Native animated slide thumb, accessibility support, and `OnContent`/`OffContent` templates. |
| `MatInput` | Built-in Avalonia `<TextBox UseFloatingWatermark="True" Classes="clearButton" />` | Native floating watermark labels, clear buttons, and inner icon slots. |
| `MatMenu` | FluentAvalonia `MenuFlyout` & `CommandBarFlyout` | Win11 fluent flyouts with rounded geometry, elevation shadow, and keyboard shortcuts. |
| Custom Button Variants | FluentAvalonia Button Styles | Native styles: `Classes="accent"`, hyper-link buttons, icon buttons. |
| `BadgedButton` | `Badge` Attached Behavior (`Common/Behaviors/Badge.cs`) | Attached properties (`Badge.Count`, `Badge.Pulse`) applicable to any button or icon with pulse animations. |
| `InitialsAvatar` | `PeerAvatar` Control (`Common/Controls/PeerAvatar.axaml`) | Multi-state presence dot (Online, Relayed, Direct, Offline) + deterministic background brush generated from public key hashes. |
| `MatChip` | `StatusChip` Control (`Common/Controls/StatusChip.axaml`) | Reusable status pill combining an icon, label, and semantic brushes (Direct P2P, Relayed, STUN punch, Swarm healthy). |
| Custom Chat Composer | `ChatComposer` Control (`Features/Chat/Controls/ChatComposer.axaml`) | Encapsulates auto-expanding input (1–6 lines), Enter-to-send vs Shift+Enter-newline, and attachment/reaction flyouts. |
| Bitfield Grid | `PieceMapControl` (`Features/FileTransfer/Controls/PieceMapControl.axaml`) | Custom control rendering 10,000+ chunk bitfields directly on a `DrawingContext` without XAML visual element overhead. |

### 3.2 Hybrid Iconography Architecture (Fluent Icons + Nerd Font)

* **The Problem**: FluentAvalonia includes Segoe Fluent Icons (`SymbolIcon`, `SymbolIconSource`) for standard desktop actions (Chat, Settings, Search, Folders). However, decentralized P2P networks require specialized technical iconography:
  * Route topologies: Direct P2P Route (`0xF0B45`) vs. Relayed Route (`0xF50F`).
  * Swarm metrics: Network Mesh (`0xEF09`), Latency meter (`0xF0128`), Tree Graph routing (`0xF104A`), Cloud Lock (`0xF11F1`).
* **The Solution**:
  1. **Cross-Platform Font Embedding**: Embed `FiraCodeNerdFontMono-Regular.ttf` as an Avalonia resource (`avares://Percolator.Desktop/Assets/Fonts/FiraCodeNerdFontMono-Regular.ttf#FiraCode Nerd Font Mono`). Guarantees identical rendering across Windows, Linux, and macOS without host OS font dependencies.
  2. **Strongly Typed `NerdGlyph` Enum**: Replaces raw hex literals with typed symbols (`NerdGlyph.RelayedRoute`, `NerdGlyph.DirectRoute`, `NerdGlyph.Latency`, `NerdGlyph.MeshNetwork`, `NerdGlyph.Swarm`, `NerdGlyph.Lock`).
  3. **Custom `NerdIcon` and `NerdIconSource`**: Subclasses FluentAvalonia's `FontIcon` and `FontIconSource`, defaulting the font family to the embedded Nerd Font while plugging into `NavigationViewItem.IconSource`, `CommandBarButton.IconSource`, and `ContentDialog.IconSource`.
  4. **Usage Rule**: Use FluentAvalonia `SymbolIcon` for universal UX actions; use `NerdIcon` for domain-specific P2P mesh status, routing indicators, and swarm telemetry.

---

## 4. Workflows & Lifecycle Orchestration

### 4.1 Connection & Handshake Lifecycle
1. **Out-of-Band Invitation Tokens**: Sharing Base64 or QR code tokens containing signed identity keys, decoded via `DecodeAndQueueInviteCommand`.
2. **Multi-Route Handshake Initiation**: Selecting between Direct IP/Port dial vs. establishing a tunnel through a chosen Relay Node (`ConnectViaNetworkCommand`).
3. **Pending Inbound Invitations Queue**: Inbound handshake requests appear in a pending menu (`PendingHandshakesMenuViewModel`) allowing the user to Accept, Reject, or Burn (blacklist) invitations.
4. **Dynamic Relay Discovery**: Discovered peers that support acting as relay nodes are queried dynamically to populate the relay selection dropdown.

### 4.2 Modular Plugin & Timeline Integration (`Percolator.PluginSdk`)
`Percolator.Desktop` provides concrete implementations of the Plugin SDK presentation interfaces:
* **`DesktopTimelinePostSink`**: Implements `ITimelinePostSink`, allowing background plugins (`Percolator.Apps.*`) to publish, update, and remove interactive cards in the user's Timeline notification center.
* **`DesktopPluginActionHandlerDispatcher`**: Implements `IPluginActionHandler`, dispatching card button clicks back to the target plugin's registered action handler.

---

## 5. Developer Tooling & Simulation Interoperability

Testing multi-peer network topologies, relays, and handshakes is handled by the standalone companion project **`Percolator.Simulator`** (see [`Percolator.Simulator/implementation-plan.md`](../Percolator.Simulator/implementation-plan.md)).

* `Percolator.Desktop` contains **zero simulator code** or mock interceptors.
* To interoperate with the simulator during development, `Percolator.Desktop` supports a development configuration flag:
  ```json
  {
    "Development": {
      "AllowLocalhostPeers": true
    }
  }
  ```
  (or `--allow-localhost` CLI argument).
* When enabled, loopback `127.0.0.1` targets are permitted as valid peer endpoints without triggering production WAN stealth security rejections, and a visual indicator is displayed in the title bar.

---

## 6. Directory Layout & Feature Slices

```
Percolator.Desktop/
├── Assets/                               # Platform icons, branding
│   └── Fonts/
│       └── FiraCodeNerdFontMono-Regular.ttf
├── Common/
│   ├── Behaviors/                        # Badge.cs (attached properties for count & pulse)
│   ├── Controls/                         # NerdGlyph.cs, NerdIcon.cs, NerdIconSource.cs, PeerAvatar, StatusChip
│   ├── Converters/                       # IdentityHashToBrushConverter, RouteToColorConverter
│   ├── Navigation/                       # INavigationService.cs, NavigationService.cs
│   ├── Services/                         # NetworkStealthGate, DesktopIdentityKeyStore, SystemDateTimeProvider
│   └── Windowing/                        # IWindowManager.cs, WindowManager.cs, IWindowHandoffCoordinator.cs, IDialogWindow.cs
├── Features/
│   ├── Unlock/                           # Database Decryption & Master Passphrase Gate (First Screen)
│   │   ├── Models/                       # UnlockStateModel.cs
│   │   ├── ViewModels/                   # UnlockViewModel.cs
│   │   └── Views/                        # UnlockWindow.axaml
│   ├── IdentityGate/                     # Total Network Stealth Gatekeeper
│   │   ├── Models/                       # SelectableIdentityModel.cs
│   │   ├── ViewModels/                   # IdentityGateViewModel.cs, CreateIdentityDialogViewModel.cs
│   │   └── Views/                        # IdentityGateWindow.axaml, CreateIdentityDialog.axaml
│   ├── Shell/                            # Main Application Shell
│   │   ├── ViewModels/                   # MainWindowViewModel.cs
│   │   └── Views/                        # MainWindow.axaml
│   ├── Sessions/                         # Peer Sessions, Handshakes & Connection Management
│   │   ├── Models/                       # PeerSessionSummaryModel.cs, PendingHandshakeModel.cs
│   │   ├── Services/                     # IPeerConnectionStateService.cs, SessionScopeFactory.cs
│   │   ├── ViewModels/                   # SessionsSidebarViewModel.cs, ConnectionManagementDialogViewModel.cs
│   │   └── Views/                        # SessionsSidebarView.axaml, ConnectionManagementDialog.axaml
│   ├── Chat/                             # Real-Time Messaging & Channels
│   │   ├── Controls/                     # ChatComposer.axaml, ReactionPickerFlyout.axaml
│   │   ├── Models/                       # ChatMessageModel.cs, ChatMessageSnapshot.cs
│   │   ├── Services/                     # IChatStateService.cs, ChatReloadCoordinator.cs
│   │   ├── ViewModels/                   # ChatViewModel.cs, ChatMessageViewModel.cs
│   │   └── Views/                        # ChatView.axaml
│   ├── Discovery/                        # Sovereign Peer Discovery & Telemetry
│   │   ├── Models/                       # DiscoveredPeerModel.cs, BlindedRendezvousModel.cs
│   │   ├── Services/                     # IDiscoveryStateService.cs
│   │   ├── ViewModels/                   # DiscoveryViewModel.cs
│   │   └── Views/                        # DiscoveryView.axaml
│   ├── FileTransfer/                     # P2P Swarm File Sharing
│   │   ├── Controls/                     # PieceMapControl.axaml (direct canvas bitfield renderer)
│   │   ├── Models/                       # FileTransferTaskModel.cs, TransferBitfieldSnapshot.cs
│   │   ├── Services/                     # IFileTransferStateService.cs
│   │   ├── ViewModels/                   # FileTransferViewModel.cs
│   │   └── Views/                        # FileTransferView.axaml
│   ├── Timeline/                         # Plugin Timeline & Card Host
│   │   ├── Models/                       # TimelineCardModel.cs, TimelineActionModel.cs
│   │   ├── Services/                     # ITimelineCardStateService.cs, DesktopTimelinePostSink.cs
│   │   ├── ViewModels/                   # TimelineViewModel.cs, TimelineCardViewModel.cs
│   │   └── Views/                        # TimelineView.axaml
│   └── Settings/                         # Identity, Network & Display Settings
│       ├── ViewModels/                   # SettingsViewModel.cs
│       └── Views/                        # SettingsView.axaml
├── ViewModels/
│   └── ViewModelBase.cs                  # ObservableObject + DisposableBag base class
├── App.axaml                             # Application styles and FluentAvalonia theme
├── App.axaml.cs                          # App bootstrap, R3 unhandled exceptions, DI composition root
├── Program.cs                            # Avalonia entry point with .UseR3()
├── implementation-plan.md                # 9-chunk implementation plan
├── README.md                             # Architectural specification & guide
└── Percolator.Desktop.csproj
```
