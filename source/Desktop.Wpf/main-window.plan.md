## Chunk A

### Connection Management Dialog (Incoming Signals) – Big-bang redesign plan (concrete changes)

**Intent:** Make the Incoming Signals tab a projection of the *existing canonical pending inbound state* (`PeerConnectionStateService.PendingInbound`) and its reload mechanism (`PeerConnectionReloadCoordinator` + MediatR notifications), instead of maintaining its own private list and private refresh/event-bus.

This is a convergence plan:

- The app already has a canonical “pending inbound” concept:
  - Query: `Percolator.Application.Sessions.IPeerConnectionQueries.LoadPendingInboundAsync(...)`
  - State: `Desktop.Wpf.Features.Sessions.PeerConnectionStateService.PendingInbound`
  - Reload: `Desktop.Wpf.Features.Sessions.PeerConnectionReloadCoordinator`
  - Signals: `Desktop.Wpf.Features.Sessions.Handlers.PeerConnectionStateUpdateHandlers` handles
    - `PendingSessionCreatedNotification`
    - `PendingSessionRemovedNotification`

The dialog should use that, and we should delete the parallel system (`IMainInvitationInbox`, `IMainInvitationInboxEvents`, ad-hoc list rebuild).

---

## A1) Define the UI data contract by mapping it to existing snapshots (no new query interface)

**Decision:** Do *not* introduce a new `IIncomingSignalsQueries` right now.

Reason: The dialog’s data needs are already covered by `PendingInboundSnapshot` (relay metadata, inviter fingerprint, expiry, etc.) loaded by `IPeerConnectionQueries.LoadPendingInboundAsync()`. The missing piece is: the **model** exposed by `PeerConnectionStateService.PendingInbound` currently throws away most of that snapshot.

**Layering decision:** `IPeerConnectionQueries` is a cross-domain read model query and therefore belongs in `Percolator.Application`. Its implementation belongs in `Percolator.Infrastructure`. WPF is responsible for presentation concerns (e.g., computing initials), and those do not need to move into `Percolator.Application`.

#### Concrete change (required): move `IPeerConnectionQueries` + snapshots out of WPF

1) Create the application-layer query contract.

- **File:** `Percolator.Application/Sessions/IPeerConnectionQueries.cs` (new)
  - Move the existing WPF `IPeerConnectionQueries` interface here.
  - Update namespaces to `Percolator.Application.Sessions`.

2) Move the read model record(s) used by the contract.

- **File:** `Percolator.Application/Sessions/PendingInboundSnapshot.cs` (new)
  - Move the existing WPF `PendingInboundSnapshot` record here.
  - Keep this as a pure immutable record.

- **File:** `Percolator.Application/Sessions/PeerConnectionStateSnapshot.cs` (new)
  - Move the existing WPF `PeerConnectionStateSnapshot` record here (if `LoadAllConnectionsAsync(...)` remains part of the contract).
  - Keep this as a pure immutable record.

3) Create the infrastructure implementation.

- **File:** `Percolator.Infrastructure/Sessions/PeerConnectionQueries.cs` (new)
  - Move the existing WPF `PeerConnectionQueries` implementation here.
  - Update namespaces to `Percolator.Infrastructure.Sessions`.
  - Implement `Percolator.Application.Sessions.IPeerConnectionQueries`.

4) Update DI wiring.

- **File:** `Percolator.Infrastructure/ServiceCollectionExtensions.cs` (or `Percolator.Infrastructure/Sessions/...` if sessions has its own extensions)
  - Register `IPeerConnectionQueries` -> `PeerConnectionQueries`.
- **File:** `Desktop.Wpf/App.xaml.cs`
  - Remove any WPF registrations for the old `Desktop.Wpf.Features.Sessions.Queries.IPeerConnectionQueries`.
    - Expected existing registration to remove/replace:
      - `services.AddScoped<Desktop.Wpf.Features.Sessions.Queries.IPeerConnectionQueries, Desktop.Wpf.Features.Sessions.Queries.PeerConnectionQueries>();`
  - Ensure WPF references `Percolator.Application.Sessions` for the interface.

5) Delete/retire the old WPF query artifacts.

