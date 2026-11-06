# Percolator.Simulator Implementation Plan

> [!NOTE] Non-Regulatory Tooling Disclaimer
> `Percolator.Simulator` is an engineering developer tool, interactive QA "monkey testing" sandbox, and automated scenario verification harness. It is **NOT** part of the formal medical device QMS (FDA 21 CFR 820.30 / IEC 62304) software item deliverables and does not contain `SYS-*` or `DEP-*` regulatory tags. Its requirements and design specifications serve internal engineering quality, protocol validation, and verification rigor.

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved — Pragmatic MVVM / Domain Reuse)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved — N/A for Local Loopback QA Harness)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved — Scenario Replay & State Verification)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved — Consumes Existing Infrastructure)
- [x] Implementation Gate: UNLOCKED (All lenses approved — ready for Phase 1 implementation)

---

## 1. Scope & Architectural Boundaries

### 1.1 Core Mission: UI-First, Incrementally Verifiable QA Sandbox
`Percolator.Simulator` is a standalone **Avalonia UI** desktop application built with **FluentAvalonia**, **CommunityToolkit.Mvvm**, and **R3 (`ObservableCollections.R3`)**. It enables a QA tester or developer to spin up simulated identities and relays, and interactively test Direct Chat, Relayed Chat, Group Chat, Encrypted Profiles, Peer Discovery, and Swarm File Transfers side-by-side **without needing cryptographic training**.

Implementation follows a strict **UI-Shell-First, Log-Verified, Record-and-Ratchet Progression**:
1. **Zero Orphaned Stubs**: Every single button, toggle, and flyout stubbed in Phase 1 is explicitly mapped to a concrete implementation phase (Phases 2–9). When Phase 9 finishes, **zero non-functional buttons remain in the application**.
2. **Phase 1 Visual Preview & Lightweight Sub-Panel Shells**: Phase 1 builds the outer 4-zone shell, toolbar, action bar, roster tree, workbench container, and `PersonaCardView` with mock seed data, while keeping `RelayCardView` (Phase 5), `GroupPanelView` (Phase 7), `DiscoveryTabView` (Phase 8), and `SwarmPanelView` (Phase 9) as lightweight placeholder shells until their implementation phases.
3. Build a **Structured Logging & UI Log Viewer** subsystem in Phase 2 so both humans (in the UI) and AI (via rolling JSONL files and `stdout`) have full observability.
4. Wire up **Persona Hosting & Direct Chat** in two right-sized sub-phases: **Phase 3A** (`SimulatedPersonaHost` Kestrel/SQLite/TLS bootstrapping + identity spawning) and **Phase 3B** (Direct Handshake + 1-on-1 Chat + Autopilot Pong), then **STOP for human interactive verification**.
5. Wire up **Scenario Recording & Playback** in Phase 4 immediately after Direct Chat is approved, capturing a committed regression script (`Scenarios/01-direct-handshake-and-chat.json`).
6. **Mandatory Cumulative Regression Gate**: In every subsequent phase (Phases 5–9), the AI must replay **all previously committed scenario recordings** via `--replay` and verify zero regressions in the structured log before declaring the phase complete.

### 1.2 Explicit Non-Goals & Out-of-Scope Boundaries (Negative Scope)
To prevent speculative bloat and architectural drift, the following capabilities are strictly **out of scope**:
- **No Embedded HTTP/REST Control Daemon or Dual CLI**: The simulator does not host an ASP.NET Core Minimal API control server or a separate interactive CLI binary. All automated verification executes through the unified `--replay <path>` startup flag.
- **No Vault Passphrase / Argon2id Unlock Ceremony or SQLite Database Encryption**: Simulated personas store plain, unencrypted SQLite databases (`SimulatorSqlCipherPragmaInterceptor` skips `sqlite3_key` and PBKDF2 completely) to allow instant zero-overhead identity creation and direct database inspection in external SQLite tools.
- **No WAN NAT Traversal (UPnP / STUN) or Multi-Machine Network Bridging**: All simulated nodes and relays bind strictly to `127.0.0.1` loopback endpoints.
- **No Continuous Online Presence or Heartbeat Pings**: Personas act strictly as store-and-forward actors (Signal Protocol parity); muting ingress (`IsListening = false`) never emits "offline" presence notifications to peers or relays.
- **No Custom Protocol or Cryptographic Primitives**: The simulator must never invent mock encryption or custom wire framing; it exclusively orchestrates the real `Percolator.Domain`, `Percolator.Application`, `Percolator.Infrastructure`, `Percolator.Infrastructure.Windows` (on Windows), and `Percolator.Apps.*` assemblies.

### 1.3 Pragmatic Type Usage (Zero Ceremonial Wrapper Bloat)
- **Reuse Existing `Percolator.Domain` Value Objects**: Whenever calling into the Percolator stack, the simulator uses the existing domain types directly (`IdentityId`, `ChannelId`, `TransferId`, `EndpointAddress`, `BlindedRoutingToken`, `DiscoveryEpoch`, `RoutingPreference`).
- **Keep UI & ViewModel Types Ergonomic**: The simulator adds only `SimulatedNodeId` (`readonly record struct` wrapping `Guid`) to uniquely identify host containers. All Avalonia UI checkboxes (`IsListening`, `AutoAcceptHandshakes`, `AutoReplyPong`, `AutoReplenishPreKeys`, `AllowDiscovery`, `AllowFileTransfers`, `SendReadReceipts`), display names (`string`), scenario aliases (`string`), and Identicon PNG buffers (`byte[]`) use plain C# primitives and `ReactiveProperty<bool>` so XAML bindings work directly with zero boilerplate value converters.
- **Zero Invasion of Core Assemblies**: `Percolator.Simulator` never adds simulator-only properties or UI events to `Percolator.Domain` or `Percolator.Application`. All cross-host reactive observation is wired via Decorator classes inside the simulator project.

### 1.4 Strict MVVM State Ownership, Angular-Style Stateful Services & R3 Reactive Bus
Because `Percolator.Simulator` hosts multiple isolated DI containers (`SimulatedPersonaHost`, `SimulatedRelayHost`) whose databases are mutated asynchronously by background gRPC/TCP threads—and because core repositories (`ChatPayloadHandler`, `HandshakeService`, `SqliteRelayPreKeyRepository`) write directly to SQLite without raising UI events—the simulator enforces a **5-part Reactive & Background-Offloaded MVVM Architecture**:

1. **Strict ViewModel State Ownership (Local vs. Shared State)**:
   - **ViewModel-Owned State**: A ViewModel exclusively owns transient UI state that belongs strictly to its own view (`ComposeMessageText`, `SelectedDisappearingTimer`, `ActiveCardTab`, `IsFlyoutOpen`, `InlineEditingMessageId`). No external service or peer ViewModel may read or mutate ViewModel-local state.
   - **Angular-Style Stateful Services (Shared State)**: Whenever state is shared across multiple ViewModels (e.g., Roster Tree + Contextual Action Bar + Center Workbench + Flight Recorder), that state is owned by a dedicated singleton **Stateful Service** exposing **`R3.ReadOnlyReactiveProperty<T>`** and **`ObservableCollections.R3.ObservableList<T>`**:
     | Stateful Service | Slice Owner | Shared State Exposed (`R3` / `ObservableList`) | Consuming ViewModels |
     | :--- | :--- | :--- | :--- |
     | `ISelectionStateService` | `Features/Shell/` | `PrimarySelectedNode`, `SecondarySelectedNode`, `MultiSelectedNodes` | `RosterViewModel`, `ContextualActionBarViewModel`, `WorkbenchViewModel` |
     | `ISimulatedWorldStateService` | `Features/Topology/` | `Personas`, `Relays`, `Groups`, `ActiveSwarms`, `PinnedCards` | `RosterViewModel`, `WorkbenchViewModel`, `ContextualActionBarViewModel`, `ScenarioRunnerService` |
     | `IFlightRecorderStateService` | `Features/Scenarios/` | `IsRecording`, `RecordedSteps`, `ActiveReplayStatus` | `FlightRecorderViewModel`, `TopGlobalToolbarViewModel` |
     | `IVirtualClockStateService` | `Features/RichChatAndProfiles/` | `CurrentUtcNow`, `CurrentDiscoveryEpoch` | `TopGlobalToolbarViewModel`, `PersonaCardViewModel`, `DiscoveryTabViewModel` |
