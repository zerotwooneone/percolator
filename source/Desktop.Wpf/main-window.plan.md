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

## Chunk A - Architectural Fix: Introduce Application-Level Query Service and Peer Connection State Service

### The Problem (Concurrency Exception & Guideline Violations)
The `SecureChannelsProjection` was built as a `Scoped` MediatR event handler. This means it shares the exact same `PercolatorDbContext` as the domain command that triggers it (e.g., `HandleHandshakeResponderHelloCommand`). When the projection drops an event onto a background Rx `Subject` and returns, the domain command continues executing while the background Rx pipeline concurrently queries the DB, causing EF Core to crash with a concurrency exception.

Beyond the bug, `SecureChannelsProjection` violates the application's Domain-Driven Design and R3 guidelines. It directly injects **six** disparate domain-level repositories (`ISessionRepository`, `IPeerIdentityRepository`, `IDirectSessionRepository`, `IPendingHandshakeQueries`, `IPreHandshakeSessionStore`, `ISentInvitationRepository`) to manually stitch together UI models. This leaks heavy domain complexity into the UI layer and breaks the "Pure Service / Stateless Repository" boundary. Furthermore, the concept of a "Secure Channel Repository" violates DDD, because "Secure Channel" is not an aggregate root in the system—it's a synthetic UI construct.

### The Architectural Shift: "Peer Connection State"
The concept we are modeling for the UI is the **state of a connection/handshake with a peer**. The primary purpose of an X3DH session is establishing a secure pipeline for chat, peer discovery, or file transfer. 

We will shift terminology from "Secure Channel" to **Peer Connection**. 

### Important AI Implementation Rules for this Chunk:
1. **Documentation Context:** When implementing these steps, you MUST adhere to the patterns and constraints defined in:
   - `@source/Desktop.Wpf/r3.readme.md` (Domain services, TimeProvider, ObservableList, thread safety)
   - `@source/Desktop.Wpf/wpf.readme.md` (WPF thread bridging, CreateView, ViewModel disposal)
   - `@source/unit-testing.md` (NUnit, Moq, FluentAssertions, FakeTimeProvider)
2. **File Deletion/Renaming Constraint:** If a step requires renaming or deleting an existing class, file, or interface, **the AI MUST NOT perform the deletion/renaming itself**. Instead, the AI must explicitly **ASK THE USER** to perform the rename/delete action, providing the exact file paths and names. You may create *new* files alongside the old ones to transition bindings, but the user must delete the old ones.
3. **Unit Testing:** Every new class containing logic (Queries, Services, ViewModels) MUST have an accompanying minimal unit test file as outlined in `@source/unit-testing.md`.

---

### Sub-Chunk A.1: Revert `RatchetKeyIndexAdapter` DbContext Hack
**Goal:** Restore the domain repository to its proper scoped state.
- **Task:** Edit `RatchetKeyIndexAdapter.cs`. Remove `IServiceScopeFactory`. Inject `PercolatorDbContext` directly via the constructor and use it directly for `TryResolveAsync` and `UpsertAsync`.
- **Validation:** Ensure existing unit tests for session indexing still compile and pass.

### Sub-Chunk A.2: Define Query Models & `IPeerConnectionQueries` Interface
**Goal:** Create the read-only CQRS boundary for the UI.
- **Task:** Create a new file `PeerConnectionStateSnapshot.cs` in `Desktop.Wpf`. It should be an immutable `record` representing the connection state (ConnectionId, PeerId, DisplayName, Status, RelayHostPeerId, LastActivityUtc).
- **Task:** Create an immutable `PendingInboundSnapshot.cs` record for incoming unaccepted handshakes.
- **Task:** Create `IPeerConnectionQueries.cs` defining:
  - `Task<IReadOnlyList<PeerConnectionStateSnapshot>> LoadAllConnectionsAsync(int selfIdentityId, CancellationToken ct)`
  - `Task<IReadOnlyList<PendingInboundSnapshot>> LoadPendingInboundAsync(CancellationToken ct)`