- **Files:**
  - `Desktop.Wpf/Features/Sessions/Queries/IPeerConnectionQueries.cs` (delete)
  - `Desktop.Wpf/Features/Sessions/Queries/PendingInboundSnapshot.cs` (delete)
  - `Desktop.Wpf/Features/Sessions/Queries/PeerConnectionQueries.cs` (delete)
  - `Desktop.Wpf/Features/Sessions/Queries/PeerConnectionStateSnapshot.cs` (delete, if it exists)

6) Update all call sites.

- Anywhere injecting or referencing these types must update `using` statements and namespaces from:
  - `Desktop.Wpf.Features.Sessions.Queries.*`
  - to:
    - `Percolator.Application.Sessions` (contract + snapshots)
    - `Percolator.Infrastructure.Sessions` (implementation only where needed; usually only DI)

### Concrete changes

1) Extend `Desktop.Wpf/Features/Sessions/Models/PeerPendingInvitationModel.cs` to store all fields from `PendingInboundSnapshot` that the dialog UI needs.

- **File:** `Desktop.Wpf/Features/Sessions/Models/PeerPendingInvitationModel.cs`
- **Change constructor signature** to accept the full `PendingInboundSnapshot` (or individual fields):
  - Must include:
    - `CreatedAtUtc`, `ExpiresAtUtc`, `InviterFingerprintHex`
    - `IsRelayed`, `RelayPeerId`, `RelayPeerName`, `RelayEndpoint`
    - and the existing ids + peer name
- **Add reactive properties** for fields that can change on reload:
  - At minimum:
    - `PeerName`
    - `IsRelayed`
    - `ExpiresAtUtc`
    - `RelayPeerName`, `RelayEndpoint` (relay identity/routing can change)
  - Keep ids as plain get-only properties.
- **Update** `UpdateFromSnapshot(PendingInboundSnapshot snapshot)` to update all mutable fields.

2) Extend `Percolator.Application/Sessions/PendingInboundSnapshot.cs` only if the UI truly needs more than is already present.

- **File:** `Percolator.Application/Sessions/PendingInboundSnapshot.cs`
  - Only extend the application-layer snapshot if the UI truly needs more than is already present.
  - **Question:** Do we also need `CallbackEndpointHost/Port`? (It’s on `PendingSession` domain model but not in this snapshot.)
    - If yes, add it to the application-layer snapshot and map it in `Percolator.Infrastructure/Sessions/PeerConnectionQueries.cs`.

---

## A2) Make `PeerConnectionStateService.PendingInbound` the source of truth for the dialog

### Concrete changes

1) Update the pending inbound creation path to pass the full snapshot into the model.

- **File:** `Desktop.Wpf/Features/Sessions/PeerConnectionStateService.cs`
- **Method:** `UpdatePendingInbound(IReadOnlyList<PendingInboundSnapshot> snapshots)`
- **Change:** Replace the current `new PeerPendingInvitationModel(...)` call (currently passes only a subset) to pass the additional snapshot fields.

2) Make pending inbound query identity-scoped (even though identity scoping is not implemented yet).

- **File:** `Percolator.Application/Sessions/IPeerConnectionQueries.cs`
  - Change signature to `LoadPendingInboundAsync(SelfId selfIdentityId, CancellationToken ct = default)`.
- **File:** `Percolator.Infrastructure/Sessions/PeerConnectionQueries.cs`
  - Implement the new parameter.
  - **Temporary multi-identity guardrail:** add `_logger.LogWarning(...)` every time this query runs stating that `selfIdentityId` is currently ignored due to implicit EF global query filters / single-active-identity scoping.
    - Example wording: "LoadPendingInboundAsync(selfIdentityId=...) currently ignores identity scoping due to implicit query filters; multi-identity is not implemented yet."
  - **Additional guardrail:** if `ActiveIdentityContext.Identity` is available and its `SelfIdentityId` differs from the requested `selfIdentityId`, log a warning indicating the mismatch.

3) Ensure `PeerConnectionStateService.InitializeAsync(SelfId selfIdentityId, ...)` remains the single “bootstrap” for pending inbound.