2. **Cross-Host Reactive Signal Bus (`ISimulatorStateBus`)**:
   - The core Percolator libraries do not emit UI events when background gRPC threads (`UnauthenticatedDeliveryEndpoint`, `RelayMailboxStreamWorker`, `ChatPayloadHandler`, `HandshakeService`) write to SQLite.
   - To keep the UI fresh without polling SQLite on timers, each node's DI container registers lightweight **Decorator hooks** around inbound/outbound ports (`SimulatorNotifyingAppRouterDecorator`, `SimulatorNotifyingHandshakeDecorator`, `SimulatorNotifyingRelayCapabilityStoreDecorator`, `SimulatorNotifyingRelayPreKeyRepositoryDecorator`).
   - Whenever any node's database or queue changes, the decorator publishes a typed `SimulatorStateSignal` (`NodeId`, `SignalCategory`: `PendingHandshakeChanged | ChatTranscriptChanged | RatchetSessionChanged | RelayQueueChanged | HostedPreKeyCountChanged | ProfileChanged | GroupStateChanged | SwarmProgressChanged`) to the singleton `ISimulatorStateBus` (`R3.Subject<SimulatorStateSignal>`).
3. **`R3` Reactive Pipelines, UI Coalescing & `DisposableBag` Lifecycle**:
   - ViewModels subscribe to `ISimulatorStateBus.Signals` filtered by their `SimulatedNodeId` using **R3 operators**:
     ```csharp
     stateBus.Signals
         .Where(s => s.NodeId == this.NodeId && s.Category == SignalCategory.ChatTranscriptChanged)
         .ThrottleLast(TimeSpan.FromMilliseconds(50)) // Coalesce bursts during 25-msg floods or swarm chunks
         .ObserveOnUIThreadDispatcher()               // Marshal to Avalonia UI thread
         .SubscribeAwait(async (_, ct) => await RefreshTranscriptFromQueryServiceAsync(ct))
         .AddTo(ref _disposables);
     ```
   - Every ViewModel implements `IDisposable` and manages all `R3` subscriptions via an `R3.DisposableBag _disposables`, ensuring zero memory leaks when cards are unpinned or `Reset World` is clicked.
4. **`System.Threading.Channels` for High-Throughput Background Pipelines**:
   - **`Channel<T>`** is used where producer-consumer queuing and backpressure make sense:
     - `Channel<SimulatorLogEntry>` in `MultiSinkLogEngine` (decoupling hot gRPC/Kestrel threads from disk JSONL I/O and UI ring-buffer ingestion).
     - `Channel<SwarmChunkProgressSignal>` in Phase 9 (coalescing high-frequency multi-stream TCP chunk completions into 30 FPS `SwarmHeatmapControl.InvalidateVisual()` passes).
   - **`R3` (`ReactiveProperty<T>`, `ObservableList<T>`, `Subject<T>`)** is used for all reactive state sharing, UI data binding (`CreateView().ToNotifyCollectionChanged()`), and deterministic `FirstAsync()` waiters in `ScenarioRunnerService`.
5. **Mandatory Background Thread Offloading (`UiTaskRunner`)**:
   - Because `Microsoft.Data.Sqlite` executes all native SQLite C-API calls synchronously on the calling thread (even inside `*Async` ADO.NET / EF Core methods), and because Signal cryptography (`libsignal`), SChannel X.509 certificate creation, and DI host bootstrapping are CPU-bound, **no ViewModel `[RelayCommand]` or `ISimulatorStateBus` signal handler may execute host, database, cryptographic, or network operations on the Avalonia UI thread**.
   - All ViewModel commands and orchestrators (`TopGlobalToolbarViewModel`, `ContextualActionBarViewModel`, `PersonaCardViewModel`, `DirectHandshakeOrchestrator`, `DirectChatCoordinator`, and all future Phase 4–9 coordinators) must offload execution to the `.NET ThreadPool` via `UiTaskRunner.RunAsync(...)` or `UiTaskRunner.FireAndForget(...)` and marshal observable UI state mutations back via `UiTaskRunner.PostToUi(...)`.

---

## 2. Complete UI Control Inventory & 100% Phase-Mapping Ledger

Every control in the simulator is listed below with its exact UI location, behavior, and target implementation phase. **No control may remain unbound when Phase 9 completes.**

### 2.1 Zone 1: Top Global Toolbar (`TopGlobalToolbarView`)
| Control ID | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :---: | :---: | :--- |
| `BTN-TOP-01` | `[ + Add Person ]` Button | Phase 1 | **Phase 3A** | Spawns 1 new simulated identity with auto-generated Word-Pair name, `Bitmap` Identicon, and loopback Kestrel server. |
| `BTN-TOP-02` | `[ + Bulk Add People ▼ ]` Flyout (`Count: 2..20`, `Relay: Optional`, `Publish PreKeys: Checkbox`) | Phase 1 | **Phase 5** | Spawns $N$ identities in batch, optionally registers their mailboxes on the selected relay, and optionally publishes initial PreKey bundles. |
| `BTN-TOP-03` | `[ + Add Relay ]` Button | Phase 1 | **Phase 5** | Spawns a new `SimulatedRelayHost` on an allocated loopback port (`UnauthenticatedDeliveryService`, `AuthenticatedRelayService`, `AnonymousGroupRelayService`). |
| `BTN-TOP-04` | `[ 🕒 +1 Min ]` Virtual Clock Button | Phase 1 | **Phase 6A** | Advances `VirtualizedTimeProvider` by 1 minute (triggers outbox retry backoffs and short disappearing message timers). |
| `BTN-TOP-05` | `[ 🕒 +1 Hour ]` Virtual Clock Button | Phase 1 | **Phase 6A** | Advances `VirtualizedTimeProvider` by 1 hour (triggers disappearing message evictions). |
| `BTN-TOP-06` | `[ 🕒 +24 Hours ]` Virtual Clock Button | Phase 1 | **Phase 8** | Advances `VirtualizedTimeProvider` by 24 hours (rotates `DiscoveryEpoch` and expires relay rendezvous registrations). |
| `BTN-TOP-07` | `[ 🔄 Reset World ]` Button | Phase 1 | **Phase 3A** | Disposes all active persona/relay hosts, releases loopback ports, clears UI state, and starts a fresh run sandbox directory. |