- **Docs Ref:** Aligns with the "Domain Snapshot Pattern" in `@source/Desktop.Wpf/r3.readme.md`.

### Sub-Chunk A.3: Implement `PeerConnectionQueries` (CQRS)
**Goal:** Move complex DB aggregation out of the background projection.
- **Task:** Create `PeerConnectionQueries.cs` implementing `IPeerConnectionQueries`. 
- **Task:** Migrate the complex data-stitching logic currently found inside `SecureChannelsProjection.ReloadAsync` into these new query methods. You may inject the 6 domain repositories (`ISessionRepository`, `IPeerIdentityRepository`, etc.) into this class, as it will be safely resolved within its own short-lived scope.
- **Testing:** Create `PeerConnectionQueriesTests.cs` using Moq to mock the underlying repositories and verify that the correct snapshots are returned (see `@source/unit-testing.md`).

### Sub-Chunk A.4: Create `PeerConnectionStateService` (R3 Orchestrator)
**Goal:** Replace the faulty projection with a thread-safe R3 Singleton Service.
- **Task:** Create `PeerConnectionStateService.cs` as a Singleton.
- **Task:** It must own `ObservableList<PeerConnectionModel>` and `ObservableList<PendingInvitationModel>`. Expose them as `IReadOnlyObservableList` (Reference: `@source/Desktop.Wpf/r3.readme.md`).
- **Task:** It must implement MediatR `INotificationHandler` for `SecureSessionCreatedNotification`, `PendingSessionCreatedNotification`, `PendingSessionRemovedNotification`, and `SentInvitationUpsertedNotification`.
- **Task:** Handle events by dropping a signal on a `Subject<Unit>`. Use `.Debounce(TimeSpan.FromMilliseconds(150), TimeProvider.System)` to trigger a private `ReloadAsync` method. 
- **Task:** Inside `ReloadAsync`, wrap the work in `using var scope = _scopeFactory.CreateScope();`, resolve `IPeerConnectionQueries`, await the snapshots, and safely mutate the `ObservableList`s inside a `SemaphoreSlim` lock.
- **Testing:** Create `PeerConnectionStateServiceTests.cs`. Inject a `FakeTimeProvider`, trigger notifications, advance time by 150ms, and verify the `ObservableList` updates correctly (Reference: `@source/Desktop.Wpf/r3.readme.md` TimeProvider section).

### Sub-Chunk A.5: UI Binding Updates & Cleanup Prompt
**Goal:** Wire the UI to the new service and remove the old architecture.
- **Task:** Update `App.xaml.cs` to register `PeerConnectionQueries` (Scoped), `PeerConnectionStateService` (Singleton), and map the MediatR notifications to the new service.
- **Task:** Update ViewModels (`SelectedChannelPaneViewModel`, `ConnectionManagementDialogViewModel`, etc.) to inject `PeerConnectionStateService` instead of `SecureChannelsStore`. Update their observable bindings using `.CreateView()` and `.ToNotifyCollectionChanged()` (Reference: `@source/Desktop.Wpf/wpf.readme.md`).
- **Task:** **STOP AND ASK THE USER** to physically delete `SecureChannelsProjection.cs`, `SecureChannelsStore.cs`, `SecureChannelModel.cs`, and any corresponding test files. Wait for user confirmation before proceeding to the next chunks.

### Definition of Done for Chunk A
- Domain complexity is hidden behind the `IPeerConnectionQueries` CQRS boundary.
- `PeerConnectionStateService` acts as a true R3 Singleton orchestrator managing in-memory collections without DB concurrency crashes.
- All dependencies are properly scoped and thoroughly unit-tested. 

### Chunk B — Refactor `PeerConnectionStateService` to Pure Clean CQRS

**Outcome:**
The UI state service becomes a thread-safe, pure state container. Background EF Core queries are executed safely within MediatR's built-in dependency injection scopes, eliminating `DbContext` concurrency crashes.

