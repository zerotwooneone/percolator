# Main Window + Simulator Handshake Enablement Plan

Goal: make the **Main Window** and **Simulator Window** capable of performing **handshakes in both directions** (Main -> Simulator peer, Simulator peer -> Main), while implementing the Main Window UI behaviors described in `design/main-window.md`.

Protocol anchor:

- The handshake UX described here is primarily the **Reverse-Signal** flow from `session-flow.md` Part 2.
  - The “inviter” sends an **invitation** (`EstablishDirectSessionRequest` containing `InviteHandshakeRequestPayload`).
  - The “acceptor” queues the invitation for approval.
  - On acceptance, the acceptor initiates X3DH as the initiator and replies with an **`InviteHandshakeResponse`** (containing the first ratchet message).
- “Direct vs Relayed” is a **transport routing** choice; crypto payloads remain the same.

Routing model (important for chunk correctness):

- **Direct**: invitation/response are delivered via the callback endpoint using `inviter_host` + `inviter_port` embedded in the invite payload.
- **Relayed**: invitation/response are delivered as opaque bytes through the relay queue (relay host peer id).

Constraints / notes:

- Chunks are intentionally large.
- The project does not need to be buildable or testable between chunks.
- Prioritize **handshake plumbing + UI** over full visual polish.
- Replace the current “Add peer” context menu entry points with a **Connection Management** dialog.
- UI iconography assumes the user has NerdFont available (see `Shared/Theme/Icons.xaml`). 

---

## Chunk A 

Outcome:

- `SimulatorStateService` is a **pure singleton orchestrator** (R3 “pure service” pattern):
  - No `IUiDispatcher` injection.
  - Exposes state only as `IReadOnlyObservableList<T>` / `IReadOnlyObservableDictionary<TKey,TValue>`.
  - Performs domain mutations directly on the calling thread under domain gates (`_peerGate`, `_relayGate`).
- All WPF thread-bridging is owned by **ViewModels** (WPF MVVM guidelines):
  - Collection projections marshal using the injected dispatcher: `IUiDispatcher.CollectionEventDispatcher`.
  - Property projections marshal using `.ObserveOnCurrentSynchronizationContext()`.
- Simulator UI and unit tests are updated to the new boundaries.

Critical review notes (what must change):

- The service currently performs UI-thread marshalling via `_ui.InvokeAsync(...)` when mutating domain `ObservableList<T>`.
  - This violates the R3 “UI-agnostic mandate” and couples the service to WPF.
- Once the service becomes UI-agnostic, domain collections will be mutated from background threads.
  - Any VM binding to `ToNotifyCollectionChanged()` without a dispatcher will throw `NotSupportedException`.
  - Therefore, every simulator VM that binds to projected collections must explicitly marshal collection change events.

Affected ViewModels (must be audited and likely updated):

- `HandshakeSimulatorViewModel`
- `SimulatedHandshakeStateMachineCardViewModel`
- `SimulatedPeerCardViewModel`
- `SimulatedPeerItemViewModel`
- `SimulatedRelayQueueItemViewModel`
- `SimulatedRelayQueuePanelViewModel`
- `SimulatorDiagnosticsTabViewModel`
- `SimulatorHandshakesTabViewModel`
- `SimulatorPeersTabViewModel`
- `SimulatorRelayTabViewModel`
- `SimulatorSessionsTabViewModel`

Also affected (non-VM but part of the simulator boundary):

- `SimulatorStateService` (primary refactor)
- `ISimulatorStateService` (ensure read-only exposure stays correct)
- Any simulator components that subscribe to state changes and assume UI-thread affinity:
  - `SimulatorOutboundInterceptor`
  - `SimulatorInitializer`
  - `SimulatorRelayDeliveryService`

Work (plan):

- Refactor `SimulatorStateService` into a pure service
  - Remove `IUiDispatcher` from the constructor and DI registrations.
  - Replace all `_ui.InvokeAsync(() => list.Add/remove/clear...)` with direct mutations under the correct gate.
  - Enforce the concurrency invariant:
    - Any iteration/snapshot of `_peers`, `_relays`, `_relationships`, dictionaries must occur under the same gate used for mutations.
  - Ensure the service exposes state as read-only:
    - `Peers`, `Relays`, `Relationships` stay `IReadOnlyObservableList<T>`.
  - Confirm persistence still uses Domain Snapshot Pattern:
    - Snapshot creation under gate.
    - Repository receives immutable records only.

- Update simulator ViewModels to own WPF thread bridging
  - Collections:
    - Any `CreateView(...).ToNotifyCollectionChanged()` must become:
      - `.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher)`.
  - Properties:
    - Any `.ToBindableReactiveProperty()` or `BindableReactiveProperty` projection must marshal first:
      - `.ObserveOnCurrentSynchronizationContext()`.
  - Filtering:
    - Keep filters purely VM-level (`AttachFilter`, `ResetFilter`, and targeted refresh).
  - Disposal:
    - Ensure every `CreateView` has `ObserveRemove().Subscribe(evt => evt.Value.View.Dispose())` when projecting child VMs.