### 2.2 Zone 2: Contextual Selection & Directional Action Bar (`ContextualActionBarView`)
| Control ID | Selection State | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `BTN-ACT-01` | 2 People (`1️⃣ Initiator ➔ 2️⃣ Target`) | `[ ⇄ Swap Roles ]` Button | Phase 1 | **Phase 1** | Pure UI state swap: swaps the `1️⃣ Initiator` and `2️⃣ Target` identities in `ISelectionStateService`. |
| `BTN-ACT-02` | 2 People (`1️⃣ ➔ 2️⃣`) | `[ ⚡ Direct Handshake (1️⃣ ➔ 2️⃣) ]` Button | Phase 1 | **Phase 3B** | Initiator fetches Target's direct loopback `InvitationLink` and invokes `HandshakeService.InitiateHandshakeAsync` over `DirectP2P`. |
| `BTN-ACT-03` | 2 People + 1 Relay | `[ 📡 Relayed Handshake (1️⃣ ➔ 2️⃣ via Relay) ]` Button | Phase 1 | **Phase 5** | Initiator fetches Target's hosted `PreKeyBundle` from Relay via `FetchPreKeyBundle` and dispatches handshake via `EnqueueMailbox`. |
| `BTN-ACT-04` | 2+ People (`1️⃣ ➔ Targets`) | `[ 📁 Send File Preset ▼ ]` (`2 MB`, `25 MB`, `100 MB`) | Phase 1 | **Phase 9** | Generates deterministic synthetic file in Initiator's temp dir and calls `IFileTransferActionHandler.InitiateTransferAsync` to selected targets. |
| `BTN-ACT-05` | 2+ People + 1 Relay | `[ 📬 Bulk Register Mailboxes ]` Button | Phase 1 | **Phase 5** | Registers `BlindedRoutingToken`, `DeliveryToken`, and `DrainToken` for all selected people on the selected Relay. |
| `BTN-ACT-06` | 2+ People + 1 Relay | `[ 🔑 Bulk Publish PreKeys ]` Button | Phase 1 | **Phase 5** | Generates and uploads a batch of 25 One-Time PreKeys + Signed PreKey from each selected person to the selected Relay. |
| `BTN-ACT-07` | 2+ People + 1 Relay | `[ 👥 Create Group on Relay... ]` Flyout (`GroupName`, `AdminSelector`) | Phase 1 | **Phase 7** | Admin creates `GroupChannel.CreateGenesis`, commits `RelayGroupLedger` with ZK params to Relay, distributes SenderKeys, and sends group invites. |

### 2.3 Zone 3: Left Roster Tree (`RosterTreeView`)
| Control ID | Roster Item Type | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `CTL-ROS-01` | Person / Relay Row | `[ 📌 Pin to Workbench ]` Toggle Button | Phase 1 | **Phase 1** | Pure UI layout toggle: pins or unpins the selected Persona Card or Relay Card into the Center Split-Viewport Workbench (up to 4 cards). |
| `CTL-ROS-02` | Person Row | `[ 1️⃣ / 2️⃣ / ☑️ ]` Selection Clicks | Phase 1 | **Phase 1** | Left-click sets `1️⃣ Initiator`; Shift+Left-click sets `2️⃣ Target`; Checkbox toggles multi-select membership. |
| `CTL-ROS-03` | Person Row | `[ 🟢 Listening / ⏸️ Muted ]` Inline Toggle Pill | Phase 1 | **Phase 3A** | Toggles `IsListening`: when `Muted`, stops Kestrel/mailbox stream ingress without notifying peers; when `Listening`, reconnects and drains. |
| `CTL-ROS-04` | Group Row | Group Selection Click | Phase 1 | **Phase 7** | Opens the Group Channel Inspector & Broadcast View in the Center Workbench. |
| `CTL-ROS-05` | Swarm Row | Swarm Transfer Selection Click | Phase 1 | **Phase 9** | Opens the Swarm Piece Heatmap & Participant Progress Panel in the Center Workbench. |

### 2.4 Zone 4A: Persona Card in Center Workbench (`PersonaCardView`)
| Control ID | Card Section | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `CTL-PER-01` | Header | Avatar Identicon + Display Name + `[ 🎲 Reroll Profile ]` Button | Phase 1 | **Phase 6B** | Generates new Identicon PNG + display name revision, encrypts via `ProfileManager` + `IBlobCryptoService`, uploads to relay, and shares capability. |
| `CTL-PER-02` | Header | `[ 🟢 Listening / ⏸️ Muted ]` Switch | Phase 1 | **Phase 3A** | Bound to the same `IsListening` capability property as `CTL-ROS-03`. |
| `CTL-PER-03` | `[ ⚙️ Features ▼ ]` Flyout | `[x] Allow Peer Discovery` Checkbox | Phase 1 | **Phase 8** | Toggles `IsDiscoveryEnabled` (rejects/ignores discovery queries when unchecked). |
| `CTL-PER-04` | `[ ⚙️ Features ▼ ]` Flyout | `[x] Allow File Transfers` Checkbox | Phase 1 | **Phase 9** | Toggles `IsFileTransferEnabled` (auto-rejects incoming transfer offers when unchecked). |
| `CTL-PER-05` | `[ ⚙️ Features ▼ ]` Flyout | `[x] Send Read Receipts` Checkbox | Phase 1 | **Phase 6A** | Toggles automatic dispatch of `MarkAsReadCommand` when viewing incoming messages. |
| `CTL-PER-06` | `[ ⚙️ Features ▼ ]` Flyout | `[ ] Relay-Only Routing (Hide IP)` Checkbox | Phase 1 | **Phase 9** | Toggles `RoutingPreference` between `DirectPreferred` and `RelayOnly` for file transfers. |
| `CTL-PER-07` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Accept Handshakes` Checkbox | Phase 1 | **Phase 3B** | Automatically approves incoming pending handshakes via `ContactRequestCoordinator` + `HandshakeService`. |
| `CTL-PER-08` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Reply "Pong"` Checkbox | Phase 1 | **Phase 3B** | Automatically replies `"Pong #<N>"` to incoming chat messages, stepping the asymmetric Double Ratchet DH chain. |
| `CTL-PER-09` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Replenish PreKeys` Checkbox | Phase 1 | **Phase 5** | Automatically uploads a fresh PreKey batch to the home relay when hosted OPK count drops below watermark. |
| `CTL-PER-10` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Share Profile on Connect` Checkbox | Phase 1 | **Phase 6B** | Automatically sends `ShareProfileCapabilityCommand` when a contact request is approved. |
| `CTL-PER-11` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Accept Group Invites` Checkbox | Phase 1 | **Phase 7** | Automatically accepts incoming `PendingGroupInvitation` items. |
| `CTL-PER-12` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Accept File Transfers` Checkbox | Phase 1 | **Phase 9** | Automatically calls `AcceptTransferAsync` to the persona's temp download folder when an offer arrives. |
| `CTL-PER-13` | Relay Bar | `[ Relay: Dropdown ▼ ]` + `[ 📬 Register ]` + `[ 🔑 Publish PreKeys ]` | Phase 1 | **Phase 5** | Registers single persona's mailbox on selected relay and manually publishes a 25-OPK bundle. |
| `CTL-PER-14` | Pending Banner | Pending Handshake Row: `[ ✓ Accept ]` `[ ✕ Reject ]` `[ 🚫 Block ]` | Phase 1 | **Phase 3B** | Invokes `ApprovePendingHandshakeAsync`, `RejectPendingHandshakeAsync`, or `BlockAndRejectHandshakeAsync`. |
| `CTL-PER-15` | Pending Banner | Pending Group Invite Row: `[ ✓ Join Group ]` `[ ✕ Decline ]` | Phase 1 | **Phase 7** | Accepts or declines `PendingGroupInvitation` in `Percolator.Apps.Chat`. |
| `CTL-PER-16` | Pending Banner | Incoming File Offer Row: `[ ✓ Accept Download ]` `[ ✕ Decline ]` | Phase 1 | **Phase 9** | Calls `IFileTransferActionHandler.AcceptTransferAsync` or `CancelTransferAsync`. |
| `CTL-PER-17` | Chat Tab | Conversation Selector Dropdown (`Peer / Group`) | Phase 1 | **Phase 3B** (Direct) / **Phase 7** (Group) | Switches the active chat transcript displayed in the Persona Card. |
| `CTL-PER-18` | Chat Tab | Message Compose TextBox + `[ Send ]` Button | Phase 1 | **Phase 3B** (Direct) / **Phase 7** (Group) | Dispatches `SendMessageCommand` via `IChatService.SendMessageAsync`. |
| `CTL-PER-19` | Chat Tab | Disappearing Timer Selector (`Off`, `1m`, `1h`) on Compose Bar | Phase 1 | **Phase 6A** | Populates `ExpireAfterSeconds` on `SendMessageCommand`. |
| `CTL-PER-20` | Chat Message Row | Hover Actions: `[ 👍 React ]` `[ ✏️ Edit ]` `[ 🗑️ Delete ]` | Phase 1 | **Phase 6A** | Invokes `AddReactionCommand`, `EditMessageCommand`, or `DeleteMessageCommand` via `IChatService`. |
| `CTL-PER-21` | Discovery Tab | `[ 📍 Publish Blinded Locator ]` + `[ 🔍 Resolve Peer Locator ]` | Phase 1 | **Phase 8** | Invokes `IPrivateRendezvousService.PublishRendezvousAsync` and `ResolveRendezvousAsync`. |
| `CTL-PER-22` | Discovery Tab | `[ 📡 Query Nearby Peers ]` + Candidate `[ Connect ]` Button | Phase 1 | **Phase 8** | Invokes `INearbyPeerDiscoveryWorkflow.QueryNearbyPeersAsync` and `ConnectToDiscoveredPeerAsync`. |

