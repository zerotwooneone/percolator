## Chunk A
 
### Goal
 
Make **main-initiated** handshakes create a **visible pending PeerConnection** in the session sidebar immediately (before the secure session is established), while ensuring **remote-initiated** (inbound) handshake requests **do not** create a new sidebar PeerConnection.
 
### Current Behavior (from code)
 
- **Sidebar list** comes from `PeerConnectionStateService.Connections`, which is populated only by:
  - `Desktop.Wpf.Features.Sessions.Queries.IPeerConnectionQueries.LoadAllConnectionsAsync(...)` (secure sessions only)
- **Inbound handshake requests** are loaded separately via:
  - `Desktop.Wpf.Features.Sessions.Queries.IPeerConnectionQueries.LoadPendingInboundAsync(...)` (currently *no* `selfIdentityId` parameter)
  - shown in `PendingHandshakesMenuViewModel` (not in sidebar)
- Reload is triggered (debounced) by `PeerConnectionStateUpdateHandlers` on:
  - `PendingSessionCreatedNotification`
  - `PendingSessionRemovedNotification`
  - `SecureSessionCreatedNotification`
  - `SentInvitationUpsertedNotification`
 
The reload plumbing already runs for outbound events, but the **query layer does not return outbound pending handshakes as sidebar connections**.
 
### Constraints / Invariants
 
- The sidebar ListBox selection is driven by a **string id**, so the sidebar must use a stable string representation of the selected item key.
- `SelectedChannelPaneViewModel` currently only resolves selections where `SecureChannelKeyType == SecureSession`.
- `PeerConnectionStatus` currently has:
  - `Direct`, `Relay`, `Group`
- `PeerConnectionStateService` currently tracks a single `ActiveSelfIdentityId`.
- Persistence layer uses EF Core global query filters tied to `ActiveIdentityContext` for tables including:
  - `PendingSessions`
  - `SentInvitations`

Upcoming requirement (not implemented here):
- Support multiple `SelfIdentityId` values that are active simultaneously.

This means we cannot fully support “select pending connection in sidebar” without extending:
- the key format / parsing (`PeerConnectionKey` already supports pending correlation / pending session)
- the status enum + selected-pane mapping.

Big-bang rollout assumptions for this plan:
- **No backward compatibility required** (assume empty DB and no persisted UI selection).
- **No shims/ports/synthetic keys**: sidebar identity is always the typed `PeerConnectionKey`.

### Plan (Research → Design → Implementation)

Implementation note:
- This is a **big-bang rollout**.
- The work is broken into sub-chunks to allow AI to implement in steps.
- The solution **does not need to compile between sub-chunks**.

Research conclusions (to eliminate ambiguity):
- Time source:
  - `Desktop.Wpf/App.xaml.cs` registers `IClock` (`services.AddSingleton<IClock, SystemClock>();`).
  - `PeerConnectionReloadCoordinator` already receives `TimeProvider` (for debounce), but **time-of-day for expiration must come from `IClock.UtcNow`**.
- Sent invitations persistence:
  - `SentInvitationDbo.RequestCorrelationId` is a `string`.
  - `SqliteSentInvitationRepository` persists `RequestCorrelationId` via `invitation.RequestCorrelationId.ToString()` and rehydrates via `Guid.TryParse(...)`.
  - SQLite provider cannot translate some `DateTimeOffset` comparisons; existing code materializes then filters in-memory.
- Selected pane UI:
  - `SelectedPaneState.Pending` already exists.
  - `SelectedChannelPaneViewModel` already has `BannerText`, `IsInputEnabled`, and `ActiveContent`.
 
#### A.1: Identify how “main-initiated handshake” is represented at rest
 
**Verified findings (code + persistence):**
 
- **Inbound (remote-initiated) handshake requests** are represented as **`PendingSessions`** rows.
  - Created in `Percolator.Application/Network/EstablishDirectSessionService.cs` via `PendingSession.FromInvitationWithMetadata(...)`.
  - Persisted through `IPendingSessionRepository.AddAsync(...)` and then:
    - publishes `PendingSessionCreatedNotification(pending.Id)`.
  - Queried for UI via `Percolator.Infrastructure/Application/PendingHandshakeQueries.cs` which filters:
    - `PendingSessions.State == (int)ApprovalState.AwaitingApproval`.
  - UI surface area today:
    - `PeerConnectionReloadCoordinator` loads these into `PeerConnectionStateService.PendingInbound` via `LoadPendingInboundAsync`.
    - shown in `PendingHandshakesMenuViewModel`.
 