**Context / Key Constraints:**
* `IPeerConnectionQueries` relies on EF Core, which is Scoped and NOT thread-safe.
* `PeerConnectionStateService` is a Singleton. Injecting `IServiceScopeFactory` to handle concurrent MediatR events manually causes race conditions and violates the Single Responsibility Principle.
* **Bug Fix:** The previous debounce implementation accidentally dropped the `selfIdentityId` during reloads, causing new connections to never load. We must cache the active identity ID.

Please execute the following exactly as written.

#### B.1 — Strip `PeerConnectionStateService` down to a Pure State Container
**File:** `Desktop.Wpf/Features/Sessions/PeerConnectionStateService.cs`

**Changes Required:**
1. **Remove Interfaces:** Remove all `INotificationHandler<...>` interfaces from the class declaration.
2. **Remove Infrastructure:** Delete the `_scopeFactory`, `_timeProvider`, `_reloadTrigger`, and `_reloadGate` fields. Remove the debounce pipeline from the constructor.
3. **Cache Identity:** Add a public property: `public int? ActiveSelfIdentityId { get; private set; }`.
4. **Update Initialization:** Rewrite `InitializeAsync` to set the identity and perform the initial load using a one-off scope:
```csharp
public async Task InitializeAsync(int selfIdentityId, CancellationToken cancellationToken = default)
{
    ActiveSelfIdentityId = selfIdentityId;
    using var scope = _scopeFactory.CreateScope();
    var queries = scope.ServiceProvider.GetRequiredService<IPeerConnectionQueries>();

    var connectionSnapshots = await queries.LoadAllConnectionsAsync(selfIdentityId, cancellationToken).ConfigureAwait(false);
    UpdateConnections(connectionSnapshots);

    var pendingSnapshots = await queries.LoadPendingInboundAsync(cancellationToken).ConfigureAwait(false);
    UpdatePendingInbound(pendingSnapshots);
}
```
*(Note: Keep `IServiceScopeFactory` injected in the constructor ONLY for this single `InitializeAsync` method).*
5. **Expose Mutators:** Change the access modifier of `UpdateConnections` and `UpdatePendingInbound` from `private` to `public`. Remove the old `ReloadAsync` and `Handle(...)` methods entirely.

#### B.2 — Extract MediatR Handlers to a Scoped Class
**File:** Create a new file `Desktop.Wpf/Features/Sessions/Handlers/PeerConnectionStateUpdateHandlers.cs`

**Implementation details:**
Create a single class that implements the 4 notification handlers. Because this class is resolved by MediatR, it is safely scoped.

```csharp
using Desktop.Wpf.Features.Sessions.Queries;
using MediatR;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;

namespace Desktop.Wpf.Features.Sessions.Handlers;

public sealed class PeerConnectionStateUpdateHandlers :
    INotificationHandler<SecureSessionCreatedNotification>,
    INotificationHandler<PendingSessionCreatedNotification>,
    INotificationHandler<PendingSessionRemovedNotification>,
    INotificationHandler<SentInvitationUpsertedNotification>
{
    private readonly IPeerConnectionQueries _queries;
    private readonly PeerConnectionStateService _state;

    public PeerConnectionStateUpdateHandlers(
        IPeerConnectionQueries queries,
        PeerConnectionStateService state)
    {
        _queries = queries;
        _state = state;
    }

    public Task Handle(SecureSessionCreatedNotification notification, CancellationToken cancellationToken) => ReloadStateAsync(cancellationToken);
    public Task Handle(PendingSessionCreatedNotification notification, CancellationToken cancellationToken) => ReloadStateAsync(cancellationToken);
    public Task Handle(PendingSessionRemovedNotification notification, CancellationToken cancellationToken) => ReloadStateAsync(cancellationToken);
    public Task Handle(SentInvitationUpsertedNotification notification, CancellationToken cancellationToken) => ReloadStateAsync(cancellationToken);

    private async Task ReloadStateAsync(CancellationToken cancellationToken)
    {
        // 1. Safely query EF Core using the scoped queries instance
        if (_state.ActiveSelfIdentityId.HasValue)
        {
            var connections = await _queries.LoadAllConnectionsAsync(_state.ActiveSelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
            _state.UpdateConnections(connections);
        }

        var pending = await _queries.LoadPendingInboundAsync(cancellationToken).ConfigureAwait(false);
        _state.UpdatePendingInbound(pending);
    }
}
```