- Update unit tests for the new threading boundaries
  - Simulator tests must stop assuming service mutates state on the UI thread.
  - Add/adjust test infrastructure to provide a `SynchronizationContext` when constructing VMs that call
    `_ui.CollectionEventDispatcher`.
  - Update affected test classes (expected to include, but not limited to):
    - `SimulatorStateServiceInitializationTests`
    - `SimulatorStateStoreTests`
    - `SimulatedPeerRuntime*Tests`
  - Add a small set of focused tests:
    - VM collection projections do not throw cross-thread when service mutates on background thread.
    - Service methods mutate domain lists without dispatcher dependency.

- Follow-up audit / regression checklist
  - Run simulator UI flows:
    - Add/remove peers
    - Toggle relay capable
    - Add/remove published-key relationships
    - Add/remove relay active sessions
    - Relay queue updates
  - Ensure no VM binds to a list via `BindableReactiveProperty<IReadOnlyList<T>>` (virtualization rule).
  - Ensure no sorting is introduced in VMs (XAML `CollectionViewSource` only).

Definition of done:

- `SimulatorStateService` has **zero** references to `IUiDispatcher` (constructor + body).
- All simulator Views/VMs remain stable (no WPF cross-thread exceptions) under background mutations.
- Simulator unit tests compile and pass.

Subchunks (implement one at a time; each is a large, coherent change-set):

## Chunk A.1 — Dispatcher boundary finalized (interface + WPF implementation + docs)

Outcome:

- `IUiDispatcher` exposes a **get-only** `CollectionEventDispatcher` (type: `ICollectionEventDispatcher`) so VMs can bridge collection change events without static globals.
- `WpfUiDispatcher` implements `CollectionEventDispatcher`.
- Docs reference `_ui.CollectionEventDispatcher` (not `SynchronizationContextCollectionEventDispatcher.Current`).

Concrete edits:

- `Desktop.Wpf/Shared/Mvvm/IUiDispatcher.cs`
  - Ensure the property exists:
    - `ICollectionEventDispatcher CollectionEventDispatcher { get; }`
- `Desktop.Wpf/Shared/Mvvm/WpfUiDispatcher.cs`
  - Implement:
    - `public ICollectionEventDispatcher CollectionEventDispatcher => SynchronizationContextCollectionEventDispatcher.Current;`
- `Desktop.Wpf/r3.readme.md`
- `Desktop.Wpf/main-window.plan.md` (this Chunk A)

Definition of done:

- All code compiles with the new interface property.
- No docs mention the static `SynchronizationContextCollectionEventDispatcher.Current`.

## Chunk A.2 — Make `SimulatorStateService` a pure service (remove `IUiDispatcher`)

Outcome:

- `SimulatorStateService` has **no** `IUiDispatcher` dependency.
- All domain collection mutations happen under gates on the calling thread.

Concrete edits (file: `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`):

- Remove field + ctor parameter:
  - `private readonly IUiDispatcher _ui;`
  - `SimulatorStateService(IUiDispatcher ui, ...)`
- Replace every `_ui.InvokeAsync(() => ...)` that mutates domain lists with direct mutations under the correct gate.
  - Known call sites to remove (from grep):
    - `_relays.Clear()`
    - `_relays.Add(relay)`
    - `_relationships.Clear()` + `_relationships.Add(rel)` loop
    - `_peers.Clear()` + `_peers.Add(model)` loop
    - `_peers.Add(model)`
    - `_peers.Remove(removedModel)`
    - `_relationships.Add(rel)` / `_relationships.Remove(rel)`
    - `_relays.Add(loaded)`
    - `_relays.Remove(relay)`

Concurrency invariants (must be enforced while doing the above):

- Any iteration/snapshot of `_peers`, `_relays`, `_relationships` must be done under the same gate as mutation.
- Do not introduce any UI-thread marshalling in the service.

Definition of done:

- `SimulatorStateService.cs` contains **zero** references to `IUiDispatcher` or `_ui.`.
- Build succeeds (even if some VMs still throw at runtime until A.3 is applied).

## Chunk A.3 — Fix simulator VM collection bridging (no cross-thread WPF exceptions)

Outcome:

- Any simulator VM binding to `NotifyCollectionChangedSynchronizedViewList<T>` uses the dispatcher-aware overload:
  - `.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher)`

Concrete edits (known files + call sites from grep):

- `Desktop.Wpf/Features/Simulator/SimulatedPeerCardViewModel.cs`
  - Update:
    - `AvailablePublishTargets = _availableTargetsView.ToNotifyCollectionChanged(...)`
    - `PublishedToTags = _publishedToView.ToNotifyCollectionChanged(...)`
    - `HostingForTags = _hostingForView.ToNotifyCollectionChanged(...)`
  - Requires `IUiDispatcher` injection if not already present.

- `Desktop.Wpf/Features/Simulator/SimulatedRelayQueuePanelViewModel.cs`
  - Update:
    - `_availableTargetsNotify = _availableTargets.ToNotifyCollectionChanged(...)`
    - `_activeSessionTagsNotify = _activeSessionTags.ToNotifyCollectionChanged(...)`
    - `QueueItems = synchronizedQueueView.ToNotifyCollectionChanged(...)`
  - Requires `IUiDispatcher` injection if not already present.

- `Desktop.Wpf/Features/Simulator/SimulatorDiagnosticsTabViewModel.cs`
  - Update:
    - `_filteredNotify = _filteredView.ToNotifyCollectionChanged(...)`

