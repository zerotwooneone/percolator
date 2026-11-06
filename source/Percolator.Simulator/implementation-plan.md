# Percolator.Simulator Implementation Plan

> [!NOTE] Non-Regulatory Tooling Disclaimer
> `Percolator.Simulator` is an engineering developer tool, interactive QA "monkey testing" sandbox, and automated scenario verification harness. It is **NOT** part of the formal medical device QMS (FDA 21 CFR 820.30 / IEC 62304) software item deliverables and does not contain `SYS-*` or `DEP-*` regulatory tags. Its requirements and design specifications serve internal engineering quality, protocol validation, and verification rigor.

## Refinement Status Gate
- [ ] Lens 1: Scope & Feature Slice Framing (Status: In Review)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: Pending)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Pending)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Pending)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Pending)
- [ ] Implementation Gate: LOCKED (Requires Approval of Lenses 1-5)

---

## 1. Scope & Architectural Boundaries (Lens 1)

### 1.1 Core Mission: UI-First, Incrementally Verifiable QA Sandbox
`Percolator.Simulator` is a standalone **Avalonia UI** desktop application built with **FluentAvalonia** and **CommunityToolkit.Mvvm**. It enables a QA tester or developer to spin up simulated identities and relays, and interactively test Direct Chat, Relayed Chat, Group Chat, Encrypted Profiles, Peer Discovery, and Swarm File Transfers side-by-side **without needing cryptographic training**.

Implementation follows a strict **UI-Shell-First, Log-Verified, Record-and-Ratchet Progression**:
1. **Zero Orphaned Stubs**: Every single button, toggle, and flyout stubbed in Phase 1 is explicitly mapped to a concrete implementation phase (Phases 2–9). When Phase 9 finishes, **zero non-functional buttons remain in the application**.
2. **Phase 1 Visual Preview**: Phase 1 includes mock seed data so the complete 4-zone layout, side-by-side Persona/Relay cards, flyouts, and action bar role pills can be inspected and verified immediately before network code is written.
3. Build a **Structured Logging & UI Log Viewer** subsystem in Phase 2 so both humans (in the UI) and AI (via rolling JSONL files and `stdout`) have full observability.
4. Wire up the simplest vertical slice in Phase 3 (**Single Identity Creation + Direct Handshake + Direct 1-on-1 Chat**), deactivating Phase 1 mock seed data, and **STOP for human interactive verification**.
5. Wire up **Scenario Recording & Playback** in Phase 4 immediately after Direct Chat is approved, capturing a committed regression script (`Scenarios/01-direct-handshake-and-chat.json`).
6. **Mandatory Cumulative Regression Gate**: In every subsequent phase (Phases 5–9), the AI must replay **all previously committed scenario recordings** via `--replay` and verify zero regressions in the structured log before declaring the phase complete.

### 1.2 Explicit Non-Goals & Out-of-Scope Boundaries (Negative Scope)
To prevent speculative bloat and architectural drift, the following capabilities are strictly **out of scope**:
- **No Embedded HTTP/REST Control Daemon or Dual CLI**: The simulator does not host an ASP.NET Core Minimal API control server or a separate interactive CLI binary. All automated verification executes through the unified `--replay <path>` startup flag.
- **No Production Vault Passphrase / Argon2id Unlock Ceremony**: Simulated personas use in-memory synthetic master keys (`InMemoryMasterKeyHandle`) to allow instant 1-click identity creation without interactive password prompts.
- **No WAN NAT Traversal (UPnP / STUN) or Multi-Machine Network Bridging**: All simulated nodes and relays bind strictly to `127.0.0.1` loopback endpoints.
- **No Continuous Online Presence or Heartbeat Pings**: Personas act strictly as store-and-forward actors (Signal Protocol parity); muting ingress (`IsListening = false`) never emits "offline" presence notifications to peers or relays.
- **No Custom Protocol or Cryptographic Primitives**: The simulator must never invent mock encryption or custom wire framing; it exclusively orchestrates the real `Percolator.Domain`, `Percolator.Application`, `Percolator.Infrastructure`, and `Percolator.Apps.*` assemblies.

### 1.3 Minimal Public Surface & Internal Encapsulation
- **Internal Visibility by Default**: All feature slice ViewModels, Views, orchestrators, scenario steps, and host wrappers in `Percolator.Simulator` are declared `internal sealed` (with `InternalsVisibleTo("Percolator.Simulator.Tests")` for unit testing).
- **Decoupled Two-Phase Orchestration**: Multi-step protocol flows are never collapsed into monolithic "god methods." Handshake initiation is decoupled from pending request approval; candidate discovery querying is decoupled from on-demand PreKey fetching and connection dialing; file transfer offer negotiation is decoupled from swarm chunk streaming.

---

## 2. Complete UI Control Inventory & 100% Phase-Mapping Ledger (Lens 1)

Every control created in Phase 1 is listed below with its exact UI location, behavior, and target implementation phase. **No control may be added in Phase 1 unless it appears in this ledger and is wired in Phases 2–9.**

### 2.1 Zone 1: Top Global Toolbar (`TopGlobalToolbarView`)
| Control ID | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :---: | :---: | :--- |
| `BTN-TOP-01` | `[ + Add Person ]` Button | Phase 1 | **Phase 3** | Spawns 1 new simulated identity with auto-generated Word-Pair name, `WriteableBitmap` Identicon, and loopback Kestrel server. |
| `BTN-TOP-02` | `[ + Bulk Add People ▼ ]` Flyout (`Count: 2..20`, `Relay: Optional`, `Publish PreKeys: Checkbox`) | Phase 1 | **Phase 5** | Spawns $N$ identities in batch, optionally registers their mailboxes on the selected relay, and optionally publishes initial PreKey bundles. |
| `BTN-TOP-03` | `[ + Add Relay ]` Button | Phase 1 | **Phase 5** | Spawns a new `SimulatedRelayHost` on an allocated loopback port (`UnauthenticatedDeliveryService`, `AuthenticatedRelayService`, `AnonymousGroupRelayService`). |
| `BTN-TOP-04` | `[ 🕒 +1 Min ]` Virtual Clock Button | Phase 1 | **Phase 6** | Advances `VirtualizedTimeProvider` by 1 minute (triggers outbox retry backoffs and short disappearing message timers). |
| `BTN-TOP-05` | `[ 🕒 +1 Hour ]` Virtual Clock Button | Phase 1 | **Phase 6** | Advances `VirtualizedTimeProvider` by 1 hour (triggers disappearing message evictions). |
| `BTN-TOP-06` | `[ 🕒 +24 Hours ]` Virtual Clock Button | Phase 1 | **Phase 8** | Advances `VirtualizedTimeProvider` by 24 hours (rotates `DiscoveryEpoch` and expires relay rendezvous registrations). |
| `BTN-TOP-07` | `[ 🔄 Reset World ]` Button | Phase 1 | **Phase 3** | Disposes all active persona/relay hosts, releases loopback ports, clears UI state, and starts a fresh run sandbox directory. |

### 2.2 Zone 2: Contextual Selection & Directional Action Bar (`ContextualActionBarView`)
| Control ID | Selection State | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `BTN-ACT-01` | 2 People (`1️⃣ Initiator ➔ 2️⃣ Target`) | `[ ⇄ Swap Roles ]` Button | Phase 1 | **Phase 1** | Pure UI state swap: swaps the `1️⃣ Initiator` and `2️⃣ Target` identities in `SelectionStateViewModel`. |
| `BTN-ACT-02` | 2 People (`1️⃣ ➔ 2️⃣`) | `[ ⚡ Direct Handshake (1️⃣ ➔ 2️⃣) ]` Button | Phase 1 | **Phase 3** | Initiator fetches Target's direct loopback `InvitationLink` and invokes `HandshakeService.InitiateHandshakeAsync` over `DirectP2P`. |
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
| `CTL-ROS-03` | Person Row | `[ 🟢 Listening / ⏸️ Muted ]` Inline Toggle Pill | Phase 1 | **Phase 3** | Toggles `IsListening`: when `Muted`, stops Kestrel/mailbox stream ingress without notifying peers; when `Listening`, reconnects and drains. |
| `CTL-ROS-04` | Group Row | Group Selection Click | Phase 1 | **Phase 7** | Opens the Group Channel Inspector & Broadcast View in the Center Workbench. |
| `CTL-ROS-05` | Swarm Row | Swarm Transfer Selection Click | Phase 1 | **Phase 9** | Opens the Swarm Piece Heatmap & Participant Progress Panel in the Center Workbench. |