#### Definition of Done:
1. `PeerConnectionStateService` no longer implements `INotificationHandler`.
2. `PeerConnectionStateService` no longer contains a `Subject` or `Debounce` pipeline.
3. The newly created `PeerConnectionStateUpdateHandlers` compiles cleanly and uses native dependency injection for the query interface.
4. When a `SecureSessionCreatedNotification` fires, the `ActiveSelfIdentityId` is successfully utilized to fetch and update the connections list.

### Chunk C — Fix Concurrency, Disposal Glitches, and Restore Debounce Shield

**Outcome:**
1. Fixes thread-safety race conditions by locking the pure Domain mutations in `PeerConnectionStateService`.
2. Fixes WPF UI glitches/R3 ObjectDisposedExceptions by correcting the remove-then-dispose order.
3. Fixes the "Thundering Herd" N+1 query regression by introducing a `PeerConnectionReloadCoordinator` to debounce MediatR events before hitting EF Core.

Please execute the following three steps exactly as written:

#### C.1 — Fix Thread-Safety and Disposal Order
**File:** `Desktop.Wpf/Features/Sessions/PeerConnectionStateService.cs`

**Changes Required:**
1. Add a locking primitive to the fields: `private readonly object _stateGate = new();`
2. Update `UpdateConnections` to lock the mutation and fix the disposal order:
```csharp
public void UpdateConnections(IReadOnlyList<PeerConnectionStateSnapshot> snapshots)
{
    lock (_stateGate)
    {
        var existingById = _connections.ToDictionary(c => c.ConnectionId);

        var toRemove = existingById.Keys.Except(snapshots.Select(s => s.ConnectionId)).ToList();
        foreach (var id in toRemove)
        {
            if (existingById.TryGetValue(id, out var model))
            {
                // FIX: Remove from the collection BEFORE disposing to prevent UI glitching
                _connections.Remove(model);
                model.Dispose();
            }
        }

        foreach (var snapshot in snapshots)
        {
            if (existingById.TryGetValue(snapshot.ConnectionId, out var existing))
            {
                existing.UpdateFromSnapshot(snapshot);
            }
            else
            {
                var model = new PeerConnectionModel(
                    snapshot.ConnectionId,
                    snapshot.PeerId,
                    snapshot.DisplayName,
                    snapshot.Initials,
                    snapshot.Status,
                    snapshot.LastActivityUtc);
                _connections.Add(model);
            }
        }
    }
}
```
3. Apply the exact same lock `lock (_stateGate)` and disposal order fix (`_pendingInbound.Remove(model); model.Dispose();`) to `UpdatePendingInbound`.

#### C.2 — Extract the Debounce Shield (The Coordinator)
**File:** Create a new file `Desktop.Wpf/Features/Sessions/PeerConnectionReloadCoordinator.cs`

**Context:** We must debounce the EF Core queries to prevent CPU spikes during mass network events, but we cannot pollute the State Service. This Coordinator handles the timing and scoped DI queries.

```csharp
using Desktop.Wpf.Features.Sessions.Queries;
using Microsoft.Extensions.DependencyInjection;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PeerConnectionReloadCoordinator : IDisposable
{
    private readonly Subject<Unit> _reloadTrigger = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PeerConnectionStateService _state;
    private DisposableBag _bag;

    public PeerConnectionReloadCoordinator(
        IServiceScopeFactory scopeFactory, 
        PeerConnectionStateService state)
    {
        _scopeFactory = scopeFactory;
        _state = state;

        _reloadTrigger
            .Debounce(TimeSpan.FromMilliseconds(250))
            .SubscribeAwait(async (_, ct) => await ReloadCoreAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public void TriggerReload() => _reloadTrigger.OnNext(Unit.Default);

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        if (!_state.ActiveSelfIdentityId.HasValue) return;

        using var scope = _scopeFactory.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<IPeerConnectionQueries>();

        var connections = await queries.LoadAllConnectionsAsync(_state.ActiveSelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
        _state.UpdateConnections(connections);

        var pending = await queries.LoadPendingInboundAsync(cancellationToken).ConfigureAwait(false);
        _state.UpdatePendingInbound(pending);
    }

    public void Dispose()
    {
        _bag.Dispose();
        _reloadTrigger.Dispose();
    }
}
```