- `Desktop.Wpf/Features/Simulator/SimulatorHandshakesTabViewModel.cs`
  - Update:
    - `_relayHostsNotify = _relayHosts.ToNotifyCollectionChanged(...)`
    - `_cardsNotify = _cards.ToNotifyCollectionChanged(...)`

- `Desktop.Wpf/Features/Simulator/SimulatorPeersTabViewModel.cs`
  - Update:
    - `_peerCardsNotify = _peerCards.ToNotifyCollectionChanged(...)`

- `Desktop.Wpf/Features/Simulator/SimulatorRelayTabViewModel.cs`
  - Update:
    - `_relayPanelsNotify = _relayPanels.ToNotifyCollectionChanged(...)`

Definition of done:

- All simulator VMs compile.
- The simulator UI no longer throws WPF cross-thread exceptions when the service mutates domain collections off-thread.

## Chunk A.4 — VM UI-thread usage audit (keep `_ui.InvokeAsync` only for true UI operations)

Outcome:

- VMs may still use `_ui.InvokeAsync(...)`, but only for:
  - clipboard access
  - updating UI-only `BindableReactiveProperty` values from background flows
  - safely enumerating UI-bound adapters (rare; prefer querying domain instead)

Concrete edits (known VM `_ui.InvokeAsync` call sites from grep):

- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
  - Clipboard write stays on UI thread.
  - Status updates remain UI-thread safe.

- `Desktop.Wpf/Features/Simulator/SimulatorDiagnosticsTabViewModel.cs`
  - Keep UI-thread filter application.
  - Ensure any domain enumeration is not performed against UI-bound adapters off-thread.

- `Desktop.Wpf/Features/Simulator/SimulatorHandshakesTabViewModel.cs`
  - Keep UI-thread rebuild hooks, but ensure they do not assume service thread affinity.

- `Desktop.Wpf/Features/Simulator/SimulatorPeersTabViewModel.cs`
  - `InitializePeerCardsView` can remain invoked on UI thread (VM concern).

- `Desktop.Wpf/Features/Simulator/SimulatorRelayTabViewModel.cs`
  - Confirm auto-deliver loop does not enumerate UI-bound lists off-thread without `_ui.InvokeAsync`.

Definition of done:

- No VM enumerates `NotifyCollectionChangedSynchronizedViewList<T>` off-thread.
- Remaining `_ui.InvokeAsync` usage in VMs is strictly UI-only.

## Chunk A.5 — Update unit tests for new dispatcher boundary

Outcome:

- Simulator tests compile and pass under the new `IUiDispatcher.CollectionEventDispatcher` requirement.

Concrete edits:

- Provide a test `IUiDispatcher` implementation that:
  - Returns a deterministic `ICollectionEventDispatcher`.
  - Uses a test `SynchronizationContext` for `InvokeAsync` execution.

- Update test classes that construct simulator VMs to pass the test dispatcher.

Definition of done:

- `dotnet test` passes.

---

## Chunk B — Simulator shell + loading state (remove redundant InitializeAsync waits)

Outcome:

- The simulator opens a window immediately and shows a simple **Loading…** state while the simulator state is initialized.
- `HandshakeSimulatorViewModel` no longer needs to call `ISimulatorStateService.InitializeAsync` (or otherwise "wait for state") in three separate places.
- Initialization responsibility is moved behind a dedicated initializer interface:
  - `ISimulatorStateInitializer.InitializeAsync(...)`.
  - `SimulatorStateService` may implement this interface.
  - `ISimulatorStateService` becomes strictly a **runtime state + actions** contract.

Motivation:

- WPF will bind to ViewModel properties as soon as `DataContext` is set.
- Async initialization that occurs after `DataContext` assignment can cause:
  - fragile “ViewModel not initialized” patterns,
  - redundant initialization calls,
  - and UI that stays closed while work happens.

Work (plan):

### Chunk B.1 — Split initialization into `ISimulatorStateInitializer`

- Add interface:
  - File: `Desktop.Wpf/Features/Simulator/ISimulatorStateInitializer.cs`
  - Shape:
    - `Task InitializeAsync(CancellationToken cancellationToken = default);`
- Update `ISimulatorStateService`:
  - Remove `InitializeAsync(...)` from the interface.
- Update `SimulatorStateService`:
  - Implement `ISimulatorStateInitializer`.
  - Keep the existing initialization logic, but expose it only via the initializer interface.
- Update DI registrations (App startup):
  - Ensure `SimulatorStateService` is registered once, and is resolved as:
    - `ISimulatorStateService`
    - `ISimulatorStateInitializer`
  - Avoid double-singleton instances.

Implementation detail (must be followed):

- Register the concrete singleton once, then map interfaces to the same instance.
  - Example pattern:
    - `services.AddSingleton<SimulatorStateService>();`
    - `services.AddSingleton<ISimulatorStateService>(sp => sp.GetRequiredService<SimulatorStateService>());`
    - `services.AddSingleton<ISimulatorStateInitializer>(sp => sp.GetRequiredService<SimulatorStateService>());`

### Chunk B.2 — Single simulator window with an in-window loading view

Goal:

- The simulator uses **one window**.
- The window opens immediately and initially shows a simple **Loading…** view.
- Only after `ISimulatorStateInitializer.InitializeAsync` completes does the window show the existing simulator UI (what it shows today).