### 2.5 Zone 4B: Relay Card & Group/Swarm Panels in Center Workbench
| Control ID | Card Type | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `CTL-REL-01` | Relay Card | Held Mailbox Envelope Row: `[ 👯 Duplicate ]` Button | Phase 1 | **Phase 5** | Clones the selected queued `MailboxEnvelope` in the relay queue to test client replay rejection. |
| `CTL-REL-02` | Relay Card | Held Mailbox Envelope Row: `[ ⚡ Flip 1 Bit ]` Button | Phase 1 | **Phase 5** | XORs 1 bit in the encrypted payload of the queued `MailboxEnvelope` to test client AEAD tamper rejection. |
| `CTL-REL-03` | Relay Card | Mailbox Queue Toolbar: `[ 🔀 Shuffle Queue Order ]` Button | Phase 1 | **Phase 5** | Randomizes the delivery order of held envelopes in the queue to test Double Ratchet out-of-order skipped keys. |
| `CTL-GRP-01` | Group Panel | `[ + Add Member ▼ ]` & Member Row `[ ✕ Evict ]` Buttons | Phase 1 | **Phase 7** | Calls `GroupChannel.AddMember` / `RemoveMember`, commits new epoch to `RelayGroupLedger`, and rotates SenderKeys. |
| `CTL-GRP-02` | Group Panel | `[ ✏️ Rename Group ]` + `[ 🌊 Flood 25 Messages ]` Buttons | Phase 1 | **Phase 7** | Renames group (advancing epoch) or dispatches 25 concurrent messages across group members. |
| `CTL-SWM-01` | Swarm Panel | Participant Row: `[ ⏸️ Pause ]` `[ ▶️ Resume ]` `[ ✕ Cancel ]` | Phase 1 | **Phase 9** | Calls `PauseTransferAsync`, `ResumeTransferAsync`, or `CancelTransferAsync` on `IFileTransferActionHandler`. |
| `CTL-SWM-02` | Swarm Panel | `[ ➕ Inject Late Downloader ▼ ]` (`Select Person`, `At %: 50%`) | Phase 1 | **Phase 9** | Registers a trigger (or immediately joins if already past 50%) for the selected person to join the active swarm. |
| `CTL-SWM-03` | Swarm Panel | `SwarmHeatmapCanvas` (Custom `DrawingContext` Piece Grid) | Phase 1 | **Phase 9** | Renders verified (green), in-flight (blue), missing (grey), and corrupted (red) chunk states for each participant. |

### 2.6 Zone 5: Right Flight Recorder Drawer & Bottom Log Dock
| Control ID | Panel | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `CTL-REC-01` | Flight Recorder | `[ 🔴 Record / ⏹️ Pause Recording ]` Toggle + `[ 🗑️ Clear Steps ]` | Phase 1 | **Phase 4** | Enables/pauses appending interactive UI actions to `IFlightRecorderStateService` or clears the current step list. |
| `CTL-REC-02` | Flight Recorder | `[ 💾 Save Scenario JSON... ]` + `[ 📂 Load & Replay JSON... ]` | Phase 1 | **Phase 4** | Saves recorded steps + assertions to a `.json` file or loads and executes a `.json` file through `ScenarioRunnerService`. |
| `CTL-LOG-01` | Bottom Log Dock | `Minimum Log Level` Dropdown (`Trace`, `Debug`, `Info`, `Warn`, `Error`) | Phase 1 | **Phase 2** | Forward-only ingestion filter: only future log events $\ge$ selected level are appended to the 500-item UI ring buffer. |
| `CTL-LOG-02` | Bottom Log Dock | `[ 🧹 Clear UI Log ]` Button | Phase 1 | **Phase 2** | Clears the currently visible items in the 500-item UI log ring buffer (does not erase `logs/simulator-latest.jsonl`). |

---

## 3. Right-Sized Phase Specifications

### 3.1 Phase 1: Application Shell, Core MVVM State Services & Preview Layout
Phase 1 establishes the visual shell, DI wiring, `R3` reactive bus, and Angular-style stateful services with a manageable XAML footprint.

1. **Project Dependencies, Legacy Template Cleanup & Multi-Target Styling**:
   - **Legacy Horizontal Folder Excise**: Delete the template-generated horizontal folders `Models/`, `ViewModels/`, `Views/`, and `ViewLocator.cs` from `Percolator.Simulator` (and remove `<Folder Include="Models\" />` from `Percolator.Simulator.csproj`) so 100% of code resides inside `Features/<SliceName>/`.
   - `Percolator.Simulator.csproj` multi-targets `net10.0` (Linux / cross-platform) and `net10.0-windows10.0.19041.0` (Windows), including `R3`, `ObservableCollections.R3`, `<ProjectReference Include="..\Percolator.Infrastructure\Percolator.Infrastructure.csproj" />`, and a conditional `<ProjectReference Include="..\Percolator.Infrastructure.Windows\Percolator.Infrastructure.Windows.csproj" />` for `net10.0-windows*` targets.
   - `App.axaml` configures FluentAvalonia styling (`avares://FluentAvaloniaUI/Styling/Styles.axaml`) and `DataTemplates` mapping ViewModels to Views.
2. **Core Shell Views & Right-Sized Sub-Panel Shells (`Features/Shell/`, `Features/Topology/`)**:
   - **Fully Laid Out in Phase 1**:
     - `MainWindow.axaml` / `MainWindowViewModel` (4-zone dock shell + non-modal status toast bar).
     - `TopGlobalToolbarView` / `TopGlobalToolbarViewModel` (`BTN-TOP-01`..`07`).
     - `ContextualActionBarView` / `ContextualActionBarViewModel` (`BTN-ACT-01`..`07`).
     - `RosterTreeView` / `RosterViewModel` (`CTL-ROS-01`..`05`).
     - `WorkbenchView` / `WorkbenchViewModel` (1–4 card responsive grid).
     - `PersonaCardView` / `PersonaCardViewModel` (`CTL-PER-01`..`20` chat & header controls).
   - **Lightweight Placeholder Shells in Phase 1** (fleshed out in their dedicated feature phases to keep Phase 1 fast and clean):
     - `RelayCardView` (fleshed out in Phase 5).
     - `GroupPanelView` (fleshed out in Phase 7).
     - `DiscoveryTabView` (fleshed out in Phase 8).
     - `SwarmPanelView` (fleshed out in Phase 9).
     - `FlightRecorderView` (fleshed out in Phase 4).
     - `LogDockView` (fleshed out in Phase 2).