#### C.3 — Update Handlers to use the Coordinator
**File:** `Desktop.Wpf/Features/Sessions/Handlers/PeerConnectionStateUpdateHandlers.cs`

**Changes Required:**
1. Replace the injected `IPeerConnectionQueries` and `PeerConnectionStateService` with the new `PeerConnectionReloadCoordinator`.
2. Remove the `ReloadStateAsync` method entirely.
3. Update all four `Handle` methods to simply call `_coordinator.TriggerReload()`.

```csharp
public sealed class PeerConnectionStateUpdateHandlers :
    INotificationHandler<SecureSessionCreatedNotification>,
    INotificationHandler<PendingSessionCreatedNotification>,
    INotificationHandler<PendingSessionRemovedNotification>,
    INotificationHandler<SentInvitationUpsertedNotification>
{
    private readonly PeerConnectionReloadCoordinator _coordinator;

    public PeerConnectionStateUpdateHandlers(PeerConnectionReloadCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task Handle(SecureSessionCreatedNotification notification, CancellationToken cancellationToken)
    {
        _coordinator.TriggerReload();
        return Task.CompletedTask;
    }
    // Repeat for the other 3 handlers...
}
```

#### Definition of Done:
1. `PeerConnectionStateService` safely locks mutations with `_stateGate` and removes items from observables *before* calling `.Dispose()`.
2. `PeerConnectionReloadCoordinator` successfully debounces MediatR events using R3.
3. All EF Core queries are safely executed inside the Coordinator's short-lived `CreateScope()`.

## Chunk D
### Refactor Request: Enforce Pure Reactive MVVM Guidelines

We need to clean up several ViewModels to adhere strictly to our R3 and WPF MVVM guidelines. Specifically, we must eliminate memory leaks caused by untracked `ISynchronizedView`s, remove imperative Dispatcher hacks, and use native reactive filtering.

Please execute the following steps exactly as written.

#### Step 1: Fix View Memory Leaks & Disposal Rules
Any time `CreateView` is called, the resulting `ISynchronizedView` MUST be stored in a private readonly field, added to the `_bag`, and have a cleanup subscription for child VMs.

**File 1: `Desktop.Wpf/Features/Sessions/PendingHandshakesMenuViewModel.cs`**
* **Fix:** Add a private field: `private readonly ISynchronizedView<PeerPendingInvitationModel, PendingHandshakeItem> _pendingView;`
* **Fix:** In the constructor, split the initialization:
    ```csharp
    _pendingView = _stateService.PendingInbound
        .CreateView(model => new PendingHandshakeItem { ... })
        .AddTo(ref _bag);
    
    PendingHandshakes = _pendingView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);
    ```

**File 2: `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`**
* **Fix:** Ensure `_connectionsView` is stored as an `ISynchronizedView` (not just the `NotifyCollectionChangedSynchronizedViewList`) and appended with `.AddTo(ref _bag)`.

#### Step 2: Replace "Shadow Collections" with Native Reactive Filtering
**File 3: `Desktop.Wpf/Features/Sessions/SessionsSidebarViewModel.cs`**
This file currently uses a hacky shadow `ObservableCollection` and listens to `INotifyCollectionChanged` to filter items. We must use R3 and Cysharp's native filtering.