Concrete edits:

- Introduce a lightweight host ViewModel that owns loading state + the real content VM:
  - `Desktop.Wpf/Features/Simulator/HandshakeSimulatorHostViewModel.cs`
  - Responsibilities:
    - `BindableReactiveProperty<bool> IsLoading` (default true)
    - `BindableReactiveProperty<string?> Status` (default "Loading…")
    - `object? ContentViewModel` (null until ready; or a typed property)
    - On startup, call and await `ISimulatorStateInitializer.InitializeAsync`.
    - When initialization succeeds:
      - create/resolve the existing simulator VM (current `HandshakeSimulatorViewModel`),
      - set `ContentViewModel`,
      - set `IsLoading = false`.
    - When initialization fails:
      - keep `IsLoading = true` and set `Status` to the error.

Lifetime ownership (must be explicit):

- `HandshakeSimulatorHostViewModel` owns the inner simulator VM lifetime.
  - If the host VM is disposed, it must dispose the inner simulator VM if it was created.
  - If initialization fails after the inner VM has been created, the host must dispose it.
  - The window should dispose the host VM on `Closed` (existing pattern).

- Update the simulator window to bind to the host VM and switch content:
  - `Desktop.Wpf/Features/Simulator/HandshakeSimulatorWindow.xaml`
    - Display a simple "Loading…" visual when `IsLoading` is true.
    - Display the existing simulator content (the view bound to `HandshakeSimulatorViewModel`) when `IsLoading` is false.
    - Implementation can use:
      - a `ContentControl` bound to `ContentViewModel`, with a `DataTemplate` for `HandshakeSimulatorViewModel`,
      - and a separate loading overlay/placeholder driven by `IsLoading`.
  - `Desktop.Wpf/Features/Simulator/HandshakeSimulatorWindow.xaml.cs`
    - Inject `HandshakeSimulatorHostViewModel` (not the inner VM).
    - Set `DataContext = hostVm`.
    - Kick off host initialization without blocking window creation.

Idiomatic pattern notes (must be followed):

- The host VM should not rely on fragile timing (e.g., hoping the service is initialized before bindings evaluate).
  - The host VM must be safe to bind immediately.
- Avoid "fire-and-forget" tasks that can crash the process:
  - Store the initialization `Task` (or use `SubscribeAwait` with error handling) so exceptions are observed.
  - Surface failures via `Status`.
- Support cancellation:
  - The host VM should accept a `CancellationToken` and cancel initialization when the window closes.
  - Ensure the host VM disposes the inner simulator VM if initialization fails after creating it.

Transition orchestration (must be MVVM-friendly):

- The host VM may obtain the inner VM via DI (constructor injection of a factory or `Func<HandshakeSimulatorViewModel>`).
- Avoid `new HandshakeSimulatorViewModel(...)` in code-behind.

Definition of done:

- Opening the simulator shows a window immediately with "Loading…".
- The existing simulator UI is not displayed until the simulator state has been initialized.

### Chunk B.3 — Constructor-time projections in simulator tab VMs (no-throw bindable getters)

Goal:

- WPF bindings must be able to evaluate immediately after `DataContext` is set.
- Therefore, any bindable property getter used by XAML must be safe:
  - Do **not** throw `InvalidOperationException("ViewModel not initialized")`.
  - Provide an empty-but-valid collection instance from the constructor.

Concrete edits:

- `Desktop.Wpf/Features/Simulator/SimulatorPeersTabViewModel.cs`
  - Remove the `PeerCards` getter throw pattern.
  - Construct the `CreateView(...)` and `ToNotifyCollectionChanged(_ui.CollectionEventDispatcher)` adapter in the constructor.
  - Remove `InitializeAsync` (or reduce it to no-op / remove state waits entirely).

- `Desktop.Wpf/Features/Simulator/SimulatorHandshakesTabViewModel.cs`
  - Remove `Cards` getter throw pattern.
  - Create `_cards` projection + `_cardsNotify` in the constructor.
  - Keep relay-host list (`_relayHostsNotify`) created in constructor (already done).
  - Wire `HookRelayHosts()` from constructor (it is UI-thread oriented and should not depend on service initialization barriers).
  - Remove `InitializeAsync` (or reduce it to directory-only concerns if any remain after shell work).

- `Desktop.Wpf/Features/Simulator/SimulatorRelayTabViewModel.cs`
  - Remove `RelayPanels` getter throw pattern.
  - Create `_relayPanels` projection + `_relayPanelsNotify` in the constructor.
  - Remove `InitializeAsync` state waits.

Definition of done:

- These three tab VMs can be constructed and bound without calling `InitializeAsync`.
- No bindable getter throws due to "not initialized".

### Chunk B.4 — Relay auto-deliver lifecycle moved into a dedicated service

Goal:

- The current relay auto-deliver loop is long-running background work and is not VM initialization.
- Replace it with a dedicated service that owns start/stop lifecycle.

Concrete edits:

- Add a new service interface + implementation:
  - `Desktop.Wpf/Features/Simulator/ISimulatorRelayAutoDeliverService.cs`
  - `Desktop.Wpf/Features/Simulator/SimulatorRelayAutoDeliverService.cs`
  - Responsibilities:
    - Start/stop a background loop.
    - Periodically query domain state (`_state.Relays`, `_state.Relationships`) and deliver items.
    - Be cancellation-safe and disposable.
  - The service should depend on:
    - `ISimulatorStateService`
    - `ISimulatorRelayDeliveryService`

Non-goals / constraints (must be followed):

- The relay auto-deliver service must NOT enumerate UI-bound adapter collections (e.g., `NotifyCollectionChangedSynchronizedViewList<T>`).
- The relay auto-deliver service must NOT depend on `IUiDispatcher`.
  - It must be purely domain-driven and safe to run in tests.

- Update `SimulatorRelayTabViewModel`:
  - Remove `_autoDeliverCts`, `_autoDeliverLoop`, and `AutoDeliverLoopAsync` from the VM.
  - Keep only UI state:
    - `GlobalAutoRelayAll`
    - `RelayPanels` projection

- Decide where start/stop is called:
  - Preferred: shell window (or the real simulator window) starts the service on `Loaded` and stops it on `Closed`.

Definition of done:

- Relay auto-deliver continues to function.
- No background loop is owned by the tab VM.

### Chunk B.5 — Refactor handshake simulator startup to use the shell