3. **Stateful Services & Mock Preview Seed (`DesignDataService`)**:
   - Registers `SelectionStateService`, `SimulatedWorldStateService`, `SimulatorStateBus`, and `StatusToastService`.
   - Seeds 2 mock preview personas (`"Crimson Falcon"`, `"Azure Badger"`) and 1 mock relay (`"Relay-Alpha"`) on startup so the workbench layout, selection badges (`1️⃣`, `2️⃣`), role swapping (`BTN-ACT-01`), and card pinning (`CTL-ROS-01`) can be inspected immediately.
   - Any button scheduled for Phases 2–9 invokes `IStatusToastService.ShowPlaceholder(actionName, targetPhase)`.
4. **Phase 1 Acceptance Criteria**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings and 0 errors across both `net10.0` and `net10.0-windows10.0.19041.0`.
   - App launches into the 4-zone shell; pinning/unpinning cards and swapping `1️⃣`/`2️⃣` roles work immediately.

---

### 3.2 Phase 2: Multi-Sink Structured Logging & UI Log Dock
Phase 2 replaces the bottom log dock placeholder with the real `System.Threading.Channels`-backed logging engine.

1. **`MultiSinkLogEngine` & `NodeScopedLoggerProvider` (`Features/Logging/`)**:
   - Single `Channel.CreateBounded<SimulatorLogEntry>(5000)` background consumer writing concurrently to:
     - **Sink 1 (File)**: `logs/simulator-latest.jsonl` (`FileShare.ReadWrite`, `AutoFlush = true`).
     - **Sink 2 (Console)**: `Console.Out` formatted as `[HH:mm:ss.fff] [LEVEL] [Node] Category: Message`.
     - **Sink 3 (UI Ring Buffer)**: `ObservableList<SimulatorLogEntry>` capped at 500 entries, marshaled onto `Dispatcher.UIThread` in 50ms batches.
2. **Bottom Log Dock UI (`LogDockView` / `LogDockViewModel`)**:
   - Wires `CTL-LOG-01` (`Minimum Log Level` dropdown: forward-only ingestion filter) and `CTL-LOG-02` (`[ 🧹 Clear UI Log ]` button).
3. **Phase 2 Acceptance Criteria**:
   - Every toolbar/action click logs a structured entry visible in `LogDockView`, `stdout`, and `logs/simulator-latest.jsonl`.

---

### 3.3 Phase 3A: `SimulatedPersonaHost` Bootstrapping, Kestrel TLS & Identity Spawning
Phase 3A focuses exclusively on spinning up real isolated Percolator persona hosts with plain unencrypted SQLite and loopback Kestrel HTTP/2 TLS servers.