- **File:** `Desktop.Wpf/Features/Shell/ShellViewModel.cs`
- **Already:** calls `_peerConnectionStateService.InitializeAsync(domainIdentity.Id, ...)`.
- **No additional dialog init** should be needed.

---

## A3) Replace ConnectionManagementDialog’s private inbox + events + local list with a projection of the state service

### Concrete changes

1) Delete dialog-only “pending invitations” list rebuild.

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`
- **Remove fields:**
  - `_inbox : IMainInvitationInbox`
  - `_inboxEvents : IMainInvitationInboxEvents`
  - `_pendingInvitations : ObservableList<PendingInvitationItemViewModel>`
  - `_pendingInvitationsView : ISynchronizedView<...>`
- **Remove methods:**
  - `RefreshInboxAsync(...)`
  - `RefreshInboxCommand` and its wiring
- **Remove subscription:**
  - `_inboxEvents.Changed.SubscribeAwait(...)`

2) Replace `PendingInvitations` property to be a projection from `_stateService.PendingInbound`.

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`
- **Add fields:**
  - `ISynchronizedView<PeerPendingInvitationModel, PendingInvitationItemViewModel> _pendingInboundView;`
- **In constructor:**
  - Create view from `_stateService.PendingInbound.CreateView(model => new PendingInvitationItemViewModel(model, ...))`
  - Bridge with `.ToNotifyCollectionChanged(ui.CollectionEventDispatcher)`
  - Ensure disposal on remove:
    - `_pendingInboundView.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose())`

3) Refactor `PendingInvitationItemViewModel` to be a projection wrapper over `PeerPendingInvitationModel`.

- **File:** `Desktop.Wpf/Features/Sessions/PendingInvitationItemViewModel.cs`
- **Replace constructor signature** from raw primitives to `PendingInvitationItemViewModel(PeerPendingInvitationModel model, IUiDispatcher ui, TimeProvider? maybe)`.
- **Replace properties:**
  - `DisplayName`, `IsRelayed`, `RelayInfoText`, `IsExpired` should be *derived reactively* from the model.
- **Follow `r3.readme.md` for properties:**
  - `model.PeerName.DistinctUntilChanged().ObserveOnCurrentSynchronizationContext().ToBindableReactiveProperty()`
  - For computed strings like relay info, use `Observable.CombineLatest` and the same marshal rule.

4) Update XAML bindings only as necessary.

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogWindow.xaml`
- Ensure bindings match the refactored `PendingInvitationItemViewModel` surface (keep names stable if possible).

---

## A4) Align actions (Accept/Burn) with the new architecture

### Concrete changes

1) Keep using MediatR commands for Accept/Burn, but remove UI-level “requery” calls.

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`
- **Update methods:** `ExecuteAcceptAsync` and `ExecuteBurnAsync`
  - Update to send identity-scoped commands:
    - `ApprovePendingSessionCommand(SelfId selfIdentityId, PendingSessionId pendingSessionId)`
    - `RejectPendingSessionCommand(SelfId selfIdentityId, PendingSessionId pendingSessionId)`
  - After a successful command, **do not** call refresh methods and do not attempt to display "Accepted".
  - On success, the item vanishes after reload.

2) `PendingInvitationItemViewModel.StatusText`

- Keep `StatusText` purely UI-ephemeral for error feedback only.

---

## A5) Fix and simplify signals: delete `IMainInvitationInboxEvents` and use the existing reload coordinator

### Concrete changes

1) Remove the custom inbox event bus entirely.

- **Files to delete (or leave unused, but big-bang suggests delete):**
  - `Desktop.Wpf/Features/Sessions/MainInvitationInboxEvents.cs`
  - `Desktop.Wpf/Features/Sessions/ConnectionManagementInboxEventListener.cs`
- **Remove DI registrations:**
  - **File:** `Desktop.Wpf/App.xaml.cs`
    - Remove `services.AddSingleton<IMainInvitationInboxEvents, MainInvitationInboxEvents>();`

2) Remove `IMainInvitationInbox` and `MainInvitationInbox` if they become unused.

- **File:** `Desktop.Wpf/Features/Sessions/MainInvitationContracts.cs`
  - Remove `IMainInvitationInbox` and `PendingInvitationDto` if nothing else references them.