- Remove simulator-state initialization waits from:
  - `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
- The handshake simulator window/viewmodel assumes the state is already initialized.
- Ensure `HandshakeSimulatorWindow.xaml.cs` does not need to await initialization before setting `DataContext`.

### Chunk B.6 — Update tests and simulator entry points

- Update any tests that were calling `ISimulatorStateService.InitializeAsync`:
  - Call `ISimulatorStateInitializer.InitializeAsync` instead.
- Ensure `dotnet test` remains green.

### Chunk B.7 — Simulator unit test fallout (initializer split + constructor projections + relay loop service)

Goal:

- Make test fallout explicit and non-surprising.
- Ensure tests remain focused on observable behavior and compile against the new contracts.

Concrete affected test files (from grep; update these explicitly):

- `Desktop.Wpf.Tests/SimulatorStateServiceInitializationTests.cs`
  - If `InitializeAsync` is removed from `ISimulatorStateService`, update tests to call:
    - `ISimulatorStateInitializer.InitializeAsync` (or cast `sut` to initializer if using concrete type).

- `Desktop.Wpf.Tests/SimulatedPeerRuntimeFinalizeTests.cs`
- `Desktop.Wpf.Tests/SimulatedPeerRuntimeFinalizeRelayedTests.cs`
- `Desktop.Wpf.Tests/SimulatedPeerRuntimeStandardHandshakeRelayedTests.cs`
- `Desktop.Wpf.Tests/SimulatedPeerRuntimeServiceDecryptFailureDiagnosticsTests.cs`
  - These tests currently call `sut.InitializeAsync(...)` on the service.
  - Update to call `initializer.InitializeAsync(...)` instead.

- `Desktop.Wpf.Tests/SimulatedPeerDirectoryInitializationTests.cs`
  - This test currently mocks `ISimulatorStateService.InitializeAsync`.
  - Update it to mock `ISimulatorStateInitializer.InitializeAsync`.
  - `SimulatorInitializer` should depend on the initializer interface (or otherwise be refactored so the test can control init gating deterministically).

Additional expected fallout (verify during implementation):

- Any tests that construct simulator tab VMs and previously required calling `InitializeAsync` before reading properties.
  - After constructor-time projections (B.3), tests should not need an init step to access bindable collections.

- Relay loop service (B.4):
  - If any tests assumed `SimulatorRelayTabViewModel.InitializeAsync` starts auto-deliver, update those tests to explicitly start the relay auto-deliver service (or to assert delivery by directly invoking relay-delivery APIs).

Definition of done:

- All simulator unit tests compile and pass.
- No tests reference the removed `ISimulatorStateService.InitializeAsync`.

Chunk B definition of done (overall):

- `HandshakeSimulatorWindow` opens immediately and shows "Loading…".
- The existing simulator UI is not displayed until `ISimulatorStateInitializer.InitializeAsync` completes.
- `HandshakeSimulatorViewModel` no longer contains redundant “wait for simulator state to load” calls.
- `ISimulatorStateService` no longer exposes `InitializeAsync`.
- Simulator tabs can be constructed/bound without calling `InitializeAsync`.
- Relay auto-deliver loop is owned by the dedicated service (not a tab VM).
- Build + tests are green.

---

## Chunk I — Notification badge + default focus behavior for Connection Management button

Outcome:

- Header button shows count badge and pulses when inbound requests exist.
- Opening dialog defaults to Tab 1 if inbound exists else Tab 2.

Work (recipe):

- Badge count
  - Ensure the header “Connection Management / Add peer” button displays the inbound pending count.
  - The count must be sourced from shared state (store-level), not recomputed ad-hoc in the view.
  - It’s acceptable to *project* the store count into a ViewModel property for binding.

- Pulse / attention behavior
  - When the inbound pending count transitions:
    - `0 -> 1` (first pending arrives)
    - or `n -> n+1` (more pendings arrive)
  - the header badge should pulse to draw attention.
  - Keep this purely UI behavior (no domain logic).

- Default tab selection when opening Connection Management
  - At dialog open time, choose the initial tab based on whether inbound pending exists:
    - inbound pending exists: default to the Incoming Signals tab
    - otherwise: default to the Network Search tab
  - This decision should be made once when the dialog is created/opened (not continuously).

Definition of done:

- Badge count matches `ISecureChannelsStore.PendingInboundCount`.
- Badge is visible and pulses whenever inbound pending exists.
- Opening Connection Management defaults to Incoming Signals tab when pending exists, otherwise defaults to Network Search.

Minimal tests:

- Unit test that a VM projection of `PendingInboundCount` updates when store pending inbound changes.

Implementation note:

- This chunk may already be complete:
  - `MatButton.NotificationCount` has built-in pulsing behavior when the count increases.
  - The sidebar button already binds its `NotificationCount` to a pending-handshake count.
  - `ConnectionManagementDialogViewModel.InitializeAsync` already selects the Incoming tab when pending invitations exist.
- If you want stricter alignment to the definition of done, the remaining delta would be to bind the badge count *directly* (or via a projection) to `ISecureChannelsStore.PendingInboundCount` and to base the default tab decision on the same store count rather than any dialog-local enumeration.

---

## Chunk J — Uplink Inspector side panel (Direct vs Relay topology)

Outcome:

- “UPLINK” button toggles a side panel showing connection topology:
  - Direct: Main <-> Peer
  - Relayed: Main -> Relay -> Peer

Work (recipe):

- Authoritative requirements
  - Follow `design/main-window.md` “Uplink Inspector UI” section.
  - Uplink is only meaningful for an active channel header; it is disabled for Pending.

- State ownership
  - Topology/route provenance is shared, notification-driven state:
    - Source: fields on `SecureChannelModel` populated by projection (Chunk H).
    - Do not store route provenance inside a scoped VM.
  - Inspector visibility/toggle is ephemeral UI state:
    - OK to keep in the right pane VM as `BindableReactiveProperty<bool> IsUplinkOpen`.
    - Alternatively keep in `SelectedSecureChannelStateModel` if you want it preserved per-channel.

- Model shape needed for UI
  - Ensure `SecureChannelModel` exposes enough to render topology:
    - Minimal for Chunk J:
      - Route kind: Direct vs Relay (and optionally Group)
      - Relay identifier/name when relayed
    - If `SecureChannelKind` already distinguishes `Relay`, use it.
    - Otherwise add explicit route fields as described in Chunk H (e.g., `RouteText`, `RelayPeerId`).

- ViewModel projection
  - Implement an inspector VM that projects from the selected channel (store) + the per-channel ephemeral state:
    - File: `Desktop.Wpf/Features/Sessions/UplinkInspectorViewModel.cs`
    - Inputs:
      - `SelectedChannelModel`
      - `ISecureChannelsStore`
      - Optional: `SelectedSecureChannelStateCache` if preserving open/closed per channel.
    - Outputs:
      - `BindableReactiveProperty<bool> IsOpen`
      - `BindableReactiveProperty<string> TopologyText` (or structured nodes)
      - `BindableReactiveProperty<bool> IsEnabled` (false when Pending/Failed)

- UI implementation
  - Add a header “UPLINK” button to the active channel header in the right pane.
    - If the right pane is a `ContentControl` with templates, add button within the Active template.
  - Add the sliding side panel:
    - Use a `Grid` column or overlay `Border` with animation when `IsOpen` changes.
    - Keep visuals simple for the first pass:
      - Direct: `[Operator Node] <====> [Peer Name]`
      - Relayed: `[Operator Node] ----> [Relay] ----> [Peer Name]`

Definition of done:

- Active direct channel shows direct topology.
- Active relayed channel shows relay topology with relay identifier.
- Pending channel disables uplink.

Minimal tests:

- Unit test for inspector VM mapping from shared model route fields to output text/state.

---

## Chunk K — Session reset / recovery action

Outcome:

- Kebab menu includes “Reset Secure Session”.
- Triggers a new outbound **Reverse-Signal invitation** behind the scenes while keeping channel history.

Work (recipe):

- Authoritative requirements
  - Follow `design/main-window.md` “Session Reset / Recovery” section.
  - Goal is to heal a desynchronized ratchet without losing the channel row/history.

- Identify existing reverse-signal primitives to reuse
  - Invite construction:
    - File: `Percolator.Application/Network/MainReverseSignalInviteFactory.cs`
    - API: `IMainReverseSignalInviteFactory.CreateInvite()`
    - Side effects:
      - Persists a `SentInvitation` keyed by `request_correlation_id`.
      - Persists a per-invite signed pre-key (needed later for inviter finalization).
  - Inbound invite queueing:
    - File: `Percolator.Application/Network/EstablishDirectSessionService.cs`
    - Publishes `PendingSessionCreatedNotification`.
  - Accepting invite:
    - File: `Percolator.Application/Network/ApprovePendingSessionCommand.cs`
    - Publishes `SecureSessionCreatedNotification` when acceptor establishes.
    - Delivers `InviteHandshakeResponse` via `IInviteHandshakeResponseDeliveryService`.
  - Inviter finalization:
    - File: `Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`
    - Correlates response by `request_correlation_id` to `SentInvitation` and publishes `SecureSessionCreatedNotification`.

- State ownership
  - “Reset in progress” is primarily an ephemeral UI concern scoped to a channel:
    - Store it in `SelectedSecureChannelStateModel` as a reactive field:
      - Example: `ReactiveProperty<bool> IsReestablishing`
      - Example: `ReactiveProperty<string?> ReestablishingText`
    - This keeps the main shared store focused on notification-driven channel facts.
  - The channel row itself MUST remain the authoritative `SecureChannelModel` from the store.
    - Do not delete and recreate the channel just to reset.

- Command surface (ViewModel)
  - Add a kebab menu affordance in the Active channel header UI:
    - “Reset Secure Session” item.
  - Implement a VM command that triggers reset:
    - File: right-pane/header VM (from Chunk G)
    - Behavior:
      - Sets `SelectedSecureChannelStateModel.IsReestablishing = true`.
      - Sends an application command via `IMediator.Send(...)` (or invokes an existing service) to initiate a new reverse-signal invite.
      - Does not directly mutate `SecureChannelsStore`.

- Application-layer behavior to trigger new invite
  - Prefer an explicit application command (so it is testable and does not couple UI to services):
    - Example: `ResetSecureSessionCommand(SecureChannelKey channelKey | PeerId remotePeerId, RouteChoice route)`.
  - Implementation must:
    - Build a fresh reverse-signal invite via `IMainReverseSignalInviteFactory.CreateInvite()`.
    - Deliver it to the remote peer using the same routing primitives as outbound initiation (Chunk H).
    - Ensure an outbound pending row exists and will migrate to active via Chunk F.6 correlation.

- UI behavior
  - While reset is pending:
    - Show a temporary banner/system message in the right pane: “Re-establishing secure connection…”.
    - Disable sending or mark messages as queued (placeholder acceptable).
  - When the new session is established:
    - Clear `IsReestablishing`.
    - Channel remains selected; UI returns to Active.

Definition of done:

- “Reset Secure Session” is available for an active 1:1 channel.
- Triggering reset creates a new outbound pending action and shows “Re-establishing…” immediately.
- When the new session completes, the channel returns to Active without losing the channel row.

Minimal tests:

- Unit test: triggering reset sets `IsReestablishing` and invokes the application command.
- Integration smoke: completing the reverse-signal flow clears `IsReestablishing` and results in an active session.

---

## Chunk L — Visual fidelity pass (match screenshots)

Outcome:

- Style and layout improvements to match `design/*.png`:
  - tab strip styling
  - button states
  - badge colors
  - list item layout

Work (recipe):

- Scope/constraints
  - This chunk is UI-only; do not change application logic or store/projection semantics.
  - Prefer consolidating WPF styles/templates over adding per-view ad-hoc styling.

- Authoritative references
  - Visual targets: `Desktop.Wpf/design/*.png`
  - Behavior targets (do not regress): `Desktop.Wpf/design/main-window.md`

- Primary files to touch
  - List visuals:
    - `Desktop.Wpf/Features/Sessions/SessionsSidebarView.xaml`
    - `Desktop.Wpf/Features/Sessions/SessionsSidebarViewModel.cs` (only if binding surface needs minor extensions)
    - `Desktop.Wpf/Features/Sessions/SecureChannelListItemViewModel.cs` (only if additional bindable display props are needed)
  - Shared styles/resources:
    - `Desktop.Wpf/Shared/Theme/Styles.xaml`
    - `Desktop.Wpf/Shared/Theme/Typography.xaml`
    - `Desktop.Wpf/Shared/Theme/Colors.xaml` (if present)
    - `Desktop.Wpf/Shared/Theme/Icons.xaml`
  - Shared controls:
    - `Desktop.Wpf/Shared/Controls/MatButton.xaml`
    - `Desktop.Wpf/Shared/Controls/MatChip.xaml`
    - `Desktop.Wpf/Shared/Controls/InitialsAvatar.xaml`

- Concrete UI checklist
  - Tab strip styling (Connection Management and any right-pane tab usage)
  - Button states:
    - hover/pressed/disabled visuals match screenshots
  - Badge colors and shapes:
    - unread badge
    - channel tech badge (DIRECT/RELAY/GROUP/PENDING/FAILED)
    - add-peer notification badge
  - List item layout:
    - avatar alignment + online dot
    - name/snippet typography
    - timestamp alignment

Definition of done:

- Screens match `design/*.png` within reasonable tolerance.
- No behavioral regressions in filtering, selection, pending badge, and store-driven updates.

---

## Chunk M — Cleanup / remove legacy handshake UI and orphaned code

Outcome:

- The codebase has a single, clear handshake entry point: **Connection Management**.
- Legacy UI and mappings that are no longer used are removed to avoid confusion and bit-rot.

Work (recipe):

- Inventory and delete legacy handshake UI
  - Remove legacy NewHandshake dialog artifacts if no longer used:
    - `Desktop.Wpf/Features/Sessions/NewHandshakeDialogWindow.xaml`
    - `Desktop.Wpf/Features/Sessions/NewHandshakeDialogWindow.xaml.cs`
    - `Desktop.Wpf/Features/Sessions/NewHandshakeDialogViewModel.cs`
  - Remove any related tests that only exist for the deleted UI.

- Remove DI registrations and view mappings
  - DI:
    - File: `Desktop.Wpf/App.xaml.cs`
    - Remove any `services.Add...<NewHandshakeDialogViewModel>()` style registrations.
  - Window/view mappings:
    - File: `Desktop.Wpf/Shared/Theme/ViewMappings.xaml` and/or `Desktop.Wpf/Shared/Windowing/ViewMappings.xaml` (remove from the one actually used)
    - Remove mappings for legacy dialog.

- Remove call sites
  - Search for and remove remaining invocations:
    - `ShowFor<NewHandshakeDialogViewModel>`
    - `ShowFor<NewHandshakeDialogWindow>`
  - Ensure they route to:
    - `ConnectionManagementDialogViewModel`

- Remove legacy event listeners/invalidation shims
  - Search for MediatR listeners or event buses that exist only to refresh the legacy handshake UI.
  - Candidate based on current code:
    - `Desktop.Wpf/Features/Sessions/ConnectionManagementInboxEventListener.cs` (calls `IMainInvitationInboxEvents.NotifyChanged()` on `PendingSessionCreatedNotification`)
  - Replace/retire them in favor of store-driven state (Chunk F) where applicable.

Definition of done:

- Only one handshake entry point remains in the UI: Connection Management.
- No references remain to `NewHandshakeDialog*` types.
- Build passes.

---

## Risks / tricky areas

- Debounced filtering and dispatcher scheduling: avoid hard UI-thread dependencies in core services.
- Preserving route provenance across handshake lifecycle (so UI can display “Arriving via …” accurately).
- Avoid mixing “pending inbound” with the Secure Channels list (anti-spam requirement).

---

## Relay Bug 1

### relay-setup-1 (repro)

- Start from a brand new DB file (no peers).
- In the Simulator window:
  - Create 2 simulated peers.
  - Mark exactly 1 of them as relay-capable (call it **Relay**).
  - Add the non-relay peer (call it **Target**) as an active session on the Relay.
  - From Target, publish a standard pre-key bundle to Relay.
- In the Main window:
  - Establish a direct connection/session to Relay.
  - Open Connection Management -> Network Search.
  - Choose route mode: **relay**.
  - Select Relay as relay host.
  - Enter Target PKH and click **Fetch Pre-Key Bundle & Initiate**.

#### Expected behavior

- Main requests Target pre-key bundle from Relay (GetPreKeyBundleRequest).
- Relay responds with GetPreKeyBundleResponse (bundle present).
- Main then creates a HandshakeInitiatorHello and sends an EnqueueOpaqueMessageRequest to Relay.
- Relay enqueues the hello in its relay queue under routing key == Target PKH.
- Simulator UI (Relay tab) shows a new queued relay message on the Relay peer.

#### What we have checked / verified

- Publish to relay host works; the pre-key bundle is stored in the relay host’s PublishedBundles collection.
- Fetch-by-PKH from relay host works end-to-end:
  - The request reaches the correct simulated peer runtime (simulatedPeerId matches the relay host peer id).
  - The request PKH matches the stored bundle key (byte-for-byte).
  - The relay host responds with a response payload containing the pre-key bundle.

Concrete debug values (from a known-good run):

- relayHostPeerId:
  - `a8d433c8-7741-472b-ab52-c7ebebd920f4`
- logicalOwnerPeerId (Target simulated peer id that published bundle):
  - `64d5433b-7a86-4195-b106-ec98f1d2c1ad`
- recipientPublicKeyHash / requested PKH (hex):
  - `C147ED93237B62FFADAEF2480C47B1D9CFEAD154556F3F8BAA851EFCE098DEA5`
- Relay host runtime ingress confirmation:
  - `simulatedPeerId` for GetPreKeyBundleRequest == `a8d433c8-7741-472b-ab52-c7ebebd920f4`
  - `getReq.PublicKeyHash` hex == `C147ED...DEA5`

### Next verification steps

The remaining suspected failure is in the **post-bundle enqueue** path (Main -> Relay EnqueueOpaqueMessageRequest -> Relay queue persistence/UI).

- Verify Main actually sends the enqueue message:
  - Breakpoint: `ConnectionManagementDialogViewModel.ExecuteNetworkSearchAsync` at the second `_transport.SendMessageAsync(...)` that sends `EnqueueOpaqueMessageRequest`.
  - Capture: relayHostPeerId, direct session id, targetPkh hex, and size of `cipherMq`.

- Verify transport routing hits the simulator interceptor for the enqueue send:
  - Breakpoint: `SimulatorOutboundInterceptor.TryDeliverOpaqueMessage(...)`.
  - Confirm: `TryResolveSimulatedPeerId(...) == true` and resolved peer id == relayHostPeerId.

- Verify relay peer runtime parses the enqueue envelope and calls state enqueue:
  - Breakpoint: `SimulatedPeerRuntime.ReceiveOpaqueMessageFromMainAsync` inside the `EnqueueOpaqueMessageRequest` branch.
  - Confirm: `RecipientPublicKeyHash` == targetPkh and `MessageBlob` length > 0.
  - Breakpoint: `SimulatorStateService.EnqueueRelayOpaqueAsync(...)` and confirm queue count increments.

- If queue count increments but UI remains unchanged:
  - Investigate `SimulatorRelayTabViewModel` / relay panel refresh behavior and dispatcher affinity.