### 2.4 Zone 4A: Persona Card in Center Workbench (`PersonaCardView`)
| Control ID | Card Section | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `CTL-PER-01` | Header | Avatar Identicon + Display Name + `[ 🎲 Reroll Profile ]` Button | Phase 1 | **Phase 6** | Generates new Identicon PNG + display name revision, encrypts via `ProfileManager` + `IBlobCryptoService`, uploads to relay, and shares capability. |
| `CTL-PER-02` | Header | `[ 🟢 Listening / ⏸️ Muted ]` Switch | Phase 1 | **Phase 3** | Bound to the same `IsListening` capability property as `CTL-ROS-03`. |
| `CTL-PER-03` | `[ ⚙️ Features ▼ ]` Flyout | `[x] Allow Peer Discovery` Checkbox | Phase 1 | **Phase 8** | Toggles `IsDiscoveryEnabled` (rejects/ignores discovery queries when unchecked). |
| `CTL-PER-04` | `[ ⚙️ Features ▼ ]` Flyout | `[x] Allow File Transfers` Checkbox | Phase 1 | **Phase 9** | Toggles `IsFileTransferEnabled` (auto-rejects incoming transfer offers when unchecked). |
| `CTL-PER-05` | `[ ⚙️ Features ▼ ]` Flyout | `[x] Send Read Receipts` Checkbox | Phase 1 | **Phase 6** | Toggles automatic dispatch of `MarkAsReadCommand` when viewing incoming messages. |
| `CTL-PER-06` | `[ ⚙️ Features ▼ ]` Flyout | `[ ] Relay-Only Routing (Hide IP)` Checkbox | Phase 1 | **Phase 9** | Toggles `RoutingPreference` between `DirectPreferred` and `RelayOnly` for file transfers. |
| `CTL-PER-07` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Accept Handshakes` Checkbox | Phase 1 | **Phase 3** | Automatically approves incoming pending handshakes via `ContactRequestCoordinator` + `HandshakeService`. |
| `CTL-PER-08` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Reply "Pong"` Checkbox | Phase 1 | **Phase 3** | Automatically replies `"Pong #<N>"` to incoming chat messages, stepping the asymmetric Double Ratchet DH chain. |
| `CTL-PER-09` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Replenish PreKeys` Checkbox | Phase 1 | **Phase 5** | Automatically uploads a fresh PreKey batch to the home relay when hosted OPK count drops below watermark. |
| `CTL-PER-10` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Share Profile on Connect` Checkbox | Phase 1 | **Phase 6** | Automatically sends `ShareProfileCapabilityCommand` when a contact request is approved. |
| `CTL-PER-11` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Accept Group Invites` Checkbox | Phase 1 | **Phase 7** | Automatically accepts incoming `PendingGroupInvitation` items. |
| `CTL-PER-12` | `[ 🤖 Autopilot ▼ ]` Flyout | `[ ] Auto-Accept File Transfers` Checkbox | Phase 1 | **Phase 9** | Automatically calls `AcceptTransferAsync` to the persona's temp download folder when an offer arrives. |
| `CTL-PER-13` | Relay Bar | `[ Relay: Dropdown ▼ ]` + `[ 📬 Register ]` + `[ 🔑 Publish PreKeys ]` | Phase 1 | **Phase 5** | Registers single persona's mailbox on selected relay and manually publishes a 25-OPK bundle. |
| `CTL-PER-14` | Pending Banner | Pending Handshake Row: `[ ✓ Accept ]` `[ ✕ Reject ]` `[ 🚫 Block ]` | Phase 1 | **Phase 3** | Invokes `ApproveRequestAsync` + `ApprovePendingHandshakeAsync`, `RejectRequestAsync(block: false)`, or `RejectRequestAsync(block: true)`. |
| `CTL-PER-15` | Pending Banner | Pending Group Invite Row: `[ ✓ Join Group ]` `[ ✕ Decline ]` | Phase 1 | **Phase 7** | Accepts or declines `PendingGroupInvitation` in `Percolator.Apps.Chat`. |
| `CTL-PER-16` | Pending Banner | Incoming File Offer Row: `[ ✓ Accept Download ]` `[ ✕ Decline ]` | Phase 1 | **Phase 9** | Calls `IFileTransferActionHandler.AcceptTransferAsync` or `CancelTransferAsync`. |
| `CTL-PER-17` | Chat Tab | Conversation Selector Dropdown (`Peer / Group`) | Phase 1 | **Phase 3** (Direct) / **Phase 7** (Group) | Switches the active chat transcript displayed in the Persona Card. |
| `CTL-PER-18` | Chat Tab | Message Compose TextBox + `[ Send ]` Button | Phase 1 | **Phase 3** (Direct) / **Phase 7** (Group) | Dispatches `SendMessageCommand` via `IChatService.SendMessageAsync`. |
| `CTL-PER-19` | Chat Tab | Disappearing Timer Selector (`Off`, `1m`, `1h`) on Compose Bar | Phase 1 | **Phase 6** | Populates `ExpireAfterSeconds` on `SendMessageCommand`. |
| `CTL-PER-20` | Chat Message Row | Hover Actions: `[ 👍 React ]` `[ ✏️ Edit ]` `[ 🗑️ Delete ]` | Phase 1 | **Phase 6** | Invokes `AddReactionCommand`, `EditMessageCommand`, or `DeleteMessageCommand` via `IChatService`. |
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
| `CTL-SWM-02` | Swarm Panel | `[ ➕ Inject Late Downloader ▼ ]` (`Select Person`, `At %: 50%`) | Phase 1 | **Phase 9** | Registers a trigger (or immediately joins if already past threshold) for the selected person to join the active swarm. |
| `CTL-SWM-03` | Swarm Panel | `SwarmHeatmapCanvas` (Custom `DrawingContext` Piece Grid) | Phase 1 | **Phase 9** | Renders verified (green), in-flight (blue), missing (grey), and corrupted (red) chunk states for each participant. |

### 2.6 Zone 5: Right Flight Recorder Drawer & Bottom Log Dock
| Control ID | Panel | UI Control & Label | Stubbed In | Wired In | Concrete Implementation Behavior |
| :--- | :--- | :--- | :---: | :---: | :--- |
| `CTL-REC-01` | Flight Recorder | `[ 🔴 Record / ⏹️ Pause Recording ]` Toggle + `[ 🗑️ Clear Steps ]` | Phase 1 | **Phase 4** | Enables/pauses appending interactive UI actions to `FlightRecorderService` or clears the current step list. |
| `CTL-REC-02` | Flight Recorder | `[ 💾 Save Scenario JSON... ]` + `[ 📂 Load & Replay JSON... ]` | Phase 1 | **Phase 4** | Saves recorded steps + assertions to a `.json` file or loads and executes a `.json` file through `ScenarioRunnerService`. |
| `CTL-LOG-01` | Bottom Log Dock | `Minimum Log Level` Dropdown (`Trace`, `Debug`, `Info`, `Warn`, `Error`) | Phase 1 | **Phase 2** | Forward-only ingestion filter: only future log events $\ge$ selected level are appended to the 500-item UI ring buffer. |
| `CTL-LOG-02` | Bottom Log Dock | `[ 🧹 Clear UI Log ]` Button | Phase 1 | **Phase 2** | Clears the currently visible items in the 500-item UI log ring buffer (does not erase `logs/simulator-latest.jsonl`). |

---

## 3. Detailed Technical Specifications: All 9 Phases (Lens 1)

### 3.1 Phase 1 Technical Specification (Application Shell & UI Layout)
Phase 1 establishes the visual shell, DI wiring, and selection state machine before backend protocol logic is written.

1. **Project Dependencies, Legacy Template Cleanup & Styling**:
   - **Legacy Horizontal Folder Excise**: Delete the template-generated horizontal folders `Models/`, `ViewModels/`, `Views/`, and `ViewLocator.cs` from `Percolator.Simulator` (and remove `<Folder Include="Models\" />` from `Percolator.Simulator.csproj`) so 100% of code resides inside `Features/<SliceName>/`.
   - `Percolator.Simulator.csproj` includes:
     ```xml
     <ProjectReference Include="..\Percolator.Infrastructure\Percolator.Infrastructure.csproj" />
     ```
   - `App.axaml` configures FluentAvalonia styling and maps Views to ViewModels within `Features/`:
     ```xml
     <Application.Styles>
         <StyleInclude Source="avares://FluentAvaloniaUI/Styling/Styles.axaml" />
     </Application.Styles>
     ```