* **Delete:** `_items`, `OnConnectionsCollectionChanged`, and `ApplyFilter`.
* **Change:** Change `Items` from `ReadOnlyObservableCollection` to `NotifyCollectionChangedSynchronizedViewList<PeerConnectionListItemViewModel>`.
* **Rewrite Constructor Initialization:** Use `AttachFilter` and `RefreshFilter` instead of shadow lists:
    ```csharp
    // 1. Create the view and track it
    var view = _stateService.Connections
        .CreateView(model => new PeerConnectionListItemViewModel(model))
        .AddTo(ref _bag);

    // 2. Dispose child VMs when removed from the domain
    view.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose()).AddTo(ref _bag);

    // 3. Attach the dynamic filter logic
    view.AttachFilter((model, vm) => 
    {
        var term = SearchText.Value?.Trim() ?? "";
        if (string.IsNullOrEmpty(term)) return true;
        // Apply your search logic here (e.g., model.DisplayName.Contains)
        return model.DisplayName.CurrentValue?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    });

    // 4. Force the view to re-evaluate the filter when the search text changes
    SearchText.Subscribe(_ => view.RefreshFilter()).AddTo(ref _bag);

    // 5. Expose to WPF
    Items = view.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);
    ```

#### Step 3: Eliminate Manual Dispatcher Hacks
**File 4: `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`**
ViewModels should rely on the SynchronizationContext naturally provided by `AsyncRelayCommand` executions, rather than manually invoking the UI Dispatcher.

* **Delete:** The `InvokeOnUiAsync` helper method entirely.
* **Fix:** Remove all `Application.Current?.Dispatcher.InvokeAsync` blocks.
* **Fix:** In methods triggered by the UI (like `ExecuteImportTokenAsync`, `ExecuteNetworkSearchAsync`, etc.), simply assign values directly to the `BindableReactiveProperty` fields (e.g., `PhaseText.Value = "Decoding...";`).
* **Note on `.ConfigureAwait(false)`:** Ensure that inside your ICommand/AsyncRelayCommand execution methods, you do **not** use `.ConfigureAwait(false)` if you intend to update UI properties immediately afterward. Omitting it allows the `await` to seamlessly resume on the WPF UI thread.

#### Definition of Done:
1. No `CreateView()` calls are left un-added to a `_bag`.
2. `SessionsSidebarViewModel` no longer implements `INotifyCollectionChanged` event handlers.
3. `ConnectionManagementDialogViewModel` no longer references `Application.Current.Dispatcher`.

## Chunk E — Clean Architecture: Extract Application Logic & Fix Reactive MVVM

**Context & AI Instructions:**
You are to execute a strict refactoring to adhere to our WPF/R3 Clean Architecture guidelines. You must follow every step exactly. Do not skip steps. Do not leave old, unused code behind.

**Goals:**
1. Eradicate the "Fat ViewModel" anti-pattern in `ConnectionManagementDialogViewModel` by extracting X3DH, cryptography, and network logic into MediatR handlers.
2. Upgrade plain POCO items into fully reactive, `IDisposable` ViewModels.
3. Fix WPF thread-crashing bugs and enforce native Cysharp `ObservableCollections` filtering.

---

#### Step 1: Upgrade Plain POCOs to Reactive ViewModels
We must change list items from basic properties to `IDisposable` ViewModels so they can update the UI dynamically.

**1A. Replace `PendingInvitationItem`**
Open `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`. Delete the `PendingInvitationItem` class.
Create a new file: `Desktop.Wpf/Features/Sessions/PendingInvitationItemViewModel.cs`:
```csharp
using R3;
using System;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingInvitationItemViewModel : IDisposable
{
    private DisposableBag _bag;

    public Guid PendingSessionId { get; }
    public string DisplayName { get; }
    public string Initials { get; }
    public bool IsRelayed { get; }
    public string? RelayInfoText { get; }
    
    // Reactive properties for UI updates
    public BindableReactiveProperty<string> StatusText { get; }
    public BindableReactiveProperty<bool> IsExpired { get; }

    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }

    public PendingInvitationItemViewModel(Guid pendingSessionId, string displayName, string initials, bool isRelayed, string? relayInfoText)
    {
        PendingSessionId = pendingSessionId;
        DisplayName = displayName;
        Initials = initials;
        IsRelayed = isRelayed;
        RelayInfoText = relayInfoText;
        
        StatusText = new BindableReactiveProperty<string>("Pending").AddTo(ref _bag);
        IsExpired = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
    }

    public void Dispose() => _bag.Dispose();
}
```