- **Outbound (main-initiated) handshake attempts** are represented as **`SentInvitations`** rows.
  - Created when main creates an invite:
    - `Percolator.Application/Network/MainReverseSignalInviteFactory.cs`:
      - generates a new `correlation` GUID (`RequestCorrelationId`)
      - persists `ISentInvitationRepository.UpsertAsync(new SentInvitation(...))`
      - publishes `SentInvitationUpsertedNotification(RequestCorrelationId)`.
    - `Desktop.Wpf/Features/Sessions/Handlers/ConnectViaNetworkCommandHandler.cs` also persists `SentInvitation` for the relay path.
  - Persistence details:
    - `SentInvitationDbo` is stored in table `SentInvitations` keyed by `(SelfIdentityId, RequestCorrelationId)`.
    - `SqliteSentInvitationRepository.EnumerateUnexpiredAsync(nowUtc)` loads unexpired attempts (in-memory filter due to SQLite DateTimeOffset limitations).
 
- **Critical discriminator:**
  - “Show pending in sidebar *only for main-initiated*” == load from `SentInvitations` (not from `PendingSessions`).
  - “Do not show pending in sidebar for remote-initiated” == keep using `PendingSessions` only for `PendingMenu`.
 
**Implication for design:** outbound pending sidebar items should be keyed by `RequestCorrelationId` (the same correlation stored in `SentInvitations`), i.e. `PeerConnectionKey.FromPendingCorrelationId(correlationGuid)`.
 
#### A.2: Introduce an application-layer sidebar projection query

**Revised approach (idiomatic + architecture-correct):** introduce an **application-layer query interface** whose purpose is to answer:

> “Which PeerConnections should the UI show in the sidebar?”

Rationale:
- No single domain (Identity / Cryptography / Network) can answer this question alone.
- The result is a UI projection across multiple domains/tables.
- The application layer is the correct place to define the query contract.
- Infrastructure is the correct place to implement it using `PercolatorDbContext` and cross-table queries.

**Big-bang change:**
- Replace `Desktop.Wpf.Features.Sessions.Queries.IPeerConnectionQueries` with the new app-layer contract and update all consumers in the same rollout.

**Files to change/add (conceptual):**
- Add (Application):
  - `Percolator.Application/Sessions/IPeerConnectionSidebarQueries.cs`
  - `Percolator.Application/Sessions/SidebarPeerConnectionDto.cs`
  - `Percolator.Application/Sessions/SidebarPeerConnectionStatus.cs`
  - `Percolator.Application/Sessions/SidebarPeerConnectionKeyType.cs`
- Add (Infrastructure):
  - `Percolator.Infrastructure/Sessions/PeerConnectionSidebarQueries.cs`
- Update (Desktop.Wpf):
  - `PeerConnectionReloadCoordinator` and `PeerConnectionStateService.InitializeAsync` to depend on the app-layer interface.

**Application-layer interface (proposed):**
- `Task<IReadOnlyList<SidebarPeerConnectionDto>> LoadSidebarConnectionsAsync(int selfIdentityId, CancellationToken cancellationToken = default);`

Notes:
- Return DTOs that are flat (or as flat as possible) and contain exactly what the sidebar needs.
- Keep identity explicit in the contract to enable a future “load once per active identity and merge” orchestration.

**DTO shape (proposed `SidebarPeerConnectionDto`):**
- Identifiers:
  - `int SelfIdentityId`
  - `SidebarPeerConnectionKeyType KeyType` (`SecureSession` / `PendingCorrelation`)
  - `Guid KeyValue`
- Peer info:
  - `Guid? PeerId`
  - `string DisplayName`
  - `string Initials`
- Sidebar state:
  - `SidebarPeerConnectionStatus Status` (`Direct` / `Relay` / `Group` / `PendingOutbound`)
  - `Guid? RelayHostPeerId`
  - `DateTimeOffset LastActivityUtc`

Layering invariant:
- The DTO and status enum live in `Percolator.Application` and must not reference `Desktop.Wpf` types.
- WPF maps `SidebarPeerConnectionStatus` → its own `PeerConnectionStatus` for presentation and selected-pane mapping.
- WPF maps `SidebarPeerConnectionKeyType` + `KeyValue` → `PeerConnectionKey`.

**Infrastructure implementation (high level):**
- Implement in Infrastructure against `PercolatorDbContext` directly.
- Do not depend on domain repositories; instead, compose cross-domain joins in SQL/EF.
- Identity scoping:
  - Use `IgnoreQueryFilters()` and explicitly filter `SelfIdentityId == selfIdentityId`.
- Time scoping:
  - Inject `Percolator.Cryptography.IClock` and compute `nowUtc = clock.UtcNow`.