- **File:** `Desktop.Wpf/Features/Sessions/MainInvitationServices.cs`
  - Remove `MainInvitationInbox`.
- **File:** `Desktop.Wpf/App.xaml.cs`
  - Remove DI registrations for `IMainInvitationInbox`.

3) Reuse the existing signal path for correctness.

- **Existing:** `PeerConnectionStateUpdateHandlers` triggers `PeerConnectionReloadCoordinator.TriggerReload(selfId)` on:
  - `PendingSessionCreatedNotification`
  - `PendingSessionRemovedNotification`
- **Result:** the dialog’s list updates automatically because it projects from `PeerConnectionStateService.PendingInbound`.

---

## A6) Optional convergence: remove `PendingHandshakesMenuViewModel` duplication

This is a “nice to have” after the dialog is fixed.

### Concrete changes (optional)

- Update `Desktop.Wpf/Features/Sessions/PendingHandshakesMenuViewModel.cs` to project from `_stateService.PendingInbound` (it already does) *and* update its item VM to use the same underlying `PeerPendingInvitationModel` fields as the dialog.
- Consider extracting a shared item VM factory/projection to avoid two different list-item VMs with subtly different behavior.

---

## A7) Tests (research questions turned into concrete test cases)

### Concrete changes

1) Add/extend unit tests proving that the dialog list updates when `PeerConnectionStateService.UpdatePendingInbound(...)` removes an item.

- **File:** likely new tests in `Desktop.Wpf.Tests` alongside existing state service tests.
- Pattern:
  - Instantiate `PeerConnectionStateService`
  - Seed with one pending inbound snapshot
  - Create dialog VM with a test `IUiDispatcher`
  - Call `UpdatePendingInbound(Array.Empty<...>())`
  - Assert the projected `PendingInvitations` count becomes 0.

2) Extend existing `PeerConnectionStateServiceTests.UpdatePendingInbound_adds_updates_and_removes_models_based_on_snapshot` to assert that the newly-added fields are updated correctly (relay info, expiry, fingerprint).

---

## A8) Identity grouping (forward-compatible, required by architecture)

**Decision:** Assume multiple self-identities can be active concurrently (feature not implemented yet). Peer connection + pending inbound state must therefore be **grouped by `SelfId`**.

**Additional decision:** Grouping implementation uses **per-identity buckets** (no grouping computed from a flat list).

---

## A8.0) Introduce `IdentityStateService` as the canonical list of active identities

### Concrete changes

1) Add a singleton state service that represents the set of active identities.

- **File:** `Desktop.Wpf/Features/Shell/IdentityStateService.cs` (new)
 - Owns:
  - `ObservableList<ActiveIdentityModel>`
 - Exposes:
  - `IReadOnlyObservableList<ActiveIdentityModel> Identities`

**R3 constraint:** Services do **not** filter or sort collections (`r3.readme.md`). `IdentityStateService` therefore exposes the full identity list; any view that needs “active identities” must create its own filtered reactive view.

**Additional design constraint:** ViewModels should not directly mutate Service-owned collections. Move identity bootstrap/upsert logic into `IdentityStateService` behind a dedicated bootstrap interface.

**Concrete model shape (decision):**

- **File:** `Desktop.Wpf/Features/Shell/ActiveIdentityModel.cs` (new)
- Fields:
  - `SelfId Id`
  - `ReactiveProperty<string> DisplayName`
  - `ReactiveProperty<bool> Active`

2) Initial implementation: load the single identity supported today.

- **File:** `Desktop.Wpf/Features/Shell/IIdentityBootstrapper.cs` (new)
  - Add an interface responsible for initializing/upserting identities into the canonical state service.
  - Shape (suggested): `Task UpsertAsync(SelfId id, string displayName, bool active, CancellationToken ct = default)`.
- **File:** `Desktop.Wpf/Features/Shell/IdentityStateService.cs`
  - Implement `IIdentityBootstrapper`.
  - `UpsertAsync(...)` takes the domain lock for the identity list and adds/updates the `ActiveIdentityModel` in `Identities`.
- **File:** `Desktop.Wpf/Features/Shell/ShellViewModel.cs`
  - After resolving the domain identity, call `_identityBootstrapper.UpsertAsync(domainIdentity.Id, domainIdentity.DisplayName, active: true, ct)`.