**1B. Replace `PendingHandshakeItem`**
Open `Desktop.Wpf/Features/Sessions/PendingHandshakesMenuViewModel.cs`. Delete the `PendingHandshakeItem` class.
Create a new file: `Desktop.Wpf/Features/Sessions/PendingHandshakeItemViewModel.cs` applying the exact same reactive pattern (constructor taking `DisplayName`, `Initials`, `BundleText`, `PendingId`, `IsRelayed`, `RelayInfoText`; and `StatusText`/`IsExpired` as `BindableReactiveProperty`). Update `PendingHandshakesMenuViewModel` to use this new type and call `.Dispose()` on old items when re-creating the view.

---

#### Step 2: Extract Cryptography and Network Logic into MediatR Commands
ViewModels must NEVER perform cryptography or call Repositories.

**2A. Create Token Decode Command**
Create `Desktop.Wpf/Features/Sessions/Commands/DecodeAndQueueInviteCommand.cs`:
```csharp
using MediatR;

namespace Desktop.Wpf.Features.Sessions.Commands;

public record DecodeAndQueueInviteCommand(string Token) : IRequest<DecodeAndQueueInviteResult>;

public abstract record DecodeAndQueueInviteResult
{
    public sealed record Success : DecodeAndQueueInviteResult;
    public sealed record Failed(string ErrorMessage) : DecodeAndQueueInviteResult;
}
```

**2B. Create Token Decode Handler**
Create `Desktop.Wpf/Features/Sessions/Handlers/DecodeAndQueueInviteCommandHandler.cs`.
* Implement `IRequestHandler<DecodeAndQueueInviteCommand, DecodeAndQueueInviteResult>`.
* **Action:** Cut the entire `try/catch` decoding, ECDSA signature verification (`VerifyInvitePayloadSignature`), and `_establishDirectSession.QueueInviteAsync` logic out of `ConnectionManagementDialogViewModel.ExecuteImportTokenAsync` and paste it into this handler's `Handle` method.
* **Dependencies to inject:** `IEstablishDirectSessionService`, `ActiveIdentityContext`.

**2C. Create Network Search Command**
Create `Desktop.Wpf/Features/Sessions/Commands/ConnectViaNetworkCommand.cs`:
```csharp
using MediatR;
using System;

namespace Desktop.Wpf.Features.Sessions.Commands;

public record ConnectViaNetworkCommand(
    string RouteMode, 
    string? DirectEndpoint, 
    string? TargetPkhText, 
    Guid? RelayHostPeerId, 
    string? TargetDisplayName) : IRequest<ConnectViaNetworkResult>;

public abstract record ConnectViaNetworkResult
{
    public sealed record Success : ConnectViaNetworkResult;
    public sealed record TargetOffline : ConnectViaNetworkResult;
    public sealed record Failed(string ErrorMessage) : ConnectViaNetworkResult;
}
```

**2D. Create Network Search Handler**
Create `Desktop.Wpf/Features/Sessions/Handlers/ConnectViaNetworkCommandHandler.cs`.
* Implement `IRequestHandler<ConnectViaNetworkCommand, ConnectViaNetworkResult>`.
* **Action:** Cut the entire `RouteMode == "direct"` and `RouteMode == "relay"` branches out of `ConnectionManagementDialogViewModel.ExecuteNetworkSearchAsync` and paste them here. This includes X3DH initiation (`_sessionCrypto.X3DH_Initiate`), Protobuf envelope building, and `_preHandshake.SaveAsync`.
* **Dependencies to inject:** `IMainReverseSignalInviteFactory`, `IGrpcSessionService`, `ISecureMessagingService`, `IMessageTransportService`, `IDirectSessionRepository`, `IPeerIdentityRepository`, `ISessionCrypto`, `IPreHandshakeSessionStore`, `ISentInvitationRepository`, `Percolator.Application.Services.IClock`, and `ActiveIdentityContext`.

