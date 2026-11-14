<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md and source/spec/design-spec.md -->

# Percolator.Desktop

Spec: [design-spec](../../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Desktop` is the cross-platform Avalonia UI presentation and composition-root deployable. It hosts the two-stage dependency injection lifecycle, coordinates the vault unlock and network stealth gates, implements the presentation sinks of [`Percolator.PluginSdk`](../../Percolator.PluginSdk/Design.md), and renders reactive MVVM views over the application and infrastructure layers (realizing [`DEP-DESKTOP-SEGREGATION-ROLE`](./deployable-requirements.md#1-segregation-role-and-boundary-constraints)).

- **In Scope:** Bootstrap/Stage-2 container handoff, vault unlock and identity-selection UI gates, UI-agnostic stateful presentation services, transient R3 reactive ViewModels, Avalonia views, and custom P2P drawing controls.
- **Out of Scope:** Domain business rules, cryptographic ratchets, SQL queries, and multi-peer network simulation (segregated into `Percolator.Simulator`).

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-DESKTOP-TWO-STAGE-HANDOFF` (Two-Stage Container Handoff & Window Coordinator):**
   * *Realizes:* [`DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE`](./deployable-requirements.md#3-public-contract-invariants) and [`DEP-DESKTOP-STARTUP-VAULT-AUTHENTICATION`](./deployable-requirements.md#2-allocated-responsibilities).
   * *Mechanism:* `App.axaml.cs` builds a minimal root `IServiceCollection` hosting only presentation coordinators (`IWindowManager`, `IWindowHandoffCoordinator`, `IDatabaseUnlockCoordinator`) and displays `StartupWindow` ($480 \times 600\text{ px}$). Authenticated domain services and database connection pools are instantiated strictly within an ephemeral child `IHost` composed post-unlock by the executable-owned `ISessionHostFactory` (`DesktopStage2SessionFactory`) from Infrastructure `IServiceCollection` slices plus Desktop session registrations. Window transition to `MainWindow` ($1200 \times 800\text{ px}$) occurs via `IWindowHandoffCoordinator` only after database migrations complete. `#session-lock`

2. **`INV-DESKTOP-STEALTH-ACTIVATION-GATE` (Identity Activation Gate & Network Ingress Inversion):**
   * *Realizes:* [`DEP-DESKTOP-PRE-ACTIVATION-STEALTH`](./deployable-requirements.md#2-allocated-responsibilities) and [`DEP-DESKTOP-SESSION-LOCK-ZEROIZATION`](./deployable-requirements.md#2-allocated-responsibilities).
   * *Mechanism:* `IdentityGateViewModel` acts as the single gatekeeper for network listener startup: transport listeners (`KestrelPeerTransportServer`, loopback discovery sockets) start strictly upon explicit `ActivatePersonaCommand` execution. Calling `LockSessionCommand` executes reverse teardown: re-instantiates `StartupWindow` first, closes `MainWindow`, and awaits its `Closed` event so deferred vault persistence writes (e.g. shell window geometry) complete while the session container is still live; then invokes `IDatabaseUnlockCoordinator.LockAsync()` — clears SQLite connection pools (`SqliteConnection.ClearAllPools()`), zeroizes key buffers (`CryptographicOperations.ZeroMemory`), and disposes the Stage 2 container. `#session-lock`

3. **`INV-DESKTOP-PRESENTATION-MODELS` (Domain Purity & Presentation-Owned Reactive Models):**
   * *Realizes:* [`DEP-DESKTOP-PRESENTATION-MODEL-ISOLATION`](./deployable-requirements.md#3-public-contract-invariants).
   * *Mechanism:* Domain aggregate entities never implement `INotifyPropertyChanged` and are never bound directly to XAML views. Presentation models (`Features.*.Models.*Model`) project immutable C# `record` snapshots (`.ToSnapshot()`) for lock-free background reads, caching, and export, isolating the domain model from UI thread locks.

4. **`INV-DESKTOP-STATEFUL-PROJECTIONS` (UI-Agnostic State Services vs. Transient ViewModels):**
   * *Realizes:* [`DEP-DESKTOP-PRESENTATION-MODEL-ISOLATION`](./deployable-requirements.md#3-public-contract-invariants) and [`DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION`](./deployable-requirements.md#2-allocated-responsibilities).
   * *Mechanism:* Cross-view state is owned by thread-safe singleton state services (`ChatStateService`, `DiscoveryStateService`, `ChatDraftStateService`) maintaining canonical `ObservableList<T>` / `ObservableDictionary<TKey, TValue>` collections without UI dispatcher references. ViewModels are transient projections created via `.CreateView(...)` whose subscriptions and child scopes are deterministically cleared on item removal (`.ObserveRemove()`).

5. **`INV-DESKTOP-THREAD-DISCIPLINE` (Filter Early, Marshal Late Reactive Pipeline):**
   * *Realizes:* [`DEP-DESKTOP-UI-THREAD-MARSHALING`](./deployable-requirements.md#3-public-contract-invariants).
   * *Mechanism:* Reactive pipelines process filtering, deduplication, and sampling (`.Sample(TimeSpan.FromMilliseconds(16), _timeProvider)`) on background worker threads, marshaling to the UI thread exclusively at the terminal step via `.ObserveOnUIThread()`. Worker threads are prohibited from enumerating or mutating UI-bound synchronized views.

---

## 3. Key Technical Design Choices

1. **`DEC-DEBOUNCED-DROP-RELOAD` (Debounced `AwaitOperation.Drop` Reload Coordinators):**
   - *Design Choice:* Bursts of inbound domain events trigger read-model refreshes through an R3 `.Debounce(...).SelectAwait(..., AwaitOperation.Drop)` pipeline so redundant triggers arriving while a database read is already in flight are dropped rather than queued. `#cqrs-projection`
   - *Rejected Alternative:* Firing a database query per inbound event or subscribing to a global "state changed" event that clears and repopulates UI collections, causing SQLite lock contention and UI frame stutter.
2. **`DEC-LRU-SESSION-SCOPES` (LRU-Cached Conversation Scopes):**
   - *Design Choice:* Active conversation child DI scopes (`IServiceScope` + `ChatViewModel` via `SessionScopeFactory`) are held in a bounded LRU cache (capacity 3–5) so switching between recent chats preserves message drafts and scroll position while evicting and disposing older scopes cleanly.
   - *Rejected Alternative:* Singleton ViewModels per conversation (unbounded RAM/subscription growth) or recreating ViewModels on every tab switch (destroying drafts and scroll state).
3. **`DEC-DIRECT-CANVAS-BITFIELD` (Direct-Canvas Bitfield Rendering):**
   - *Design Choice:* Large file transfer bitfields (`PieceMapControl`) render directly onto an Avalonia `DrawingContext` rather than instantiating thousands of individual XAML visual tree elements. `#bitfield-swarm`
   - *Rejected Alternative:* Binding an `ItemsControl` of individual rectangle shapes to chunk arrays, which exhausts the Avalonia visual tree and layout measurement queue.
4. **`DEC-REACTIVE-EVENT-BUS-BRIDGE` (R3 Event Observable Bridge & Explicit UI Thread Marshaling):**
   - *Design Choice:* Presentation components bridge to the inward application event bus via `ObserveEvent<TEvent>()` and `ObserveEventOnUIThread<TEvent>()` in `EventBusExtensions` (consuming [`INV-APP-EVENT-BUS-DECOUPLING`](../../Percolator.Application/Design.md#2-architectural-invariants-and-encapsulation)). Subscriptions observing UI-bound state must marshal to `AvaloniaSynchronizationContext`, preserving the thread-agnostic core of the application bus while preventing cross-thread Avalonia UI mutation exceptions.
   - *Rejected Alternative:* Forcing the application event bus to be aware of Avalonia UI dispatchers or `SynchronizationContext`, which breaks headless unit tests, introduces UI deadlock hazards, and violates Onion Architecture.
5. **`DEC-CANARY-ERROR26-UNWRAP` (Isolated Canary Query for Database Unlock Verification):**
   - *Design Choice:* To verify user-provided passphrases without trial decryption of sensitive data tables, the unlock coordinator executes an isolated canary query `SELECT count(*) FROM sqlite_master;` immediately following Argon2id key derivation, trapping native SQLite error code `26` (`SQLITE_NOTADB`).
   - *Rejected Alternative:* Querying identity or contact tables directly, which exposes schema details to memory inspection during incorrect password attempts.
6. **`DEC-PULL-BASED-CONSENT-STAGING` (Pull-Based Staging vs. Intrusive Push Modals):**
   - *Design Choice:* Inbound unsolicited handshakes and connection offers are staged in a silent background queue and visualized exclusively by illuminating a subtle badge on the Left Sidebar Pending Requests button (realizing [`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`](./deployable-requirements.md#2-allocated-responsibilities)).
   - *Rejected Alternative:* Displaying modal dialogs, popup windows, or OS notification toasts, which leak presence to unauthenticated network probers and interrupt the user.