2. **Feature-Scoped ViewModel Composition (`Features/Shell/`)**:
   - `MainWindowViewModel` (`Features/Shell/`): root coordinator hosting 5 zones.
   - `TopGlobalToolbarViewModel` (`Features/Shell/`): Zone 1 toolbar commands.
   - `ContextualActionBarViewModel` (`Features/Shell/`): Zone 2 action bar bound to `ISelectionStateService`.
   - `RosterViewModel` (`Features/Shell/`): Zone 3 left tree.
   - `WorkbenchViewModel` (`Features/Shell/`): Zone 4 center multi-card split-viewport.
   - `FlightRecorderViewModel` (`Features/Scenarios/`) & `LogDockViewModel` (`Features/Logging/`): Zone 5 right drawer and bottom log dock.
3. **Selection State Machine (`ISelectionStateService`)**:
   - Singleton service tracking `PrimarySelectedNode` (`1️⃣ Initiator`), `SecondarySelectedNode` (`2️⃣ Target`), and `MultiSelectedNodes` (`☑️ Bulk`).
   - `BTN-ACT-01` (`[ ⇄ Swap Roles ]`) swaps Primary and Secondary nodes immediately in the UI.
4. **Center Workbench Pinning Logic (Max 4 Cards)**:
   - `WorkbenchViewModel` manages `ObservableCollection<WorkbenchCardViewModel> PinnedCards`.
   - When a 5th card is pinned via `CTL-ROS-01`, the oldest unpinned card is automatically unpinned.
5. **Design Data / Preview Seed Mode (`DesignDataService`)**:
   - Seeds mock cards (`"Crimson Falcon" [1️⃣]`, `"Silent Otter" [2️⃣]`, `"Relay-Alpha"`) on startup for immediate visual layout inspection.
   - **Transition Rule**: In Phase 3, this mock seed is completely replaced by real runtime nodes.
6. **Standardized Placeholder Toast (`IStatusToastService`)**:
   - Clicking any stubbed Phase 2–9 control calls `ShowPlaceholder(actionName, scheduledPhase)`, displaying a FluentAvalonia `InfoBar` toast (*"ℹ️ Action 'Send File Preset' is scheduled for implementation in Phase 9"*).
7. **Phase 1 Verification & Acceptance Criteria**:
   - `dotnet build Percolator.Simulator` succeeds with 0 warnings/errors.
   - Launching opens the 4-zone UI showing the seeded mock cards, responsive role swapping, and informative toasts on stubbed buttons.

---

### 3.2 Phase 2 Technical Specification (Logging Engine & Bounded UI Dock)
Phase 2 establishes full multi-sink observability so all future protocol events are readable by both humans and AI.

1. **Structured JSONL Schema (`logs/simulator-latest.jsonl`)**:
   Each line in the rolling log file is a compact JSON object adhering to this schema:
   ```csharp
   public sealed record SimulatorLogEntry(
       DateTimeOffset TimestampUtc,
       LogLevel Level,
       string? NodeName,
       string? NodeId,
       string Category,
       string Message,
       string? Exception = null);
   ```
2. **Rolling File Retention Policy**:
   - All logs write to `logs/` at the application root.
   - On startup: if `logs/simulator-latest.jsonl` exists, it is renamed to `logs/simulator-YYYYMMDD-HHmmss.jsonl`.
   - The directory retains the **10 most recent** archived session logs and deletes older ones automatically.
3. **Thread-Safe Multi-Sink (`ISimulatorLogSink`)**:
   - Background gRPC/TCP worker threads push entries to a non-blocking `Channel<SimulatorLogEntry>.CreateUnbounded(new UnboundedChannelOptions { SingleReader = true })`.
   - A dedicated background consumer thread batches and appends entries to `logs/simulator-latest.jsonl` and formats lines to `stdout`, completely avoiding `IOException` file-locking issues.
4. **Node-Scoped Logger Provider (`NodeScopedLoggerProvider`)**:
   - Implements `ILoggerProvider`. When each `SimulatedPersonaHost` (Phase 3) or `SimulatedRelayHost` (Phase 5) builds its internal `IServiceProvider`, it registers:
     ```csharp
     logging.AddProvider(new NodeScopedLoggerProvider(nodeName, nodeId, globalLogSink));
     ```
   - Automatically enriches internal framework logs (`HandshakeService`, `OutboxWorker`, `Kestrel`) with the persona's display name (`Crimson Falcon`).
5. **Forward-Only UI Log Gate (`LogDockViewModel`)**:
   - `MinimumLogLevel` dropdown (`Trace`, `Debug`, `Information` [Default], `Warning`, `Error`).
   - Filtering is evaluated **at ingestion time**:
     ```csharp
     if (entry.Level < MinimumLogLevel) return;
     ```
   - It acts strictly as a forward gate for future messages; existing messages in the UI log collection are never re-evaluated or lost.
6. **Bounded 500-Entry UI Ring Buffer**:
   - `ObservableCollection<UiLogEntry> LogEntries` is capped at 500 items.
   - When entry #501 arrives, it executes:
     ```csharp
     Dispatcher.UIThread.Post(() => {
         if (LogEntries.Count >= 500) LogEntries.RemoveAt(0);
         LogEntries.Add(new UiLogEntry(entry));
     });
     ```
   - Guarantees constant memory usage and zero UI stutter during high-speed monkey-testing or swarm transfers.
7. **UI Log Formatting & Actions**:
   - `CTL-LOG-01` (`MinimumLogLevel` selector) and `CTL-LOG-02` (`[ 🧹 Clear UI Log ]` button).
   - Monospace font rendering with color-coded severity badges (`Trace`: Grey, `Debug`: LightBlue, `Info`: Green, `Warn`: Yellow, `Error`: Red) and node attribution pills (`[Crimson Falcon]`).
8. **Phase 2 Verification & Acceptance Criteria**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - App startup creates `logs/simulator-latest.jsonl` with structured JSON entries.
   - Pushing 600 test entries via a diagnostic test verifies that the UI collection truncates smoothly at 500 items.
   - Changing `MinimumLogLevel` to `Warning` immediately suppresses incoming `Info` messages while retaining existing log items.
   - `[ 🧹 Clear UI Log ]` empties the visual dock without affecting the `.jsonl` disk file.

---

### 3.3 Phase 3 Technical Specification (Direct Identity & Chat Engine)
Phase 3 establishes live simulated identity hosting, direct loopback gRPC transport, and 1-on-1 chat.

1. **Host Lifecycle & DI Isolation (`SimulatedPersonaHost`)**:
   - Each simulated persona executes in an isolated `IHost` / `IServiceScope`:
     ```csharp
     public interface ISimulatedPersonaHost : IAsyncDisposable
     {
         SimulatedNodeId Id { get; }
         PersonaDisplayName DisplayName { get; }
         Uri BoundEndpoint { get; }
         IServiceProvider Services { get; }
         PersonaCapabilityPolicy Capabilities { get; set; }
         PersonaAutopilotPolicy Autopilot { get; set; }
         ValueTask StartAsync(CancellationToken ct = default);
         ValueTask StopAsync(CancellationToken ct = default);
     }
     ```
   - Configures an in-memory SQLite connection string (`Data Source=persona_{nodeId};Mode=Memory;Cache=Shared`).
   - Seeds a synthetic master key (`new InMemoryMasterKeyHandle()`) and calls `AddAuthenticatedInfrastructure(...)` with `SqlCipherPragmaInterceptor`.
   - Registers `NodeScopedLoggerProvider` (from Phase 2) so all internal logs carry the persona's name into `ISimulatorLogSink`.