1. **Host Lifecycle & DI Container Isolation (`SimulatedPersonaHost`)**:
   - Each persona runs in an isolated `IServiceProvider` / `IHost` owning:
     - Per-node isolated **plain (unencrypted) SQLite database** (`VaultStorageOptions.BaseDirectory = _nodeTempDirectory`) using `SimulatorSqlCipherPragmaInterceptor` (`PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;` with zero `sqlite3_key` or SQLCipher encryption).
     - Full `AddAuthenticatedInfrastructure(...)` and `Percolator.Apps.Chat` registration, plus platform-aware overrides:
       - On Windows (`#if WINDOWS`): `AddWindowsInfrastructureOverrides()` with node-scoped `WindowsSChannelCertificateProvider` and `WindowsSparseDesktopFileStore`.
       - On Linux / Cross-Platform (`#else`): `InMemoryTransportCertificateProvider` (OpenSSL) and node-scoped `PortableDesktopFileStore`.
     - `NodeScopedLoggerProvider` (Phase 2) forwarding all `Percolator.*` logs at `LogLevel.Trace` to `ISimulatorLogSink` while filtering noisy framework categories (`Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `System.Net.Http`) to `LogLevel.Warning`.
     - Reactive `IsListening` (`ReactiveProperty<bool>`) capability toggle (`CTL-ROS-03` / `CTL-PER-02`).
2. **Procedural Name, Identicon & Loopback Kestrel Transport**:
   - `WordPairNameGenerator.Generate()` produces display names (`"Crimson Falcon"`).
   - `IdenticonGenerator.CreatePng(uint seed)` renders a symmetric 5×5 blocky avatar as a `64x64` PNG `byte[]` loaded into an Avalonia `Bitmap`.
   - `LoopbackPortAllocator` leases an available port on `127.0.0.1` and binds `KestrelPeerTransportServer` on `https://127.0.0.1:{port}` (HTTP/2 + TLS 1.3 using `WindowsSChannelCertificateProvider` on Windows and `InMemoryTransportCertificateProvider` on Linux, plus TOFU validation on `PeerGrpcChannelFactory`).
3. **Toolbar & Roster Wiring (`BTN-TOP-01`, `BTN-TOP-07`, `CTL-ROS-03`, `CTL-PER-02`)**:
   - Clicking `[ + Add Person ]` (`BTN-TOP-01`) clears Phase 1's `DesignDataService` mock preview seed, offloads `SimulatedPersonaHost` creation and startup to a background thread via `UiTaskRunner.RunAsync`, and pins its `PersonaCardViewModel` to the Workbench on the UI thread.
   - Clicking `[ 🔄 Reset World ]` (`BTN-TOP-07`) disposes all active hosts on a background thread via `UiTaskRunner.RunAsync`, releases loopback ports, and resets UI state.
   - Toggling `[ 🟢 Listening / ⏸️ Muted ]` (`CTL-ROS-03` / `CTL-PER-02`) asynchronously stops or restarts inbound Kestrel transport via `UiTaskRunner.FireAndForget` without blocking the UI thread or notifying peers.
4. **Phase 3A Acceptance Criteria**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors across both `net10.0` and `net10.0-windows10.0.19041.0`.
   - Clicking `[ + Add Person ]` spawns real personas with unique Identicons and Kestrel ports without freezing the UI, logging startup cleanly to the UI Log Dock and `logs/simulator-latest.jsonl`.

---

### 3.4 Phase 3B: Direct Handshake, 1-on-1 Chat, Reactive Decorators & Autopilot Pong
Phase 3B wires end-to-end direct handshakes, 1-on-1 Double Ratchet chat, and cross-host `ISimulatorStateBus` decorators on top of Phase 3A's hosts, then stops for human verification.

1. **Cross-Host Reactive Bus Decorators (`SimulatorNotifyingAppRouterDecorator`, `SimulatorNotifyingHandshakeDecorator`)**:
   - Registered inside `SimulatedPersonaHost` wrapping `IAppRouter`, `IHandshakeService`, and `IContactRequestCoordinator`.
   - Publishes `SimulatorStateSignal` (`PendingHandshakeChanged`, `RatchetSessionChanged`, `ChatTranscriptChanged`) to `ISimulatorStateBus` whenever background gRPC threads mutate SQLite.
2. **Direct Handshake Initiation & Approval (`BTN-ACT-02`, `CTL-PER-07`, `CTL-PER-14`)**:
   - `[ ⚡ Direct Handshake (1️⃣ ➔ 2️⃣) ]` (`BTN-ACT-02`) builds a direct `InvitationLink` for Target and calls `HandshakeService.InitiateHandshakeAsync` over `DeliveryRouteType.DirectP2P`.
   - Target's `PersonaCardViewModel` receives `PendingHandshakeChanged` via `R3` and displays the Pending Handshake banner (`CTL-PER-14`) with `[ ✓ Accept ]`, `[ ✕ Reject ]`, and `[ 🚫 Block ]`.
   - If Target has `[x] Auto-Accept Handshakes` (`CTL-PER-07`), Target automatically approves the request and completes X3DH session establishment.
3. **Direct 1-on-1 Chat & Autopilot Pong (`CTL-PER-17`, `CTL-PER-18`, `CTL-PER-08`)**:
   - Sending a message in `CTL-PER-18` invokes `IChatService.SendMessageAsync`.
   - Target receives the message via `ChatPayloadHandler`, decorator signals `ChatTranscriptChanged`, and Target's `PersonaCardViewModel` updates its `ObservableList<ChatMessageItemViewModel>` on the UI thread.
   - If Target has `[x] Auto-Reply "Pong"` (`CTL-PER-08`), `AutopilotPongResponder` replies `"Pong #<N> (re: <snippet>)"` after 250ms, stepping the bidirectional Double Ratchet DH chain.
4. **🛑 Phase 3B Verification & Mandatory User Checkpoint**:
   - Launch app $\rightarrow$ add 2 people $\rightarrow$ `[ ⚡ Direct Handshake ]` $\rightarrow$ `[ ✓ Accept ]` $\rightarrow$ send chat message $\rightarrow$ toggle `Auto-Reply Pong` and verify bidirectional chat.
   - **STOP and await explicit user confirmation before proceeding to Phase 4.**

---

### 3.5 Phase 4: Flight Recorder, Scenario Runner & Headless `--replay` CLI
Phase 4 captures interactive user actions into repeatable JSON scenarios and introduces `--replay <path>` for automated regression gates.

1. **Incremental Scenario Schema (`ScenarioDocument`)**:
   - Uses `System.Text.Json` polymorphic `$type` serialization.
   - **Incremental Registration Rule**: Phase 4 defines the base `ScenarioStepDefinition` / `ScenarioAssertionDefinition` classes and registers **only the 6 Direct Chat steps and 3 assertions** required through Phase 4 (`SpawnPersonas`, `InitiateDirectHandshake`, `ApprovePendingHandshake`, `RejectPendingHandshake`, `SetListeningState`, `SendDirectMessage`; `AssertDirectRatchetSession`, `AssertMessageDelivered`, `AssertZeroErrors`). Phases 5–9 incrementally register their own step and assertion records as those features are built.
2. **Interactive Flight Recorder (`IFlightRecorderStateService`, `CTL-REC-01`, `CTL-REC-02`)**:
   - Tracks `ReadOnlyReactiveProperty<bool> IsRecording` and `IReadOnlyObservableList<ScenarioStepDefinition> RecordedSteps` (ignoring autonomous `AutoReplyPong` bot actions).
   - `CTL-REC-01` toggles recording or clears steps; `CTL-REC-02` saves/loads `.json` scenario files.
3. **Alias-Mapped Scenario Runner & Deterministic `R3` Waiters (`ScenarioRunnerService`)**:
   - Maps logical string aliases (`"Alice"`, `"Bob"`) to fresh runtime `SimulatedNodeId`s after calling `ISimulatedWorldStateService.ResetWorldAsync()`.
   - Every step awaits its corresponding `ISimulatorStateBus.Signals.FirstAsync(predicate, ct)` completion signal (10s timeout) with zero flaky `Task.Delay` sleeps.
4. **Headless CLI Mode (`--replay <path>`)**:
   - Runs single `.json` file or directory of `.json` scenarios in headless mode, logging to `logs/simulator-latest.jsonl` + `stdout` and exiting with code `0` (pass) or `1` (failure).
5. **Phase 4 Acceptance Criteria**:
   - Record and commit `Scenarios/01-direct-handshake-and-chat.json`.
   - Verify `dotnet run --project Percolator.Simulator -- --replay Scenarios/01-direct-handshake-and-chat.json` passes with exit code `0`.

---

### 3.6 Phase 5: Relay Node Hosting, Relayed Chat, PreKey Depletion & Queue Chaos
Phase 5 introduces `SimulatedRelayHost`, store-and-forward relayed messaging, OPK depletion/replenishment, and relay queue chaos mutations.

1. **Relay Host & Reactive Store Decorators (`SimulatedRelayHost`, `BTN-TOP-03`)**:
   - Spawns an isolated Kestrel host registering `WindowsSChannelCertificateProvider` (on Windows) or `InMemoryTransportCertificateProvider` (on Linux), `UnauthenticatedDeliveryEndpoint`, `AuthenticatedRelayEndpoint`, and `AnonymousGroupRelayEndpoint`.
   - Wraps `InMemoryRelayMailboxCapabilityStore` and `SqliteRelayPreKeyRepository` with decorators publishing `RelayQueueChanged` and `HostedPreKeyCountChanged` to `ISimulatorStateBus`.
   - Fleshes out `RelayCardView` / `RelayCardViewModel` to display live held envelopes and hosted OPK counts (`N / 25`).
2. **Single & Bulk Provisioning (`CTL-PER-13`, `BTN-TOP-02`, `BTN-ACT-05`, `BTN-ACT-06`)**:
   - Wires single and bulk mailbox registration (`BlindedRoutingToken`, `DeliveryToken`, `DrainToken`) and 25-OPK batch publishing, plus `[ + Bulk Add People ▼ ]` (`BTN-TOP-02`).
3. **Relayed Handshake, Store-and-Forward Chat & Auto-Replenish (`BTN-ACT-03`, `CTL-PER-09`)**:
   - Initiator fetches Target's `PreKeyBundle` from the Relay and enqueues X3DH handshake to Target's mailbox.
   - Envelopes wait in the Relay queue while Target is `Muted` (`IsListening = false`) and drain automatically via `ConnectMailboxStream` when `Listening`.
   - When hosted OPKs drop below 5, `[x] Auto-Replenish PreKeys` (`CTL-PER-09`) uploads a fresh batch of 25 OPKs.
4. **Relay Queue Chaos (`CTL-REL-01`, `CTL-REL-02`, `CTL-REL-03`)**:
   - Wires `[ 👯 Duplicate ]` (verifying replay filter rejection), `[ ⚡ Flip 1 Bit ]` (verifying AEAD tamper failure), and `[ 🔀 Shuffle Queue Order ]` (verifying Double Ratchet out-of-order skipped keys).
5. **Scenario Extensions & Phase 5 Regression Gate**:
   - Registers Phase 5 scenario steps (`SpawnRelay`, `RegisterMailboxes`, `PublishPreKeys`, `InitiateRelayedHandshake`, `DuplicateRelayEnvelope`, `FlipRelayEnvelopeBit`, `ShuffleRelayQueue`) and assertions (`AssertHostedOpkCount`, `AssertMailboxQueueDepth`, `AssertLogContainsCode`).
   - Replays `01-direct-handshake-and-chat.json`, then records and commits `Scenarios/02-relayed-stealth-and-chat.json` and `Scenarios/03-prekey-depletion.json`.

---

### 3.7 Phase 6A: Virtual Clock, Disappearing Messages, Reactions, Edits/Deletes & Read Receipts
Phase 6A wires controllable time acceleration and rich 1-on-1 chat mutations.

1. **Virtual Clock Service (`IVirtualClockStateService`, `BTN-TOP-04`, `BTN-TOP-05`)**:
   - Exposes `ReadOnlyReactiveProperty<DateTimeOffset> CurrentUtcNow` and `AdvanceTime(TimeSpan delta)` (`+1 Min`, `+1 Hour`), triggering outbox retry backoffs and `DisappearingMessagePruner` sweeps across all hosts.
2. **Disappearing Messages (`CTL-PER-19`)**:
   - Compose bar timer selector (`Off`, `1m`, `1h`) populates `SendMessageCommand.ExpireAfterSeconds`; advancing the virtual clock prunes expired messages and updates the UI via `ISimulatorStateBus`.
3. **Reactions, Edits, Tombstones & Read Receipts (`CTL-PER-20`, `CTL-PER-05`)**:
   - Message bubble hover actions wire `[ 👍 React ]` (`AddReactionCommand`), `[ ✏️ Edit ]` (`EditMessageCommand`), and `[ 🗑️ Delete ]` (`DeleteMessageCommand`).
   - `[x] Send Read Receipts` (`CTL-PER-05`) dispatches `MarkAsReadCommand` and updates delivery ticks (`✓` $\rightarrow$ `✓✓ Grey` $\rightarrow$ `✓✓ Blue`).
4. **Phase 6A Regression Gate**:
   - Replays `Scenarios/01`..`03` via `--replay` and verifies 0 regressions.

---

### 3.8 Phase 6B: Encrypted Profile & 64x64 Identicon Blind Blob Sync
Phase 6B wires AES-GCM avatar blind blob storage, ISO/IEC 7816-4 padded profile encryption, and live profile rerolling.

1. **Encrypted Profile & Avatar Upload/Download (`CTL-PER-01`, `CTL-PER-10`)**:
   - Encrypts the persona's 64x64 PNG Identicon via `IBlobCryptoService` (`UploadBlindBlob`) and encrypts `ProfileMetadata` into a 1040-byte padded block via `ProfileManager.EncryptProfile` (`UploadProfile`).
   - When `[x] Auto-Share Profile on Connect` (`CTL-PER-10`) is enabled, dispatches `ShareProfileCapabilityCommand` upon contact approval so the peer downloads, decrypts, and renders the remote Identicon and display name.
   - `[ 🎲 Reroll Profile ]` (`CTL-PER-01`) generates a new Word-Pair name and Identicon seed, uploads the new revision, and pushes the updated capability to contacts.
2. **Scenario Extensions & Phase 6B Regression Gate**:
   - Registers Phase 6 scenario steps (`AdvanceVirtualTime`, `SendDisappearingMessage`, `AddReaction`, `EditMessage`, `DeleteMessage`, `MarkConversationRead`, `RerollProfile`) and assertions (`AssertMessageExpired`, `AssertReactionPresent`, `AssertMessageEdited`, `AssertMessageTombstoned`, `AssertProfileSynced`).
   - Replays `Scenarios/01`..`03` and commits `Scenarios/04-advanced-chat-and-profiles.json`.

---

### 3.9 Phase 7: Group Messaging, ZK Relay Ledger, Roster Mutations & Group Flood
Phase 7 wires SenderKey ratchets, zero-knowledge presentation proofs on the home relay, epoch rotation on member eviction, and 25-message flood testing.

1. **Group Genesis, Invitations & Out-of-Order Cache (`BTN-ACT-07`, `CTL-ROS-04`, `CTL-PER-11`, `CTL-PER-15`)**:
   - Fleshes out `GroupPanelView` / `GroupPanelViewModel`.
   - Admin executes `GroupChannel.CreateGenesis`, initializes `RelayGroupLedger` with `ZkGroupPublicParams` on the home relay, and sends pairwise `GroupInvitationDto` invites.
   - Unrecognized group messages arriving before an invitee clicks `[ ✓ Join Group ]` (`CTL-PER-15`) are parked in `IUnknownGroupMessageCacheRepository` and decrypted upon joining.
2. **SenderKey Distribution & ZK Relay Fanout (`GroupKeyDistributionService`, `AnonymousGroupRelayEndpoint`)**:
   - Distributes 89-byte pairwise `GroupSenderKeyRatchet` payloads; group messages are dispatched once to the home relay with a `zk_presentation_proof` (`ZkgroupCryptographyService`) and fanned out over `SubscribeGroupStream`.
3. **Roster Add/Evict, Epoch $E \rightarrow E+1$ Rotation & 25-Message Flood (`CTL-GRP-01`, `CTL-GRP-02`)**:
   - `[ + Add Member ▼ ]`, `[ ✕ Evict ]` (advancing epoch $E \rightarrow E+1$, revoking routing token, and rotating SenderKeys for forward secrecy), `[ ✏️ Rename Group ]`, and `[ 🌊 Flood 25 Messages ]` (coalesced smoothly in UI via `R3.ThrottleLast(50ms)`).
4. **Scenario Extensions & Phase 7 Regression Gate**:
   - Registers Phase 7 steps (`CreateGroup`, `AcceptGroupInvite`, `DeclineGroupInvite`, `SendGroupMessage`, `FloodGroupMessages`, `AddGroupMember`, `EvictGroupMember`, `RenameGroup`) and assertions (`AssertGroupEpoch`, `AssertGroupMessageDelivered`, `AssertMemberCannotDecrypt`).
   - Replays `Scenarios/01`..`04` and commits `Scenarios/05-group-chat-and-epochs.json`.

---

### 3.10 Phase 8: Peer Discovery (Blinded Rendezvous, Nearby Directory & 24h Epoch Rotation)
Phase 8 wires private blinded rendezvous locators, public nearby peer discovery on the relay, and 24-hour epoch rotation.

1. **Discovery Tab UI & Private Blinded Rendezvous (`CTL-PER-21`)**:
   - Fleshes out `DiscoveryTabView` / `DiscoveryTabViewModel`.
   - Computes daily rotating `BlindedLocator = HMAC-SHA256(currentEpoch.Value, sharedSecret)` and wires `[ 📍 Publish Blinded Locator ]` and `[ 🔍 Resolve Peer Locator ]` via `IPrivateRendezvousService`.
2. **Nearby Peer Directory & One-Click Connect (`CTL-PER-03`, `CTL-PER-22`)**:
   - Hosts `NearbyDiscoveryService` (`InMemoryRelayPeerDirectory`) on `SimulatedRelayHost`, honoring `[x] Allow Peer Discovery` (`CTL-PER-03`).
   - `[ 📡 Query Nearby Peers ]` renders candidate cards with synced Identicons and a `[ 🤝 Connect ]` button (`INearbyPeerDiscoveryWorkflow.ConnectToDiscoveredPeerAsync`).
3. **24-Hour Epoch Rotation (`BTN-TOP-06`)**:
   - `[ 🕒 +24 Hours ]` advances `IVirtualClockStateService` by 24h, rotating `CurrentDiscoveryEpoch` ($D \rightarrow D+1$) and evicting expired directory registrations.
4. **Scenario Extensions & Phase 8 Regression Gate**:
   - Registers Phase 8 steps (`SetDiscoveryVisibility`, `PublishBlindedLocator`, `ResolveBlindedLocator`, `QueryNearbyPeers`, `ConnectDiscoveredPeer`) and assertions (`AssertBlindedLocatorResolved`, `AssertNearbyCandidateCount`).
   - Replays `Scenarios/01`..`05` and commits `Scenarios/06-peer-discovery.json`.

---

### 3.11 Phase 9: Swarm File Transfer, Custom Piece Heatmap & Late Downloader Injection
Phase 9 completes the simulator with BitTorrent-style multi-stream file transfers, a custom 30 FPS piece heatmap control, and dynamic 50% late joiner injection.

1. **Synthetic Merkle Presets & Transfer Negotiation (`BTN-ACT-04`, `CTL-PER-04`, `CTL-PER-06`, `CTL-PER-12`, `CTL-PER-16`)**:
   - Fleshes out `SwarmPanelView` / `SwarmPanelViewModel`.
   - Uses `WindowsSparseDesktopFileStore` (on Windows via `Percolator.Infrastructure.Windows`) or `PortableDesktopFileStore` (on Linux via `Percolator.Infrastructure`) for file pre-allocation and boundary slice storage in the node's temp directory, generates deterministic `2 MB`, `25 MB`, and `100 MB` files with SHA-256 Merkle trees, and calls `IFileTransferActionHandler.InitiateTransferAsync`.
   - Honors `Allow File Transfers` (`CTL-PER-04`), `Auto-Accept File Transfers` (`CTL-PER-12`), Pending Offer banner (`CTL-PER-16`), and `Relay-Only Routing (Hide IP)` (`CTL-PER-06`: `RelayTransferPipeClient` vs. `TcpMultiStreamPoolFactory`).
2. **30 FPS `SwarmHeatmapControl` & Participant Controls (`CTL-ROS-05`, `CTL-SWM-01`, `CTL-SWM-02`, `CTL-SWM-03`)**:
   - Coalesces chunk progress via `Channel<SwarmChunkProgressSignal>` into 33ms UI ticks rendering `SwarmHeatmapControl` (`Grey`: missing, `Blue`: in-flight, `Green`: verified, `Red`: corrupted).
   - Wires `[ ⏸️ Pause ]`, `[ ▶️ Resume ]`, `[ ✕ Cancel ]` (`CTL-SWM-01`), and `[ ➕ Inject Late Downloader ▼ ]` (`CTL-SWM-02`) at 50% progress to prove multi-source swarm downloading from both seeder and partial peer.
3. **Final Regression Gate & Zero Orphaned Stubs Audit**:
   - Registers Phase 9 steps and assertions, replays all `Scenarios/01`..`07` with exit code `0`, and runs a mechanical check confirming **0 controls out of 53 remain bound to `ShowPlaceholder`**.

---

## 4. Streamlined Vertical Feature Slice Tree

```
Percolator.Simulator/
├── Features/
│   ├── Shell/
│   │   ├── MainWindow.axaml / .cs
│   │   ├── MainWindowViewModel.cs
│   │   ├── TopGlobalToolbarView.axaml / .cs
│   │   ├── TopGlobalToolbarViewModel.cs
│   │   ├── ContextualActionBarView.axaml / .cs
│   │   ├── ContextualActionBarViewModel.cs
│   │   ├── RosterTreeView.axaml / .cs
│   │   ├── RosterViewModel.cs
│   │   ├── WorkbenchView.axaml / .cs
│   │   ├── WorkbenchViewModel.cs
│   │   ├── SimulatedNodeSummary.cs
│   │   ├── ISelectionStateService.cs
│   │   ├── SelectionStateService.cs
│   │   ├── ISimulatorStateBus.cs
│   │   ├── SimulatorStateBus.cs
│   │   ├── SimulatorStateSignal.cs
│   │   ├── IStatusToastService.cs
│   │   ├── StatusToastService.cs
│   │   ├── UiTaskRunner.cs
│   │   └── DesignDataService.cs
│   ├── Logging/
│   │   ├── SimulatorLogEntry.cs
│   │   ├── ISimulatorLogSink.cs
│   │   ├── MultiSinkLogEngine.cs
│   │   ├── NodeScopedLoggerProvider.cs
│   │   ├── LogDockView.axaml / .cs
│   │   └── LogDockViewModel.cs
│   ├── Topology/
│   │   ├── SimulatedNodeId.cs
│   │   ├── ISimulatedWorldStateService.cs
│   │   ├── SimulatedWorldStateService.cs
│   │   ├── ISimulatedPersonaHost.cs
│   │   ├── SimulatedPersonaHost.cs
│   │   ├── SimulatorSqlCipherPragmaInterceptor.cs
│   │   ├── SimulatorNotifyingAppRouterDecorator.cs
│   │   ├── SimulatorNotifyingHandshakeDecorator.cs
│   │   ├── LoopbackPortAllocator.cs
│   │   ├── WordPairNameGenerator.cs
│   │   ├── IdenticonGenerator.cs
│   │   ├── PersonaCardView.axaml / .cs
│   │   └── PersonaCardViewModel.cs
│   ├── DirectChat/
│   │   ├── DirectHandshakeOrchestrator.cs
│   │   ├── DirectChatCoordinator.cs
│   │   ├── AutopilotPongResponder.cs
│   │   └── ChatMessageItemViewModel.cs
│   ├── Scenarios/
│   │   ├── ScenarioDocument.cs
│   │   ├── ScenarioStepDefinitions.cs
│   │   ├── ScenarioAssertionDefinitions.cs
│   │   ├── IFlightRecorderStateService.cs
│   │   ├── FlightRecorderStateService.cs
│   │   ├── ScenarioRunnerService.cs
│   │   ├── HeadlessReplayRunner.cs
│   │   ├── FlightRecorderView.axaml / .cs
│   │   └── FlightRecorderViewModel.cs
│   ├── RelayedChat/
│   │   ├── ISimulatedRelayHost.cs
│   │   ├── SimulatedRelayHost.cs
│   │   ├── SimulatorNotifyingRelayCapabilityStoreDecorator.cs
│   │   ├── SimulatorNotifyingRelayPreKeyRepositoryDecorator.cs
│   │   ├── RelayProvisioningCoordinator.cs
│   │   ├── IRelayQueueChaosController.cs
│   │   ├── RelayQueueChaosController.cs
│   │   ├── RelayCardView.axaml / .cs
│   │   └── RelayCardViewModel.cs
│   ├── RichChatAndProfiles/
│   │   ├── IVirtualClockStateService.cs
│   │   ├── VirtualClockStateService.cs
│   │   ├── RichChatActionCoordinator.cs
│   │   └── ProfileAvatarSyncService.cs
│   ├── GroupMessaging/
│   │   ├── GroupSimulationCoordinator.cs
│   │   ├── GroupFloodService.cs
│   │   ├── GroupPanelView.axaml / .cs
│   │   └── GroupPanelViewModel.cs
│   ├── DiscoverySimulation/
│   │   ├── DiscoverySimulationCoordinator.cs
│   │   ├── DiscoveredPeerItemViewModel.cs
│   │   ├── DiscoveryTabView.axaml / .cs
│   │   └── DiscoveryTabViewModel.cs
│   └── SwarmSimulation/
│       ├── SwarmChunkProgressSignal.cs
│       ├── SyntheticFilePresetGenerator.cs
│       ├── SwarmSimulationCoordinator.cs
│       ├── LateDownloaderInjector.cs
│       ├── SwarmHeatmapControl.cs
│       ├── SwarmPanelView.axaml / .cs
│       └── SwarmPanelViewModel.cs
├── Scenarios/
│   ├── 01-direct-handshake-and-chat.json
│   ├── 02-relayed-stealth-and-chat.json
│   ├── 03-prekey-depletion.json
│   ├── 04-advanced-chat-and-profiles.json
│   ├── 05-group-chat-and-epochs.json
│   ├── 06-peer-discovery.json
│   └── 07-swarm-late-joiner.json
├── App.axaml / .cs
└── Program.cs
```