- **File:** `Desktop.Wpf/App.xaml.cs`
  - Register `IdentityStateService` as `Singleton`.
  - Register `IIdentityBootstrapper` to resolve to the same singleton.

3) This becomes the sole source used by the Connection Management dialog to decide which identity groups to display.

- **Important:** The dialog ViewModel must filter identities locally by `ActiveIdentityModel.Active.Value == true` by creating a reactive view over `IdentityStateService.Identities`.

### Concrete changes

1) Refactor `PeerConnectionStateService` to store state per identity.

- **File:** `Desktop.Wpf/Features/Sessions/PeerConnectionStateService.cs`
- Replace:
  - `ObservableList<PeerConnectionModel> _connections`
  - `ObservableList<PeerPendingInvitationModel> _pendingInbound`
  - `SelfId? ActiveSelfIdentityId`
- With:
 - With per-identity buckets:
  - `ObservableDictionary<SelfId, PeerIdentityConnectionState> _bySelf`
  - `PeerIdentityConnectionState` owns:
    - `ObservableList<PeerConnectionModel> Connections`
    - `ObservableList<PeerPendingInvitationModel> PendingInbound`
    - `object Gate`
  - Add API:
    - `PeerIdentityConnectionState GetOrCreate(SelfId selfIdentityId)`
    - `bool TryGet(SelfId selfIdentityId, out PeerIdentityConnectionState state)`

2) Make reload APIs explicit about which identity they target.

- **File:** `Desktop.Wpf/Features/Sessions/PeerConnectionReloadCoordinator.cs`
- Replace `TriggerReload()` with:
  - `TriggerReload(SelfId selfId)`
- Replace `ReloadCoreAsync()` with:
  - `ReloadCoreAsync(SelfId selfId, CancellationToken ct)`
- In reload core:
  - Call `IPeerConnectionSidebarQueries.LoadSidebarConnectionsAsync(selfId.Value, ct)`
  - Call `IPeerConnectionQueries.LoadPendingInboundAsync(selfId, ct)` (see next item)
  - Update only that identity’s bucket in state service.

3) Update `IPeerConnectionQueries.LoadPendingInboundAsync` to be identity-scoped.

- **File:** `Percolator.Application/Sessions/IPeerConnectionQueries.cs`
  - Signature is identity-scoped (see A2).
- **File:** `Percolator.Infrastructure/Sessions/PeerConnectionQueries.cs`
  - For now the identity parameter is ignored due to implicit EF global query filters / single-active-identity scoping.
  - Add the same warning/guardrails described in A2.

4) Update MediatR notification handlers to reload the correct identity.

- **File:** `Desktop.Wpf/Features/Sessions/Handlers/PeerConnectionStateUpdateHandlers.cs`
- Change the handlers to call `TriggerReload(selfId)`.

**Technical constraint (must be addressed for multi-identity):**

- `PendingSessions` are currently globally scoped by EF query filter:
  - **File:** `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs`
  - `PendingSessionDbo` has `SelfIdentityId` and `HasQueryFilter(... e.SelfIdentityId == _active.Identity.SelfIdentityId.Value)`.
- This is compatible with a *single* active identity, but not with “multiple identities active concurrently” unless we:
  - run separate identity-scoped `DbContext` instances where `_active.Identity` is set per-scope, or
  - remove the query filter and require explicit `where SelfIdentityId == ...` in queries.

**Plan choice (scope constraint):** do **not** remove reliance on implicit global query filters for this big-bang change. Multi-identity correctness at the persistence layer is explicitly out-of-scope for Chunk A; instead we add identity parameters + warnings/guardrails so the architecture is forward-compatible.