2. **Procedural Name & Identicon Generation**:
   - `WordPairNameGenerator.Generate()` combines 256 adjectives and 256 nouns (e.g. `Crimson Falcon`).
   - `IdenticonGenerator.CreatePng(seed)` generates a symmetric 5×5 pixel blocky avatar as a `64x64` PNG byte buffer, loaded into an Avalonia `Bitmap` for rendering in `PersonaCardView`.
3. **Dynamic Loopback Port Allocation & Platform TLS Transport**:
   - `LoopbackPortAllocator` leases an ephemeral available port on `127.0.0.1`.
   - Binds `KestrelPeerTransportServer` on `https://127.0.0.1:{port}` with HTTP/2 and TLS 1.3.
   - On Windows, registers `WindowsSChannelCertificateProvider` to import ECDSA P-256 certs into CNG (`UserKeySet`); on Linux/macOS, registers `InMemoryTransportCertificateProvider`.
   - Configures `PeerGrpcChannelFactory` with TOFU certificate validation callback (`(_, _, _, _) => true`).
4. **Direct Handshake Initiation (`BTN-ACT-02`)**:
   - Reads `PrimarySelectedNode` (Initiator) and `SecondarySelectedNode` (Target) from `ISelectionStateService`.
   - Initiator creates a direct `InvitationLink(Target.Host, Target.Port, Target.IdentityKey)`.
   - Calls `HandshakeService.InitiateHandshakeAsync(...)` over `DeliveryRouteType.DirectP2P`.
   - Target's `UnauthenticatedDeliveryService.DeliverDirect` receives envelope and invokes `IContactRequestCoordinator.HandleInboundRequestAsync`.
   - Handshake envelope parks in `IPendingHandshakeRepository`; Target UI surfaces **Pending Handshake** banner (`CTL-PER-14`) with `[ ✓ Accept ]`, `[ ✕ Reject ]`, and `[ 🚫 Block ]`.
   - If Target has `AutoAcceptHandshakes = true` (`CTL-PER-07`), Target automatically calls `ApproveRequestAsync` + `ApprovePendingHandshakeAsync`.
5. **Direct 1-on-1 Chat Messaging (`CTL-PER-18`)**:
   - Author enters text and clicks `[ Send ]`.
   - Resolves active direct `ChannelId` for peer contact and invokes `IChatService.SendMessageAsync(command)`.
   - When Target receives message, `ChatPayloadHandler` decrypts payload and publishes `ChatMessageReceivedEvent`.
   - Target's `PersonaCardViewModel` marshals the message into its `ObservableCollection<ChatMessageItemViewModel>`.
6. **Autopilot Pong Responder (`CTL-PER-08`)**:
   - If Target has `AutoReplyPong = true`, receiving a message schedules an automatic reply:
     ```csharp
     if (!content.StartsWith("Pong #", StringComparison.OrdinalIgnoreCase))
     {
         await Task.Delay(250, ct);
         await chatService.SendMessageAsync(new SendMessageCommand(..., $"Pong #{++_pongCount} (re: {snippet})"));
     }
     ```
   - Guarantees bidirectional message alternation, proving that the **Double Ratchet DH ratchet steps** properly on both sides.
7. **`IsListening` Stealth Ingress Toggle (`CTL-ROS-03` / `CTL-PER-02`)**:
   - When set to `Muted`, stops or drops inbound Kestrel connections. Outbox dispatch remains functional.
   - Does not broadcast or notify peers, verifying zero presence leakage.
8. **Phase 1 Preview Seed Deactivation**:
   - On first invocation of `BTN-TOP-01` (`+ Add Person`), `DesignDataService.ClearPreviewData()` is called, removing mock data and binding the Roster and Workbench exclusively to live `SimulatedNodeRegistry` instances.
9. **🛑 Phase 3 Verification & Mandatory User Checkpoint**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - Launch app $\rightarrow$ click `[ + Add Person ]` twice (e.g. `Crimson Falcon` and `Silent Otter` appear in Roster and Workbench).
   - Click `[ ⚡ Direct Handshake ]` $\rightarrow$ verify Pending banner appears on Silent Otter.
   - Click `[ ✓ Accept ]` $\rightarrow$ verify handshake completes and chat becomes active.
   - Type `"Hello Otter"` in Crimson Falcon's chat and click `[ Send ]` $\rightarrow$ verify message appears in Silent Otter's card.
   - Toggle `Auto-Reply Pong` on Silent Otter $\rightarrow$ send message $\rightarrow$ verify Silent Otter automatically replies with `"Pong #1"`.
   - **Execution stops and awaits explicit user confirmation before proceeding to Phase 4.**

---

### 3.4 Phase 4 Technical Specification (Flight Recorder, Playback Engine & Headless Runner)
Phase 4 captures user actions as repeatable JSON scenarios and enables automated CLI replay (`--replay <path>`) for CI and AI regression verification.

1. **Scenario Document JSON Schema & Polymorphic Registry**:
   Scenarios are serialized via `System.Text.Json` using polymorphic type discriminators (`$type`):
   ```csharp
   public sealed record ScenarioDocument(
       string Name,
       string Description,
       int Version,
       IReadOnlyList<ScenarioStepDefinition> Steps,
       IReadOnlyList<ScenarioAssertionDefinition> Assertions);
   ```
   **Complete Polymorphic Step & Assertion Discriminator Registry (Phases 4–9)**:
   | Phase | Step `$type` Discriminators | Assertion `$type` Discriminators |
   | :---: | :--- | :--- |
   | **Phase 4** | `SpawnPersonas`, `InitiateDirectHandshake`, `ApprovePendingHandshake`, `RejectPendingHandshake`, `SetListeningState`, `SendDirectMessage` | `AssertDirectRatchetSession`, `AssertMessageDelivered`, `AssertZeroErrors` |
   | **Phase 5** | `SpawnRelay`, `RegisterMailboxes`, `PublishPreKeys`, `InitiateRelayedHandshake`, `DuplicateRelayEnvelope`, `FlipRelayEnvelopeBit`, `ShuffleRelayQueue` | `AssertHostedOpkCount`, `AssertMailboxQueueDepth`, `AssertLogContainsCode` (`AEAD_TAG_MISMATCH`, `REPLAY_REJECTED`) |
   | **Phase 6** | `AdvanceVirtualTime`, `SendDisappearingMessage`, `AddReaction`, `EditMessage`, `DeleteMessage`, `MarkConversationRead`, `RerollProfile` | `AssertMessageExpired`, `AssertReactionPresent`, `AssertMessageEdited`, `AssertMessageTombstoned`, `AssertProfileSynced` |
   | **Phase 7** | `CreateGroup`, `AcceptGroupInvite`, `DeclineGroupInvite`, `SendGroupMessage`, `FloodGroupMessages`, `AddGroupMember`, `EvictGroupMember`, `RenameGroup` | `AssertGroupEpoch`, `AssertGroupMessageDelivered`, `AssertMemberCannotDecrypt` |
   | **Phase 8** | `SetDiscoveryVisibility`, `PublishBlindedLocator`, `ResolveBlindedLocator`, `QueryNearbyPeers`, `ConnectDiscoveredPeer` | `AssertBlindedLocatorResolved`, `AssertNearbyCandidateCount` |
   | **Phase 9** | `SendFilePreset`, `AcceptFileTransfer`, `DeclineFileTransfer`, `PauseFileTransfer`, `ResumeFileTransfer`, `CancelFileTransfer`, `InjectLateDownloader` | `AssertTransferCompleted`, `AssertMerkleRootVerified`, `AssertSwarmMultiSourceDownload` |

   Example committed scenario (`Scenarios/01-direct-handshake-and-chat.json`):
   ```json
   {
     "name": "01-direct-handshake-and-chat",
     "description": "Tests direct 1-on-1 identity creation, handshake approval, and bidirectional chat.",
     "version": 1,
     "steps": [
       { "$type": "SpawnPersonas", "aliases": ["Alice", "Bob"] },
       { "$type": "InitiateDirectHandshake", "initiator": "Alice", "target": "Bob" },
       { "$type": "ApprovePendingHandshake", "recipient": "Bob", "sender": "Alice" },
       { "$type": "SendDirectMessage", "author": "Alice", "recipient": "Bob", "text": "Hello Bob" },
       { "$type": "SendDirectMessage", "author": "Bob", "recipient": "Alice", "text": "Pong #1" }
     ],
     "assertions": [
       { "$type": "AssertDirectRatchetSession", "nodeA": "Alice", "nodeB": "Bob" },
       { "$type": "AssertMessageDelivered", "recipient": "Bob", "author": "Alice", "snippet": "Hello Bob" },
       { "$type": "AssertMessageDelivered", "recipient": "Alice", "author": "Bob", "snippet": "Pong #1" },
       { "$type": "AssertZeroErrors" }
     ]
   }
   ```