**Concrete query logic (DbContext tables):**
- Established secure sessions source:
  - `PercolatorDbContext.Sessions` (`SessionDbo`)
    - keys: `(SessionId, SelfIdentityId)`
    - fields needed:
      - `SessionId` → `KeyType=SecureSession`, `KeyValue`
      - `RemotePeerId` → `PeerId`
      - `LastUsedAtUtc` → `LastActivityUtc`
- Direct-vs-relay determination source:
  - `PercolatorDbContext.DirectSessions` (`DirectSessionDbo`)
    - if there exists a `DirectSessionDbo` row for `(SelfIdentityId, RemotePeerId)`, status is `Direct`
    - else status is `Relay`
  - Note: current UI snapshot does not populate a relay host for established sessions (`RelayHostPeerId` is always null). Keep this null unless a reliable relay-host source is introduced.
- Display name source:
  - `PercolatorDbContext.PeerIdentities` (`PeerIdentityDbo`)
    - left join on `RemotePeerId == PeerId` for established sessions
    - `DisplayName = PeerIdentities.Name ?? RemotePeerId.ToString()[..8]`
    - `Initials` computed from `DisplayName`

- Pending outbound source:
  - `PercolatorDbContext.SentInvitations` (`SentInvitationDbo`)
    - keys: `(SelfIdentityId, RequestCorrelationId)`
    - filter: `ExpiresAtUtc > nowUtc`
      - SQLite translation note (verified): use the same pattern as `SqliteSentInvitationRepository`:
        - materialize candidate rows first (`ToListAsync`), then filter in-memory on `ExpiresAtUtc`.
    - identity: `SelfIdentityId == selfIdentityId`
    - projection:
      - `KeyType=PendingCorrelation`, `KeyValue = Guid.Parse(RequestCorrelationId)` (persistence stores correlation as string)
      - `PeerId = TargetPeerId` (nullable)
      - `DisplayName` derivation:
        - prefer `TargetDisplayName`
        - else if `TargetPeerId` exists, left join `PeerIdentities` to get `Name`
        - else if endpoint exists, use `"{TargetEndpointHost}:{TargetEndpointPort}"`
        - else fallback to correlation prefix
      - `Status = PendingOutbound`
      - `RelayHostPeerId = InviteRelayHostPeerId`
      - `LastActivityUtc = CreatedAtUtc`
    - pending disappears when:
      - expired, or
      - `TargetPeerId` is non-null and an established `SessionDbo` exists for the same `RemotePeerId`.

Tie-break rule (explicit):
- If both an established secure session row and a pending-outbound row exist for the same remote peer, the sidebar returns **only** the established secure session row.

**Invariant enforced by construction:** outbound pending rows come from `SentInvitations`, so remote-initiated pending requests never appear in the sidebar.

Future evolution for multiple active identities:
- When the app supports multiple active self identities simultaneously, the coordinator can call the same query methods once per `selfIdentityId` and merge the results.
- If needed to disambiguate rows across identities, introduce a composite UI identity key (e.g., `(selfIdentityId, PeerConnectionKey)`) at the UI model layer.

#### A.3: Implementation sub-chunks (big-bang)

##### A.3.1: Add Application-side DTOs + query contract

**Outcome:** Application exposes a stable contract for “what belongs in the sidebar”, with no WPF types.

**Add files:**
- `Percolator.Application/Sessions/IPeerConnectionSidebarQueries.cs`
- `Percolator.Application/Sessions/SidebarPeerConnectionDto.cs`
- `Percolator.Application/Sessions/SidebarPeerConnectionStatus.cs`
- `Percolator.Application/Sessions/SidebarPeerConnectionKeyType.cs`

**Contract rules:**
- The DTO must be flat.
- Keys are `KeyType + KeyValue`.
- Status includes `PendingOutbound`.

##### A.3.2: Add Infrastructure query implementation (DbContext projection)

**Outcome:** Infrastructure returns the unified sidebar projection.

**Add file:**
- `Percolator.Infrastructure/Sessions/PeerConnectionSidebarQueries.cs`

**Implementation rules:**
- Query `Sessions`, `DirectSessions`, `PeerIdentities`, `SentInvitations` via `PercolatorDbContext`.
- Apply `IgnoreQueryFilters()` and filter `SelfIdentityId == selfIdentityId` explicitly.
- Do not use `DateTimeOffset.UtcNow`. Inject `Percolator.Cryptography.IClock` and use `clock.UtcNow`.
- Expiration filtering (SQLite limitation):
  - materialize `SentInvitations` candidates (scoped to `SelfIdentityId`) with `ToListAsync`, then filter `ExpiresAtUtc > nowUtc` in-memory.
- Return:
  - established secure sessions (`KeyType=SecureSession`, `KeyValue=SessionId`, status direct/relay)
  - outbound pending (`KeyType=PendingCorrelation`, `KeyValue=RequestCorrelationId`, status pending outbound)