5) Update dialog ViewModel to project pending inbound for all active identities (grouped).

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`
- **Decision:** The dialog lists *all incoming signals for all active identities*, grouped by identity display name.

### Concrete changes

1) Ensure pending inbound snapshots/models carry `SelfId` (and identity display name is resolvable).

- **Add to snapshot:**
  - **File:** `Percolator.Application/Sessions/PendingInboundSnapshot.cs`
  - Add: `SelfId SelfIdentityId` (and optionally `string SelfIdentityDisplayName`)
- **Map it:**
  - **File:** `Percolator.Infrastructure/Sessions/PeerConnectionQueries.cs`
  - Ensure `LoadPendingInboundAsync(SelfId selfId, ...)` sets the snapshot `SelfIdentityId = selfId`.
  - If we decide to include display name in the snapshot, join via self identity table or resolve via an identity lookup query.

2) Update `PeerPendingInvitationModel` to expose `SelfIdentityId`.

- **File:** `Desktop.Wpf/Features/Sessions/Models/PeerPendingInvitationModel.cs`
- Add `SelfId SelfIdentityId { get; }`.

3) Replace `PendingInvitations` with a grouped projection.

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`
- Replace the flat `PendingInvitations` surface with one of:
  - **Option A (recommended):** `INotifyCollectionChangedSynchronizedViewList<IncomingSignalsGroupViewModel> IncomingSignalGroups`
    - Each group holds:
      - `string IdentityDisplayName`
      - `INotifyCollectionChangedSynchronizedViewList<PendingInvitationItemViewModel> Items`
  - **Option B:** flatten into a single list with “header rows” (more hacky; avoid).

4) Use `IdentityStateService` as the stable identity-name source.

- For now, `IdentityStateService.Identities` will contain exactly one identity.
- For multi-identity later, `IdentityStateService.Identities` will hold N identities.
- The dialog creates its own filtered view of active identities and uses `DisplayName` as the group header.

5) Update the XAML to show grouping.

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogWindow.xaml`
- Replace the `ListBox` of items with an `ItemsControl` (or `ListBox`) of groups:
  - Group template:
    - Header: identity display name
    - Inner items: the pending invitation cards with Burn/Accept buttons.

---

## A9) Post-change dead code audit (conditional cleanup)

After implementing A1–A8 and confirming behavior, perform a dead-code audit and conditionally remove unused code paths to keep the architecture clean.

### Verification steps

1) Grep for remaining references to legacy paths.

- Search terms:
  - `IMainInvitationInbox`
  - `IMainInvitationInboxEvents`
  - `MainInvitationInbox`
  - `MainInvitationInboxEvents`
  - `ConnectionManagementInboxEventListener`
  - `PendingInvitationDto`
  - `RefreshInboxCommand`
  - `RefreshInboxAsync`

2) Build the solution.

- Ensure all projects compile after removals.

### Expected dead code (remove if unused)

#### Legacy inbox/event-bus system (WPF-only)

- **Files:**
  - `Desktop.Wpf/Features/Sessions/MainInvitationInboxEvents.cs`
  - `Desktop.Wpf/Features/Sessions/ConnectionManagementInboxEventListener.cs`
  - `Desktop.Wpf/Features/Sessions/MainInvitationServices.cs` (at least `MainInvitationInbox`)
  - `Desktop.Wpf/Features/Sessions/MainInvitationContracts.cs` (at least `IMainInvitationInbox`, `IMainInvitationInboxEvents`, `PendingInvitationDto`)
- **DI removals:**
  - **File:** `Desktop.Wpf/App.xaml.cs`
    - Remove registrations for:
      - `IMainInvitationInbox`
      - `IMainInvitationInboxEvents`

#### Dialog-local refresh/list rebuild

- **File:** `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogViewModel.cs`
  - Remove (once the dialog projects from `PeerConnectionStateService.PendingInbound`):
    - `RefreshInboxCommand`
    - `RefreshInboxAsync`
    - `_pendingInvitations` local list rebuild code
    - `_inbox` / `_inboxEvents` fields and subscriptions

#### WPF query artifacts (after moving to Application/Infrastructure)

- Remove if they still exist after the migration:
  - `Desktop.Wpf/Features/Sessions/Queries/IPeerConnectionQueries.cs`
  - `Desktop.Wpf/Features/Sessions/Queries/PendingInboundSnapshot.cs`
  - `Desktop.Wpf/Features/Sessions/Queries/PeerConnectionQueries.cs`
  - `Desktop.Wpf/Features/Sessions/Queries/PeerConnectionStateSnapshot.cs` (if it exists)

---