2. **Interactive Flight Recorder (`IFlightRecorderService`)**:
   - Tracks interactive user actions in real time:
     - When user clicks `[ + Add Person ]`, records `SpawnPersonasStep`.
     - When user clicks `[ ⚡ Direct Handshake ]`, records `InitiateDirectHandshakeStep`.
     - When user clicks `[ ✓ Accept ]`, records `ApprovePendingHandshakeStep`.
     - When user sends a message, records `SendDirectMessageStep`.
   - **Bot Deduplication**: Autonomous reactions (`AutoReplyPong`) are ignored by the interactive step recorder, but can be added to the scenario's `assertions` block.
   - `CTL-REC-01` toggles `IsRecording` or clears recorded steps.
   - `CTL-REC-02` prompts for a `.json` save location or loads an existing `.json` file for interactive stepping/replay.
3. **Alias-Mapped Scenario Runner (`ScenarioRunnerService`)**:
   - Replay maps logical string aliases (`"Alice"`, `"Bob"`) to dynamically generated runtime `SimulatedNodeId`s and newly leased loopback ports.
   - **Hermetic Clean-Slate**: Before executing any scenario, calls `SimulatedNodeRegistry.ResetAllAsync()` and clears temp SQLite files to prevent state contamination.
4. **Deterministic Step Completion Waiters (Zero Flaky `Task.Delay`)**:
   - Each step awaits an observable domain/network condition before advancing (with a 10s timeout):
     - `SpawnPersonas`: awaits all Kestrel servers bound and listening.
     - `InitiateDirectHandshake`: awaits target node receiving pending handshake in `IPendingHandshakeRepository`.
     - `ApprovePendingHandshake`: awaits both nodes having active `DirectRatchetSession` in `IRatchetSessionRepository`.
     - `SendDirectMessage`: awaits target node firing `ChatMessageReceivedEvent`.
5. **Headless Execution Mode (`--replay <path>`)**:
   - In `Program.cs` / `App.axaml.cs`: if `args` contains `--replay <path>`:
     - Configures Avalonia in offscreen/headless mode.
     - Recursively locates `.json` files if `<path>` is a directory, or executes the single scenario file.
     - Runs each scenario through `ScenarioRunnerService`.
     - Logs step execution and assertion evaluations directly to `ISimulatorLogSink` (`logs/simulator-latest.jsonl` + `stdout`).
     - Exits with `Environment.Exit(0)` if all assertions pass, or `Environment.Exit(1)` on any assertion/step failure.
6. **Phase 4 Verification & Acceptance Criteria**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - Launch app $\rightarrow$ click `[ 🔴 Record ]` $\rightarrow$ add 2 people, handshake, approve, and send 2 messages $\rightarrow$ save as `Scenarios/01-direct-handshake-and-chat.json`.
   - Run `dotnet run --project Percolator.Simulator -- --replay Scenarios/01-direct-handshake-and-chat.json`:
     - Verifies scenario executes headlessly, completes all steps, and exits with code `0`.
     - Verifies `logs/simulator-latest.jsonl` records all step executions and zero errors.

---

### 3.5 Phase 5 Technical Specification (Relay Node Hosting, Relayed Chat & Queue Chaos)
Phase 5 introduces the asynchronous store-and-forward relay boundary, mailbox registration, prekey hosting, and relay queue chaos.

1. **Relay Host Architecture (`SimulatedRelayHost`)**:
   - Implements `ISimulatedRelayHost : IAsyncDisposable`:
     ```csharp
     public interface ISimulatedRelayHost : IAsyncDisposable
     {
         SimulatedNodeId Id { get; }
         Uri BoundEndpoint { get; }
         IServiceProvider Services { get; }
         ValueTask StartAsync(CancellationToken ct = default);
         ValueTask StopAsync(CancellationToken ct = default);
     }
     ```
   - Leases an ephemeral loopback port via `LoopbackPortAllocator` (e.g. `https://127.0.0.1:5201`).
   - Builds an isolated DI container registering:
     - `InMemoryRelayMailboxCapabilityStore` (singleton capability store for mailboxes).
     - `SqliteRelayPreKeyRepository` (SQLite WAL repository for hosted prekeys).
     - `NodeScopedLoggerProvider` (from Phase 2) tagging all relay logs with `[Relay-Alpha]`.
   - Binds `KestrelPeerTransportServer` registering:
     - `UnauthenticatedDeliveryEndpoint` (`FetchPreKeyBundle`, `EnqueueMailbox`, `GetProfile`, `UploadProfile`, `DownloadBlindBlob`, `UploadBlindBlob`).
     - `AuthenticatedRelayEndpoint` (`RegisterMailbox`, `DrainMailbox`, `ConnectMailboxStream`, `UploadPreKeyBatch`).
     - `AnonymousGroupRelayEndpoint` (prepared for Phase 7).
2. **Relay Mailbox & PreKey Provisioning**:
   - Single Persona: `CTL-PER-13` allows selecting a relay, clicking `[ 📬 Register ]` (registers `BlindedRoutingToken`, `DeliveryToken`, `DrainToken`), and `[ 🔑 Publish PreKeys ]` (uploads 25 OPKs).
   - Bulk Person Creation (`BTN-TOP-02`): spawns $N$ identities, registers their mailboxes, and uploads their OPKs in a single operation.
   - Bulk Register / Publish (`BTN-ACT-05` / `BTN-ACT-06`): provisions all currently selected roster nodes to the selected relay.
3. **Relayed Handshake Initiation (`BTN-ACT-03`)**:
   - Initiator queries Relay's `FetchPreKeyBundle` endpoint for Target's hosted `PreKeyBundle`.
   - Initiator performs X3DH initiation, wraps the handshake into a `SealedEnvelopeProto`, and dispatches via `EnqueueMailbox` to Target's `BlindedRoutingToken`.
   - If Target is in `Muted` (`IsListening = false`), envelope sits in Relay's queue; Relay Card lights up `Held Envelopes: 1`.
   - When Target toggles `IsListening = true`, `ConnectMailboxStream` reconnects, drains envelope, pops Pending Handshake banner (`CTL-PER-14`), and establishes session upon approval.
4. **Relayed Store-and-Forward Chat**:
   - Author sends message via `DeliveryRouteType.RelayedOneToOne`.
   - Envelope buffers in Target's mailbox queue on the relay.
   - When Target drains, `ChatPayloadHandler` decrypts payload and delivers message to chat view.
5. **Relay Queue Chaos Controller (`IRelayQueueChaosController`)**:
   - The Relay Card (`RelayCardView`) inspects held envelopes:
     - `CTL-REL-01` (`[ 👯 Duplicate ]`): Clones the selected queued `MailboxEnvelope`. When Target drains, `IHandshakeReplayFilter` drops duplicate, proving replay defense.
     - `CTL-REL-02` (`[ ⚡ Flip 1 Bit ]`): XORs 1 bit in ciphertext payload. When Target drains, AEAD decryption fails, proving tamper rejection.
     - `CTL-REL-03` (`[ 🔀 Shuffle Queue Order ]`): Shuffles envelope list order, proving Double Ratchet skipped-keys cache handles out-of-order delivery.
6. **PreKey Depletion & Auto-Replenishment**:
   - Each X3DH handshake consumes 1 OPK from `SqliteRelayPreKeyRepository`.
   - Relay Card displays live count: `Hosted OPKs: N / 25`.
   - When count drops below 5 (watermark): if persona has `AutoReplenishPreKeys = true` (`CTL-PER-09`), client background worker automatically uploads a fresh batch of 25 OPKs.