- Exclude inbound pending by never reading from `PendingSessions` in this query.

##### A.3.3: Big-bang WPF model: key-based identity everywhere

**Outcome:** WPF state and UI no longer assume “sidebar id == secure session id”. Sidebar identity is `PeerConnectionKey`.

**Change files:**
- `Desktop.Wpf/Features/Sessions/Queries/PeerConnectionStateSnapshot.cs`
- `Desktop.Wpf/Features/Sessions/Models/PeerConnectionModel.cs`
- `Desktop.Wpf/Features/Sessions/PeerConnectionStateService.cs`
- `Desktop.Wpf/Features/Sessions/PeerConnectionStatus.cs`

**Rules:**
- Remove `ConnectionId` from the snapshot/model layer.
- `PeerConnectionKey Key` is the identity.
- `Guid? PeerId` is nullable.
- Add `PeerConnectionStatus.PendingOutbound`.
- `PeerConnectionStateService.UpdateConnections(...)` must diff/update using `PeerConnectionKey`.

##### A.3.4: Sidebar ListBox selection uses typed key strings

**Outcome:** Selecting any sidebar row round-trips `PeerConnectionKey` via the string `Id`.

**Change files:**
- `Desktop.Wpf/Features/Sessions/PeerConnectionListItemViewModel.cs`
- `Desktop.Wpf/Features/Sessions/SessionsSidebarViewModel.cs`

**Rules:**
- `PeerConnectionListItemViewModel.Id = model.Key.ToString()`.
- `SessionsSidebarViewModel.TryParseKey(...)` parses only canonical `"{Type}:{guid}"`.
- Shared selection updates the ListBox selection using `key.Value.ToString()`.

##### A.3.5: Selected pane supports outbound-pending selection

**Outcome:** Selecting `PendingCorrelation:{guid}` shows a pending UI state and does not create chat content.

**Change file:**
- `Desktop.Wpf/Features/Sessions/SelectedChannelPaneViewModel.cs`

**Rules:**
- Resolve the selected model by `c.Key == selectedKey`.
- For `PendingCorrelation`:
  - show pending banner
  - disable input
  - `ActiveContent = null`

Verified concrete UI behavior to implement:
- `SelectedChannelPaneViewModel` already maps `SelectedPaneState.Pending` to `BannerText = "Establishing…"`.
- `IsInputEnabled` is already `true` only for `SelectedPaneState.Active`.

##### A.3.6: Reload pipeline uses the new Application query

**Outcome:** Sidebar reload returns both established sessions and outbound pending attempts.

**Change files:**
- `Desktop.Wpf/Features/Sessions/PeerConnectionReloadCoordinator.cs`
- `Desktop.Wpf/Features/Sessions/PeerConnectionStateService.cs` (if it wires queries)
- DI wiring:
  - `Desktop.Wpf/App.xaml.cs` currently registers `IPeerConnectionQueries` and must be updated.

**Rules:**
- Replace `IPeerConnectionQueries` usage with `IPeerConnectionSidebarQueries`.
- Reload does:
  - `LoadSidebarConnectionsAsync(selfIdentityId)`
  - maps DTO → `PeerConnectionStateSnapshot`
  - `_state.UpdateConnections(...)`
- Inbound pending remains separate and is never added to sidebar state.

Verified DI wiring locations (to avoid ambiguity):
- `Desktop.Wpf/App.xaml.cs` currently registers:
  - `services.AddScoped<Desktop.Wpf.Features.Sessions.Queries.IPeerConnectionQueries, Desktop.Wpf.Features.Sessions.Queries.PeerConnectionQueries>();`
  - Update this to register `Percolator.Application.Sessions.IPeerConnectionSidebarQueries` to the Infrastructure implementation.
- `PeerConnectionReloadCoordinator` and `PeerConnectionStateService.InitializeAsync` currently resolve `IPeerConnectionQueries` from a scope; both must be updated to resolve the new app-layer interface.

##### A.3.7: Tests

**Outcome:** Tests assert the feature behavior and the key-based selection format.

**Add/adjust tests:**
- Infrastructure projection tests for `PeerConnectionSidebarQueries`:
  - established sessions projected
  - pending outbound projected
  - expired invitations excluded
  - outbound pending suppressed when session exists for `TargetPeerId`
- WPF tests:
  - `SessionsSidebarViewModel.TryParseKey` parses canonical keys
  - selecting `PendingCorrelation` yields pending pane state

### Done Criteria

- When main initiates a handshake, the Sessions sidebar shows a new row immediately with status “pending/outbound”.
- Inbound pending requests still only show in `PendingHandshakesMenuViewModel`.
- Selecting the pending outbound row shows a Pending banner and does not enable chat input.