---

#### Step 3: Refactor ConnectionManagementDialogViewModel (Thin & Thread-Safe)
Open `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`.

**3A. Strip Dependencies**
Remove all cryptography, transport, and repository interfaces from the constructor. You only need: `IMediator`, `IUiDispatcher`, `ActiveIdentityContext`, `IMainInvitationInbox`, `IMainInvitationInboxEvents`, and `PeerConnectionStateService`.

**3B. Fix RelayHost Threading Crash**
* Delete `private readonly ObservableCollection<RelayHostOption> _relayHostOptions = new();`
* Add:
  ```csharp
  private readonly ObservableCollections.ObservableList<RelayHostOption> _relayHostOptions = new();
  private readonly ObservableCollections.ISynchronizedView<RelayHostOption, RelayHostOption> _relayHostView;
  ```
* In the constructor:
  ```csharp
  _relayHostView = _relayHostOptions.CreateView(x => x).AddTo(ref _bag);
  RelayHostOptions = _relayHostView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);
  ```
* In `QueueRelayHostRefresh()`, completely delete the `Task.Run` wrapper. Just call the async method directly (fire and forget). Since `_relayHostOptions` is now a pure domain list, it is safe to modify from background threads.

**3C. Fix Execute Methods**
Rewrite the execution methods to use the commands you created in Step 2:
```csharp
private async Task ExecuteImportTokenAsync(CancellationToken ct = default)
{
    ResetStatus();
    PhaseText.Value = "Processing...";
    
    var result = await _mediator.Send(new DecodeAndQueueInviteCommand(InviteTokenText.Value), ct);
    
    if (result is DecodeAndQueueInviteResult.Failed failed)
    {
        ErrorText.Value = failed.ErrorMessage;
        PhaseText.Value = null;
        return;
    }
    
    PhaseText.Value = null;
    await RefreshInboxAsync(ct);
    SelectedTabIndex.Value = 0;
}
```
*(Apply the exact same MediatR dispatch pattern to `ExecuteNetworkSearchAsync` using `ConnectViaNetworkCommand`).*

**3D. Update Inbox to use ViewModels**
In `RefreshInboxAsync`, change `ObservableCollection<PendingInvitationItem>` to `ObservableList<PendingInvitationItemViewModel>`. Ensure you call `.Dispose()` on old items when clearing the list. When `AcceptInvitationCommand` succeeds, update `item.StatusText.Value = "Accepted";` (using `.Value` since it is now reactive).

---

#### Step 4: Fix Sidebar Filtering Rules
Open `Desktop.Wpf/Features/Sessions/SessionsSidebarViewModel.cs`.

**4A. Delete Anti-Patterns**
* DELETE `private readonly ObservableCollection<PeerConnectionListItemViewModel> _items = new();`.
* DELETE the `ApplyFilter` method.
* DELETE the `OnConnectionsCollectionChanged` method.

**4B. Implement Native R3 Filtering**
Rewrite the constructor to properly project, track, and filter the view natively without shadow collections:
```csharp
// 1. Create the view and track it
_connectionsView = _stateService.Connections
    .CreateView(model => new PeerConnectionListItemViewModel(model))
    .AddTo(ref _bag);

// 2. Dispose child VMs when removed from the domain
_connectionsView.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose()).AddTo(ref _bag);

// 3. Attach the dynamic filter logic
_connectionsView.AttachFilter((model, vm) => 
{
    var term = SearchText.Value?.Trim() ?? "";
    if (string.IsNullOrEmpty(term)) return true;
    return vm.DisplayName.CurrentValue?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
});

// 4. Force the view to re-evaluate when search text changes
SearchText.Subscribe(_ => _connectionsView.RefreshFilter()).AddTo(ref _bag);

// 5. Expose to WPF
Items = _connectionsView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);
```

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