7. **Phase 5 Verification & Mandatory Regression Gate**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - **Regression Gate**: Runs `dotnet run -- --replay Scenarios/01-direct-handshake-and-chat.json` $\rightarrow$ verifies exit code `0` and zero regressions in `logs/simulator-latest.jsonl`.
   - Interactive Verification: Add Relay $\rightarrow$ Add 2 People assigned to Relay $\rightarrow$ Put Bob in Muted $\rightarrow$ Alice sends relayed handshake $\rightarrow$ verify envelope held at Relay $\rightarrow$ test `[ Duplicate ]` or `[ Flip 1 Bit ]` $\rightarrow$ unmute Bob $\rightarrow$ verify delivery and tamper rejection.
   - Record and commit `Scenarios/02-relayed-stealth-and-chat.json` and `Scenarios/03-prekey-depletion.json`.

---

### 3.6 Phase 6 Technical Specification (Advanced Chat Features, Virtual Clock & Encrypted Profiles)
Phase 6 completes 1-on-1 chat rich features, virtualized time acceleration, and encrypted profile/Identicon synchronization.

1. **Virtual Clock Controller (`IVirtualizedTimeController`)**:
   - Governs simulated time across all personas and relays via a controllable `TimeProvider`:
     ```csharp
     public interface IVirtualizedTimeController
     {
         DateTimeOffset UtcNow { get; }
         void AdvanceTime(TimeSpan delta);
     }
     ```
   - `BTN-TOP-04` (`[ 🕒 +1 Min ]`): calls `AdvanceTime(TimeSpan.FromMinutes(1))`.
   - `BTN-TOP-05` (`[ 🕒 +1 Hour ]`): calls `AdvanceTime(TimeSpan.FromHours(1))`.
   - Triggers scheduled pruning timers, outbox backoffs, and disappearing message sweeps without real-world waiting.
2. **Disappearing Messages (`CTL-PER-19`)**:
   - Compose bar timer dropdown: `Off`, `1m` (60s), `1h` (3600s).
   - Populates `SendMessageCommand.ExpireAfterSeconds`.
   - Both sender and receiver calculate `ExpiresAtUtc = ReceivedAtUtc + ExpireAfterSeconds`.
   - When virtual clock advances past `ExpiresAtUtc`, `DisappearingMessagePruner` deletes message from repository; `PersonaCardViewModel` removes bubble from UI!
3. **Reactions, Edits & Deletions (`CTL-PER-20`)**:
   - Hovering over any chat message bubble reveals action buttons:
     - `[ 👍 React ]`: flyout with emoji palette (`👍`, `❤️`, `😂`, `🔥`, `🎉`). Calls `IChatService.AddReactionAsync(new AddReactionCommand(ChannelId, AuthorId, PayloadId, Emoji))`.
     - `[ ✏️ Edit ]`: opens inline edit box. Calls `IChatService.EditMessageAsync(new EditMessageCommand(ChannelId, AuthorId, PayloadId, NewContent))`. Updates message text and displays `(edited)` indicator.
     - `[ 🗑️ Delete ]`: calls `IChatService.DeleteMessageAsync(new DeleteMessageCommand(ChannelId, AuthorId, PayloadId))`. Replaces message bubble with tombstone: *"🚫 This message was deleted"*.
4. **Read Receipts (`CTL-PER-05`)**:
   - Each message bubble renders delivery state ticks:
     - `✓` (Sent to transport).
     - `✓✓ Grey` (Delivered to peer).
     - `✓✓ Blue` (Read by peer).
   - When Target opens the conversation: if Target has `SendReadReceipts = true` (`CTL-PER-05`), Target invokes `IChatService.MarkAsReadAsync(new MarkAsReadCommand(...))`. Author receives receipt and flips tick icon to blue!
5. **Encrypted Profile & Identicon Sync (`CTL-PER-01` / `CTL-PER-10`)**:
   - Each persona generates its 64x64 PNG Identicon avatar.
   - Persona encrypts avatar PNG using AES-GCM (`IBlobCryptoService`) with an ephemeral `AvatarKey`, and uploads to Relay's `UploadBlindBlob` endpoint via `RelayBlobClient`.
   - Persona serializes `ProfileMetadata` (display name, avatar blob ID, revision) and encrypts into a 1040-byte ISO/IEC 7816-4 padded block via `ProfileManager.EncryptProfile` with `ProfileKey`, uploading to Relay's `UploadProfile` endpoint.
   - Upon contact approval: if `AutoShareProfileOnConnect = true` (`CTL-PER-10`), persona dispatches `ShareProfileCapabilityCommand` to peer.
   - Peer fetches encrypted profile and avatar blind blob from relay, decrypts, and replaces the grey `?` avatar placeholder with the real colored Identicon!
   - `[ 🎲 Reroll Profile ]` (`CTL-PER-01`): generates a new Word-Pair name and Identicon seed, uploads the new revision, and pushes an updated profile capability to contacts.
6. **Phase 6 Verification & Mandatory Regression Gate**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - **Regression Gate**: Runs `dotnet run -- --replay Scenarios/` $\rightarrow$ verifies `01`, `02`, and `03` pass with exit code `0`.
   - Interactive Verification: Send message with 1m timer $\rightarrow$ click `[ 🕒 +1 Min ]` $\rightarrow$ verify message disappears; react with `👍` $\rightarrow$ verify count updates; click `[ 🎲 Reroll Profile ]` $\rightarrow$ verify peer avatar updates.
   - Record and commit `Scenarios/04-advanced-chat-and-profiles.json`.

---

### 3.7 Phase 7 Technical Specification (Group Messaging, ZK Relay Ledger, Roster Mutations & Group Flood)
Phase 7 establishes scalable group communication using SenderKey ratchets, zero-knowledge presentation proofs against the home relay's ledger, epoch rotation upon membership mutations, and group message flood testing.

1. **Group Genesis & Home Relay Topology (`BTN-ACT-07`)**:
   - Enabled in Zone 2 when 2+ personas are checked in the Roster (`MultiSelectedNodes`).
   - Clicking `[ 👥 Create Group on Relay... ]` opens a FluentAvalonia flyout:
     - `Group Name`: editable text (defaults to `"Crypto Squad"`, `"Alpha Unit"`, etc.).
     - `Home Relay`: dropdown listing all active `SimulatedRelayHost` instances (defaults to first active relay, e.g. `Relay-Alpha`).
     - `Admin`: designated creator persona (defaults to `PrimarySelectedNode`).
   - Group Creator executes `GroupChannel.CreateGenesis(channelId, creatorId, groupName, timeProvider)`.
   - Initializes `RelayGroupLedger` on the home relay via `AnonymousGroupRelayEndpoint`, publishing `ZkGroupPublicParams` and the initial roster's `BlindedRoutingToken` set.
   - Roster Category `Groups` (`CTL-ROS-04`) populates with the newly created group.
2. **Group Invitations & Ingress Caching (`CTL-PER-11`, `CTL-PER-15`)**:
   - Admin dispatches pairwise `GroupInvitationDto` to each member via `GroupInvitationService.SendInvitationAsync` over direct or relayed channels (`ChatPayloadType.GroupInvitation`).
   - Member Handling:
     - If `AutoAcceptGroupInvites = true` (`CTL-PER-11`), invitee immediately calls `AcceptInvitationAsync`, joins `SubscribeGroupStream`, and exchanges sender keys.
     - If `false`, invitee's card renders a pending banner (`CTL-PER-15`):
       *`[ Group Invite: "Alpha Unit" from Crimson Falcon ] [ ✓ Join Group ] [ ✕ Decline ]`*.
   - **Out-of-Order Message Caching Guard (`IUnknownGroupMessageCacheRepository`)**:
     - If other members send messages to the group before an invitee accepts, the invitee's ingress catches the unrecognized channel payloads and parks them in `IUnknownGroupMessageCacheRepository`.
     - Upon clicking `[ ✓ Join Group ]`, the client drains the cached envelopes, decrypts them with the newly received SenderKey, and inserts them into the group transcript in chronological order!
3. **SenderKey Ratchet & Pairwise Key Distribution (`GroupKeyDistributionService`)**:
   - Author generates a `GroupSenderKeyRatchet` with an initial symmetric chain key and ephemeral signing key.
   - Exports the 89-byte distribution payload: `[MessageType(0x01) || ChannelId(16B) || KeyId(4B) || Iteration(4B) || ChainKey(32B) || AuthorSigningPublicKey(32B)]`.
   - Dispatches distribution payload to all active group members via pairwise `AppId.SystemControl` messages.
   - Recipient members save the session into `IGroupReceiverSessionRepository` as a `GroupReceiverSession`.
   - Subsequent group messages from this author are encrypted once using the sender key iteration and dispatched via the home relay—**avoiding $O(N)$ network fanout** on the sender device.
4. **Anonymous Relay Fanout & ZK Presentation Proofs (`AnonymousGroupRelayEndpoint`)**:
   - Activates `AnonymousGroupRelayService` on Phase 5's Kestrel `SimulatedRelayHost`.
   - **Subscribing (`SubscribeGroupStream`)**:
     - Client submits `GroupStreamClientMessage` containing `ChannelId`, `CurrentEpoch`, and a `zk_presentation_proof` generated via `ZkgroupCryptographyService` / `IZkProofEngine`.
     - Relay verifies proof against `RelayGroupLedger.ActiveRoutingTokens` without learning the client's identity, establishing a server-push gRPC stream.
   - **Dispatching (`DispatchGroupMessage`)**:
     - Author submits `DispatchGroupMessageRequest` with ciphertext, `sender_key_iteration`, and `zk_presentation_proof`.
     - Relay verifies presentation proof, records dispatch digest in `_recentDispatchDigests` (replay defense), and fans out a `GroupBroadcastEnvelope` to all active client streams.
5. **Roster Mutations, Epoch Advancement & Forward Secrecy (`CTL-GRP-01`)**:
   - Group Panel (`CTL-GRP-01`):
     - `[ + Add Member ▼ ]`: picks an unjoined persona $\rightarrow$ dispatches invite $\rightarrow$ upon acceptance, admin invokes `CommitRosterMutationRequest` with the new routing token $\rightarrow$ sender distributes fresh sender keys so the new member can decrypt future messages, but cannot read historical messages.
     - `[ ✕ Evict Member ]`: Admin selects a member and clicks Evict. Admin calls `CommitRosterMutationRequest` advancing `EpochNumber` ($E \rightarrow E+1$) and revoking the member's `BlindedRoutingToken` on the relay. All remaining members generate fresh `GroupSenderKeyRatchet` instances and distribute them pairwise.
     - **Forward Secrecy Verification**: Evicted member's stream is disconnected by relay; even if the evicted member captures subsequent broadcasts, they cannot decrypt messages sent in epoch $E+1$.
     - `[ ✏️ Rename Group ]`: Updates group metadata and broadcasts group info update.
6. **Group Message Flood Testing (`CTL-GRP-02`)**:
   - Clicking `[ 🌊 Flood 25 Messages ]`: Dispatches 25 rapid-fire group messages through `SendMessageCommand`.
   - Tests SenderKey ratchet chain progression (iterating 25 steps) and verifies that all connected group member UI cards update concurrently without dropped frames or UI thread deadlock.
7. **Phase 7 Verification & Mandatory Regression Gate**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - **Regression Gate**: Runs `dotnet run -- --replay Scenarios/` $\rightarrow$ verifies `01`, `02`, `03`, and `04` pass with exit code `0`.
   - Interactive Verification: Create group with 3 members $\rightarrow$ send group messages $\rightarrow$ test flood 25 messages $\rightarrow$ evict 1 member $\rightarrow$ send message from remaining member $\rightarrow$ verify evicted member cannot receive or decrypt.
   - Record and commit `Scenarios/05-group-chat-and-epochs.json`.

---

### 3.8 Phase 8 Technical Specification (Peer Discovery: Blinded Rendezvous, 24h Epoch Rotation & Nearby Candidates)
Phase 8 establishes zero-knowledge blinded rendezvous discovery and public nearby peer discovery directories on the simulated relay, along with 24-hour temporal epoch rotation.

1. **Dual Discovery Architecture & UI Integration**:
   - The Discovery Tab in `PersonaCardView` exposes two distinct discovery pathways:
     - **Modality A: Private Blinded Rendezvous (`CTL-PER-21`)**: For two personas who share an existing contact secret or pairwise token and wish to locate each other without exposing their network presence, identity keys, or social graph to the relay.
     - **Modality B: Nearby / Local Peer Directory (`CTL-PER-22`)**: For QA testers and sandbox personas to discover open peers registered on the relay.
2. **Private Blinded Rendezvous Workflow (`CTL-PER-21`)**:
   - **Daily Rotating Locator Derivation**:
     - Calculates `currentEpoch = DiscoveryEpoch.FromUtc(virtualClock.UtcNow)`.
     - Computes 32-byte `BlindedLocator = HMAC-SHA256(currentEpoch.Value, sharedContactSecret)`.
   - **Publication (`PublishRendezvous`)**:
     - Persona serializes its direct loopback endpoint or relay mailbox routing token, encrypts it with the shared secret, and calls `PrivateRendezvousRelayService.PublishRendezvous(new BlindedRendezvousPublishProto { BlindedLocator = ..., EncryptedRoute = ... })`.
   - **Resolution (`ResolvePeerLocator`)**:
     - Discoverer computes identical `BlindedLocator` for the current day and calls `QueryRendezvous`.
     - Relay returns the encrypted route without learning who either party is.
     - Discoverer decrypts the route and initiates a standard X3DH direct or relayed handshake.
3. **Nearby Discovery Directory & Admission Gate (`CTL-PER-22`)**:
   - Hosted on `SimulatedRelayHost` via `NearbyDiscoveryService` (`discovery.proto`) backed by `InMemoryRelayPeerDirectory`.
   - **Visibility Policy (`CTL-PER-03` `[x] Allow Peer Discovery`)**:
     - If checked: persona registers `RelayPeerRegistration` with `PeerDiscoveryVisibility.Public`.
     - If unchecked: persona sets `Visibility = PeerDiscoveryVisibility.Invisible` (or remains unregistered).
   - **Querying Nearby Peers (`[ 📡 Query Nearby Peers ]`)**:
     - Invokes `INearbyPeerDiscoveryWorkflow.QueryNearbyPeersAsync(requesterId, targetRelayId, limit)`.
     - Relay directory returns public candidates, automatically excluding `Invisible` personas and the requester's own ID (`candidate.PeerId != requesterId`).
4. **Candidate List UI & One-Click Connection (`CTL-PER-22`)**:
   - Renders a list of `DiscoveredPeerItemViewModel` cards in the Persona Card's Discovery tab:
     - Discovered peer's 64x64 Identicon avatar (resolved via Phase 6 encrypted profile).
     - Display name (`"Amber Fox"`).
     - Routing descriptor badge (`Direct Loopback` or `Relay Mailbox`).
     - `[ 🤝 Connect ]` button.
   - Clicking `[ 🤝 Connect ]` executes `INearbyPeerDiscoveryWorkflow.ConnectToDiscoveredPeerAsync`, initiating an X3DH handshake. Upon peer acceptance, the session establishes and immediately opens in the Chat tab.
5. **24-Hour Epoch Rotation & Directory Eviction (`BTN-TOP-06`)**:
   - Clicking `[ 🕒 +24 Hours ]` calls `IVirtualizedTimeController.AdvanceTime(TimeSpan.FromHours(24))`.
   - Advances `virtualClock.UtcNow`, rolling `DiscoveryEpoch` to the next day index ($D \rightarrow D+1$).
   - Calls `InMemoryRelayPeerDirectory.EvictExpired(UtcNow)`, automatically purging directory registrations older than `DefaultRegistrationTtl` (24 hours).
   - Verifies that previous blinded locators expire, proving temporal unlinkability and forward secrecy against directory harvesting.
6. **Phase 8 Verification & Mandatory Regression Gate**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - **Regression Gate**: Runs `dotnet run -- --replay Scenarios/` $\rightarrow$ verifies `01`, `02`, `03`, `04`, and `05` pass with exit code `0`.
   - Interactive Verification: Add 3 personas $\rightarrow$ Alice & Bob check `Allow Discovery`, Charlie leaves it unchecked $\rightarrow$ Alice clicks `[ 📡 Query Nearby Peers ]` $\rightarrow$ Bob appears in candidate list, Charlie does not $\rightarrow$ Alice clicks `[ 🤝 Connect ]` $\rightarrow$ handshake succeeds $\rightarrow$ click `[ 🕒 +24 Hours ]` $\rightarrow$ verify old registrations evicted.
   - Record and commit `Scenarios/06-peer-discovery.json`.

---

### 3.9 Phase 9 Technical Specification (Swarm File Transfer, Late Joiners & Custom Piece Heatmap)
Phase 9 completes the simulator harness by wiring multi-stream BitTorrent-style swarm file transfers, custom piece heatmap visualization, late downloader dynamic injection, and verifying 100% operational UI controls with zero remaining stubs.

1. **Synthetic Deterministic File Presets (`BTN-ACT-04`)**:
   - `[ 📁 Send File Preset ▼ ]` flyout offers `2 MB` (Fast Sanity Check), `25 MB` (Standard Multi-Chunk Swarm), and `100 MB` (High-Throughput Torture Test).
   - Generates deterministic pseudo-random chunks in Initiator's temp run directory (`temp/runs/{sessionId}/{nodeId}/transfers/`), calculating **SHA-256 Merkle leaf and root hashes** (`TransferManifestProto.merkle_root` and `concatenated_leaves`). Zero large test binary files committed to source control.
2. **Transfer Offer, Ingress Filtering & Autopilot (`CTL-PER-04`, `CTL-PER-12`, `CTL-PER-16`)**:
   - Initiator calls `IFileTransferActionHandler.InitiateTransferAsync(channelId, initiatorId, recipients, rootPath, filePaths, wanPolicy)`.
   - Dispatches `FileTransferOfferProto` over direct or relayed pairwise channels.
   - Recipient Handling:
     - If `Allow File Transfers = false` (`CTL-PER-04`), immediately rejects offer with `TransferTerminationReason.PolicyRejected`.
     - If `AutoAcceptFileTransfers = true` (`CTL-PER-12`), receiver automatically calls `AcceptTransferAsync` to a designated scratch folder.
     - If `false`, receiver card pops pending banner (`CTL-PER-16`):
       *`[ Incoming File: "test_25mb.bin" (25 MB) from Crimson Falcon ] [ ✓ Accept Download ] [ ✕ Decline ]`*.
3. **Routing Mode Enforcement: Direct Multi-Stream vs. Relay Pipe (`CTL-PER-06`)**:
   - If recipient has `Relay-Only Routing (Hide IP)` (`CTL-PER-06`) checked:
     - `FileTransferAcceptProto` sets `routing_preference = RoutingPreference.RelayOnly`.
     - `TransferSessionCoordinator` forces all 4–8 transfer connections through `RelayTransferPipeClient` on the home relay, hiding peer IP addresses.
   - If unchecked:
     - Connects directly via `TcpMultiStreamPoolFactory` using loopback TCP streams (`TCP_NODELAY = true`), maximizing transfer speed.
4. **Swarm Dashboard & Custom `SwarmHeatmapCanvas` (`CTL-ROS-05`, `CTL-SWM-03`)**:
   - Selecting `Swarm Row` in Left Roster (`CTL-ROS-05`) opens the Swarm Panel in the Center Workbench.
   - Avalonia Custom Control: `SwarmHeatmapControl : Control` overrides `Render(DrawingContext context)`:
     - Draws an adaptive 64-to-256 block grid representing the file's SHA-256 Merkle chunk tree:
       - `Grey (#333333)`: Missing / Unrequested chunk.
       - `Blue (#2196F3)`: In-Flight chunk across active parallel streams.
       - `Green (#4CAF50)`: Verified chunk (SHA-256 leaf hash match).
       - `Red (#F44336)`: Corrupted chunk (hash mismatch, re-queued for re-download).
     - Renders per-participant progress bars (`N %`), active transfer speed gauges (MB/s via `TokenBucketRateLimiter`), and stream health indicators.
5. **Participant Controls & Dynamic Late Downloader Injection (`CTL-SWM-01`, `CTL-SWM-02`)**:
   - Participant Row (`CTL-SWM-01`): `[ ⏸️ Pause ]`, `[ ▶️ Resume ]`, and `[ ✕ Cancel ]` calls corresponding methods on `IFileTransferActionHandler`.
   - Late Joiner Injection (`CTL-SWM-02`):
     - `[ ➕ Inject Late Downloader ▼ ]`: Selects an unjoined persona and triggers injection when the initial transfer reaches 50% progress.
     - Late joiner receives `FileTransferOfferProto`, connects to the swarm, and downloads pieces concurrently from **both the initial seeder AND the partial downloader** (who shares already verified chunks via `resumption_bitfield`).
     - Demonstrates decentralized P2P bitfield gossiping and proves swarm efficiency.
6. **Zero Orphaned Stubs Mechanical Audit & Final Regression Gate**:
   - `dotnet build Percolator.Simulator` compiles with 0 warnings/errors.
   - **Regression Gate**: Runs `dotnet run -- --replay Scenarios/` $\rightarrow$ verifies `01`, `02`, `03`, `04`, `05`, and `06` pass with exit code `0`.
   - Interactive Verification: Add 3 personas $\rightarrow$ Alice seeds 25 MB to Bob $\rightarrow$ observe live chunk updates on `SwarmHeatmapCanvas` $\rightarrow$ at 50% inject Charlie as late downloader $\rightarrow$ verify Charlie pulls chunks from both Alice and Bob $\rightarrow$ verify file completes and matches the SHA-256 Merkle root hash.
   - Record and commit `Scenarios/07-swarm-late-joiner.json`.
   - **100% UI Operational Verification**: Automated test scans all 53 controls in Section 2, verifying **0 controls remain bound to `ShowPlaceholder` or no-op commands**—all 53 controls are 100% operational.

---

## 4. Vertical Feature Slice Architecture & File Inventory (Lens 1)

### 4.1 Complete Vertical Feature Slice Tree
Zero horizontal bucket folders (`Models/`, `ViewModels/`, `Views/`, `Services/`, `Common/`) are permitted. Every View, ViewModel, orchestrator, and state object lives inside its autonomous feature slice under `Features/`:

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
│   │   ├── ISelectionStateService.cs
│   │   ├── SelectionStateService.cs
│   │   ├── IStatusToastService.cs
│   │   ├── StatusToastService.cs
│   │   └── DesignDataService.cs
│   ├── Logging/
│   │   ├── SimulatorLogEntry.cs
│   │   ├── ISimulatorLogSink.cs
│   │   ├── MultiSinkLogEngine.cs
│   │   ├── NodeScopedLoggerProvider.cs
│   │   ├── LogDockView.axaml / .cs
│   │   └── LogDockViewModel.cs
│   ├── Topology/
│   │   ├── ISimulatedPersonaHost.cs
│   │   ├── SimulatedPersonaHost.cs
│   │   ├── ISimulatedNodeRegistry.cs
│   │   ├── SimulatedNodeRegistry.cs
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
│   │   ├── IFlightRecorderService.cs
│   │   ├── FlightRecorderService.cs
│   │   ├── ScenarioRunnerService.cs
│   │   ├── HeadlessReplayRunner.cs
│   │   ├── FlightRecorderView.axaml / .cs
│   │   └── FlightRecorderViewModel.cs
│   ├── RelayedChat/
│   │   ├── ISimulatedRelayHost.cs
│   │   ├── SimulatedRelayHost.cs
│   │   ├── RelayProvisioningCoordinator.cs
│   │   ├── IRelayQueueChaosController.cs
│   │   ├── RelayQueueChaosController.cs
│   │   ├── RelayCardView.axaml / .cs
│   │   └── RelayCardViewModel.cs
│   ├── RichChatAndProfiles/
│   │   ├── IVirtualizedTimeController.cs
│   │   ├── VirtualizedTimeController.cs
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
│   │   └── DiscoveryTabView.axaml / .cs
│   └── SwarmSimulation/
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

### 4.2 Zero Orphaned Stubs Verification & Final Cumulative Gate
When Phase 9 is complete:
1. Every single control from the 53-item inventory (`BTN-TOP-01` through `CTL-LOG-02`) is wired to real domain/application logic.
2. The entire regression suite (`Scenarios/01` through `07`) executes in a single pass under `dotnet run -- --replay Scenarios/` with exit code `0`.
3. `logs/simulator-latest.jsonl` contains zero assertion failures and zero unhandled exceptions.
