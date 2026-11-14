# Shell Window — Windowing, Composition & Storage Tickets

Sources: `shell-flow.md` §"Responsive Design Principles" + the session-
composition and plaintext-storage reviews. Governing specs: `spec/View.spec.md`
([VIEW-003], [VIEW-008]), `spec/CodeBehind.spec.md` ([CB-001]–[CB-009]),
`spec/ViewModel.spec.md` ([VM-004], [VM-006], [VM-009]), `spec/Design.md`
([INV-DESKTOP-TWO-STAGE-HANDOFF], [INV-STAGE2-HOST-ISOLATION],
[INV-ADAPTER-HYDRATION-BOUNDARY], [INV-VERTICAL-PERSISTENCE-SLICES],
[INV-DESKTOP-STEALTH-ACTIVATION-GATE], [DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE],
[DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).

Verified baseline (this plan reflects the code as it exists):

- `MainWindow.axaml` already declares `MinWidth="800" MinHeight="600"`,
  `Width="1200" Height="800"`, `WindowStartupLocation="CenterScreen"`,
  `InspectorSplitView` (`OpenPaneLength` = `InspectorPaneWidth` = 300) and
  `ChatsSidebarSplitView` (`OpenPaneLength` = `RightSidebarWidth` = 240), both
  `DisplayMode="Inline"` with two-way `IsPaneOpen` bindings.
- `MainWindow.axaml.cs.ApplyResponsiveLayout` already flips both `DisplayMode`s
  and force-closes the chats pane below 850px — WIN-01 is a behavioral fix to
  existing code, not a from-scratch feature.
- `SqliteDatabaseMigrator.MigrateAsync` runs `GenerateCreateScript()` rewritten
  to `CREATE ... IF NOT EXISTS` when `GetMigrations()` is empty (there is no
  `Migrations/` folder today) — new tables need **no EF migration tooling**.
- `PersonaDirectoryService.ShredAndDeleteAsync` already deletes the persona's
  settings row through the facade; `IExchangeDirectory` has **no exchange-
  delete operation** (`UnlinkPersonaAsync` removes a mailbox binding — the
  relay registration persists for re-linking).

## Implementation order

Tickets are listed in dependency order — the document IS the plan of record.
Phase 0 items are mutually independent and may run in parallel; every later
phase gates on the previous one. IDs were renumbered to match this order
(former IDs in parentheses).

| # | Ticket | Depends on | Notes |
|---|--------|------------|-------|
| 1 | CTX-01 (was CTX-03) | — | non-nullable transitions; shrinks the coordinator diff |
| 2 | STOR-01 | — | Infra-only; idempotent DDL path adds the tables |
| 3 | STOR-02 | STOR-01 | new port + store; zero Desktop changes |
| 4 | WIN-01 (merged WIN-02/03) | — | code-behind fix to existing breakpoints |
| 5 | CTX-02 (was CTX-01) | CTX-01 | the composition surgery — **compile gate** |
| 6 | CTX-03 (was CTX-02) | CTX-02 | facades + session VMs move together — **compile gate** |
| 7 | STOR-03 | STOR-02 + CTX-03 | facade impl swap |
| 8 | STOR-04 | STOR-03 | delete-path parity test + exchange-row decision |
| 9 | WIN-02 (merged WIN-01/04) | STOR-02 + CTX-03 + CTX-01 | geometry store is Stage-2 |
| 10 | STOR-05 | STOR-03 | retire + grep sweep — always last |

---

# Phase 0 — Foundation (parallel-safe)

## CTX-01 — Non-nullable session transitions + Stage-1 ctor injection

**Was:** CTX-03. **Depends on:** nothing — landing it first shrinks the
`SqlCipherDatabaseUnlockCoordinator` diff before CTX-02 rewrites the same file.
**QMS:** [INV-DESKTOP-STEALTH-ACTIVATION-GATE] (the decoy vault is a real
session), [VM-004] (ViewModels/facades ctor-inject, no locator).

### Summary

Every session transition requires a session — nullable
`IAuthenticatedSession?` params become required, and the session-less decoy
result dies: duress activation returns a real decoy-vault session or the
unlock throws.

### Implementation

- `UnlockResult.DuressActivated` → `public sealed record DuressActivated(
  IAuthenticatedSession Session)` — non-nullable.
  `SqlCipherDatabaseUnlockCoordinator`'s decoy-init failure path
  (`return new UnlockResult.DuressActivated();`) propagates the exception
  instead; `VaultUnlockViewModel` catches → generic unlock-failure surface
  (not `InvalidCredentials`). Update `UnlockResultTests` and
  `FakeDatabaseUnlockCoordinator` to pass a `FakeAuthenticatedSession`
  (all three in `Percolator.Application.Tests`).
- `IWindowHandoffCoordinator`:
  `TransitionToGateAsync(IAuthenticatedSession session, CancellationToken ct)`
  and `TransitionToMainShellAsync(IAuthenticatedSession session,
  IReadOnlyList<PersonaLaunchDescriptor>?, IReadOnlyList<ExchangeLaunchDescriptor>?,
  CancellationToken)` — session param non-nullable, no overloads. The
  `session is not null → session.StartAsync` guard in
  `TransitionToMainShellAsync` becomes unconditional `await session.StartAsync(ct)`.
  Same non-nullable treatment for `StartupHostViewModel.NavigateToGateAsync`
  and `IdentityGateViewModel.LoadAsync`. `PendingSession` stays — it is the
  Stage-1 parking slot between unlock and launch, not a nullable-contract leak.
- `IdentityGateViewModel`: `_session` is always set —
  `IsExchangeCreateEnabled` drops the `_session is not null` conjunct; the
  `_session is null` guard in `CreateExchangeAsync` deletes; `LoadAsync`'s
  early-return on null session deletes.
- `WindowHandoffCoordinator` ctor: replace the
  `GetRequiredService<StartupHostViewModel>` call in `LockSessionAsync` with
  ctor-injected `Func<StartupHostViewModel>` (matches the existing `Func<VM>`
  registration pattern in `App.axaml.cs` — add the missing
  `Func<StartupHostViewModel>` registration line). The ctor keeps its
  `IServiceProvider` — it is the composition-seam provider used to construct
  post-unlock UI roots (`MainWindowViewModel` today, moved to
  `session.Services` in CTX-03).
- `WindowManager` ctor is unchanged (`IServiceProvider`) — CTX-03 registers
  a second, session-bound `IWindowManager` instance rather than adding a
  `provider` parameter to its methods.

### Acceptance

- No `IAuthenticatedSession?` parameters on `IWindowHandoffCoordinator`,
  `StartupHostViewModel.NavigateToGateAsync`, or
  `IdentityGateViewModel.LoadAsync`; decoy-init failure surfaces as a
  generic unlock failure; `Func<StartupHostViewModel>` registered in
  `App.axaml.cs`.

---

## Plaintext Settings Store Elimination (preamble for STOR-*)

Source: privacy review of `SqliteDesktopSettingsRepository` — the plaintext
sidecar database stores `IdentitySettings` rows keyed by persona/relay
`PublicIdentityId` GUIDs, enumerating vault contents at rest and surviving
duress crypto-erasure. Nothing reads it pre-unlock — every consumer already
holds an `IAuthenticatedSession`.

Decision: **rip it out and replace.** The plaintext store is deleted outright —
no data migration, no cleanup code; stale files on existing installs are
removed out of band. The legacy file name appears exactly once in this plan:
the STOR-05 grep-sweep instruction.

QMS: [INV-STAGE2-HOST-ISOLATION], [INV-ADAPTER-HYDRATION-BOUNDARY],
[INV-VERTICAL-PERSISTENCE-SLICES] (`spec/Design.md`), [CB-002].

Architectural rules for this work (strict hexagonal):
- The vault-side **port owns its contract types** in the same ring that declares
  it — a new `IdentityLaunchSettings` record in `Percolator.Application`,
  keyed on the domain `PublicIdentityId` (never a raw `Guid`/`string` on the
  port surface). `DesktopIdentitySettingsEntity` is deleted, not moved.
- Infra entities follow the `public sealed class *Entity` convention in
  `Persistence\Entities` (`OpsReservedPortEntity` precedent — public because
  `DbSet<T>` on a public DbContext requires it).
- `PublicIdentityId` persists as a 16-byte `BLOB` (`FromBytes`/`ToBytes`),
  matching the relay/discovery identity columns — not TEXT guid strings.
- Session access: Stage-2-registered Desktop services ctor-inject ports;
  `session.Services.GetRequiredService` appears only at composition seams
  (coordinator/window construction of post-unlock UI roots) — never inside a
  service method.
- App-setting keys become `VaultSettingKeys` constants owned by Application —
  no magic strings scattered across ViewModels.
- Ports stay `int` (matches `OpsReservedPortEntity.PortNumber`, `RelayEndpoint`,
  all launch descriptors) — deliberate convention consistency, not a VO refactor;
  range-validate at the port boundary.

## STOR-01 — Vault entities for identity settings

**Depends on:** nothing — pure Infra change inside today's Stage-2 container.
**QMS:** [INV-VERTICAL-PERSISTENCE-SLICES] (entity mapping co-located in the
persistence slice), [INV-ADAPTER-HYDRATION-BOUNDARY].

### Summary

Both tables move into the encrypted `percolator.db` as EF entities on
`PercolatorDbContext`. **No EF migration is required:**
`SqlCipherSchemaMigrator` (bootstrap) → `SqliteDatabaseMigrator.MigrateAsync`
runs during unlock before any repository resolves, and its no-migrations
branch rewrites `GenerateCreateScript()` to `CREATE ... IF NOT EXISTS` —
adding entities to the model creates the new tables on next unlock
automatically. EF-migrations tooling (`Migrations/` folder,
`IDesignTimeDbContextFactory`, EF Design package, `InitialSchema` baseline)
is a separate future-work item in `implementation-plan.md` — needed for
ALTERs, not for table creation.

### Implementation

- New `public sealed` entities in
  `Percolator.Infrastructure/Persistence/Entities/`:

  ```csharp
  public sealed class IdentitySettingsEntity
  {
      public byte[] IdentityId { get; set; } = Array.Empty<byte>(); // PK — PublicIdentityId bytes
      public int PreferredPort { get; set; }
      public int? LastBoundPort { get; set; }
      public bool AutoStart { get; set; }
      public int SidebarOrder { get; set; }
  }

  public sealed class AppSettingEntity
  {
      public string SettingKey { get; set; } = string.Empty;   // PK
      public string SettingValue { get; set; } = string.Empty;
  }
  ```

- `PercolatorDbContext`: `DbSet`s + `OnModelCreating` entries —
  `ToTable("identity_settings")` / `ToTable("app_settings")`,
  `HasKey(e => e.IdentityId)` / `HasKey(e => e.SettingKey)`,
  `IdentityId` → `HasColumnType("BLOB")`. Column names need no mapping — the
  context's existing `ToSnakeCase` convention renames all properties.
  No extra indexes — `GetAllAsync` scans `ORDER BY SidebarOrder`.
- No backfill — legacy plaintext data is discarded per the rip-out decision.

### Acceptance

- Unlock applies the new tables cleanly on a pre-existing vault
  (`SqliteDatabaseMigratorTests` fixture pattern: DDL path is idempotent).
- Fresh vault: `sqlite_master` shows `identity_settings` + `app_settings`.


---

## STOR-02 — Application-owned `IVaultSettingsStore` port + EF realization

**Depends on:** STOR-01.
**QMS:** [INV-VERTICAL-PERSISTENCE-SLICES] (write repository in the slice),
[INV-STAGE2-HOST-ISOLATION] (repository exists only in the Stage-2 container),
[INV-ADAPTER-HYDRATION-BOUNDARY].

### Summary

New driven port `IVaultSettingsStore` in `Percolator.Application` (named for
the capability — vault-scoped settings — not the Desktop consumer), realized by
`SqliteVaultSettingsStore` as a **session singleton over
`IDbContextFactory<PercolatorDbContext>`**, consumed by ctor injection from
any Stage-2-registered service. The port owns its contract types:
`IdentityLaunchSettings` record + `VaultSettingKeys` constants live beside it
in the same ring.

### Implementation

- `Percolator.Application/Ports/IVaultSettingsStore.cs` (match sibling port
  namespace conventions):

  ```csharp
  Task<IdentityLaunchSettings?> GetAsync(PublicIdentityId identityId, CancellationToken ct = default);
  Task<IReadOnlyList<IdentityLaunchSettings>> GetAllAsync(CancellationToken ct = default);
  Task SaveAsync(IdentityLaunchSettings settings, CancellationToken ct = default);
  Task DeleteAsync(PublicIdentityId identityId, CancellationToken ct = default);
  Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default);
  Task SetAppSettingAsync(string key, string value, CancellationToken ct = default);
  ```

- Contract types beside the port (Application layer):

  ```csharp
  public sealed record IdentityLaunchSettings(
      PublicIdentityId IdentityId,   // domain type — covers personas AND relay hosts
      int PreferredPort,
      int? LastBoundPort,
      bool AutoStart,
      int SidebarOrder);

  public static class VaultSettingKeys
  {
      public const string GateOnboardingCompleted = "Gate.OnboardingCompleted";
      public const string ShellWindowGeometry = "Shell.WindowGeometry";
  }
  ```

- `SqliteVaultSettingsStore` (**singleton**, ctor
  `IDbContextFactory<PercolatorDbContext>` — `SqliteRelayPreKeyRepository` /
  `SqliteRelayMailboxCapabilityStore` precedent): each method does
  `await using var db = _dbFactory.CreateDbContextAsync(ct)`; `AsNoTracking`
  reads, `PublicIdentityId.ToBytes()` ↔ `FromBytes` at the entity boundary,
  query→update-or-add upsert (same `ON CONFLICT` semantics as today),
  `ExecuteDeleteAsync` for `DeleteAsync`. `PreferredPort` range-validated
  1–65535 on `SaveAsync` (boundary check, not a VO).
- Register `services.AddSingleton<IVaultSettingsStore, SqliteVaultSettingsStore>()`
  in `InfrastructureServiceCollectionExtensions` — inside today's
  `AddAuthenticatedInfrastructure` monolith; CTX-02 relocates the same line
  into `AddVaultPersistenceInfrastructure` unchanged.
- ISP note: `IdentityLaunchSettings` CRUD and app k/v stay on one port — both
  are vault-scoped preference storage owned by the same entity/table pair;
  splitting adds an interface + registration for zero behavioral gain.

### Edge cases

- `UseEncryption=false` dev vaults: same code path, DB is just unkeyed.
- `Guid`↔`PublicIdentityId` conversion lives at the *call-site boundary*
  (facade), never inside the store.

### Acceptance

- Unit tests against unencrypted `SqliteTestDb` fixture: full round-trip,
  `DeleteAsync` removes, k/v upsert idempotent, out-of-range port rejected.

---

## WIN-01 — Preserve sidebar state across the 850px collapse

**Merged:** former WIN-02 + WIN-03 — both breakpoints live in one
`ApplyResponsiveLayout` handler in `MainWindow.axaml.cs`, so they are one
change.
**Depends on:** nothing — pure code-behind, land anytime.
**Spec:** shell-flow.md ("< 1000px → overlay/drawer with backdrop dismiss
scrim"; "< 850px → Right Sidebar collapses into a slide-out drawer accessible
via a hamburger toggle").
**QMS:** [VIEW-003] (native `SplitView`, no hand-rolled drawers), [CB-003],
[CB-008] (DisplayMode flips live in code-behind size handlers).

### Summary

`ApplyResponsiveLayout` already flips both `DisplayMode`s (`Loaded` +
`SizeChanged` handlers — [CB-008]-compliant, no new subscription needed) and
auto-closes the chats pane on the wide→narrow edge. The defect: the
narrow→wide edge writes `ChatsSidebarSplitView.IsPaneOpen = !narrow`
unconditionally — it force-*re-opens* the pane even when the user explicitly
closed it while narrow. Remaining work: preserve the pre-collapse pane state,
hoist literals to named constants, and verify the pane-width/flicker edge
cases.

### Implementation

- Hoist `1000`/`850` to class constants (`InspectorDrawerBreakpointDip`,
  `SidebarDrawerBreakpointDip`).
- Track the pre-collapse state in a `bool _sidebarOpenBeforeCollapse` field:
  on the Inline→Overlay edge save `ChatsSidebarSplitView.IsPaneOpen` (the
  two-way binding already reflects the VM's `IsChatsSidebarOpen`) and set it
  `false`; on the Overlay→Inline edge restore the saved value instead of
  `true`. The `IsPaneOpen` write propagates to `IsChatsSidebarOpen` through
  the two-way binding — no VM property needs touching.
- Inspector: unchanged — its open state already crosses both transition
  edges untouched (scrim-dismiss writes back through the binding).
- Verify at runtime: `InspectorPaneWidth` = 300 and `RightSidebarWidth` = 240
  are both < the 800px min width (already sane); `DisplayMode` flips cannot
  change window width (no flicker loop — add 20px hysteresis only if a
  boundary oscillation is actually observed).

### Edge cases

- Exactly 1000px → Inline; exactly 850px → Inline (thresholds are `<`, not `≤`).
- Both breakpoints crossed at once (1100→800): both flips happen in one
  handler pass; the sidebar still auto-closes.
- Explicit hamburger close while narrow, then widen: pane stays closed
  (user intent wins); reopen with the hamburger.
- Both overlays open simultaneously in compact mode — they stack; light-
  dismiss resolves them independently.

### Acceptance

- Drag-resize across 1000px: inspector changes push→float live; scrim click
  in compact closes it AND `IsInspectorOpen` is false.
- Cross 850px downward: sidebar becomes a drawer AND closes.
- Hamburger-close while narrow → widen → pane stays closed; hamburger-open
  while narrow → widen → pane re-inlines open.

---

# Phase 1 — Session Composition & Context Refactor (compile gates)

Context: every session-facing facade hand-rolls
`session.Services.GetRequiredService<IServiceScopeFactory>().CreateScope()` +
`GetRequiredService<TPort>()` — service-locator plumbing duplicated across
`ExchangeDirectoryService`/`PersonaDirectoryService` (~13 call sites), and decoy
transitions use a nullable `IAuthenticatedSession?` param to mean "no context."

Root cause: **composition ownership is inverted.** The Stage-2 container is
built inside Infrastructure (`SqlCipherDatabaseUnlockCoordinator` →
`IStage2HostFactory.BuildAuthenticatedHost` → `AddAuthenticatedInfrastructure`)
with `IStage2HostFactory` marked `internal` — so Desktop types can never be
registered in the session container and are forced to tunnel through
`session.Services` per call.

Design ruling (senior review): **executables build their own containers; Infra
provides `IServiceCollection` helpers only.** Desktop composes the session host
from fine-grained Infra helpers plus its own `AddDesktopSessionServices`, so
post-unlock services/VMs get real ctor injection — the session boundary is
expressed as *which container resolved you*, not a threaded parameter. Scoped
DbContext lifetimes exist for inbound message pipelines — not UI call paths;
`IDbContextFactory` is already registered and `SqliteRelayMailboxCapabilityStore`/
`SqliteRelayPreKeyRepository` prove the factory pattern. `ValidateScopes = true`
on the session host (`Stage2HostFactory` already sets it) makes "no scoped deps
resolved from the root graph" mechanically enforced.

Decisions (approved): keep the `IAuthenticatedSession` name; **no
`IStage1Context`**; **no null-session paths — the decoy vault is a real
session or the unlock throws**; **no `ISessionPorts` registry — the
container is the registry**; a port used by both UI and handler pipelines
gets usage-appropriate lifetimes — two usages may mean two
registrations/interfaces, not one forced compromise.

## CTX-02 — Executable-owned session composition + port lifetime audit

**Was:** CTX-01. **Depends on:** CTX-01 recommended — it already made
`DuressActivated.Session` non-nullable, so this ticket's coordinator rewrite
is pure composition plumbing.
**QMS:** [INV-STAGE2-HOST-ISOLATION], [INV-ADAPTER-HYDRATION-BOUNDARY].

### Summary

Promote host composition to the executable. `IStage2HostFactory` (internal,
Infra) becomes a **public port** consumed by `IDatabaseUnlockCoordinator`;
Desktop registers the implementation that builds the session host from
fine-grained Infra helpers plus its own session registrations.

### Implementation

- New port in `Percolator.Application.Ports` (beside `IAuthenticatedSession`):

  ```csharp
  public interface ISessionHostFactory
  {
      IAuthenticatedSession CreateSession(IMasterKeyHandle masterKey);
  }
  ```

  Returns the session directly — kills today's double-construct where
  `Stage2HostFactory` news up one `AuthenticatedSession` for the holder and the
  coordinator news up a second at the call site.

- Decompose `AddAuthenticatedInfrastructure` (~190 lines) into slices.
  Signatures pinned: only the core slice takes parameters —
  `AddSessionCoreInfrastructure(this IServiceCollection, IMasterKeyHandle,
  VaultStorageOptions)`; the rest are parameterless and read the registered
  `VaultStorageOptions`/`IMasterKeyHandle` singletons via `sp` (the
  `AddDbContextFactory` lambda currently captures `options` — switch it to
  `sp.GetRequiredService<VaultStorageOptions>()`):
  - `AddSessionCoreInfrastructure` — `IMasterKeyHandle`, `VaultStorageOptions`,
    `DbConnectionInterceptor`, event bus, crypto/serialization/platform
    singletons.
  - `AddVaultPersistenceInfrastructure` — `AddDbContextFactory` + scoped
    `PercolatorDbContext`, scoped `IDatabaseMigrator`, repositories & query
    services, plus the repository-maintenance workers
    (`OutboxRetentionPruner`, `RelayPreKeyDirectoryPruner`,
    `RelayProfileSlotPruner`).
  - `AddTransportInfrastructure` — transport singletons, ingress, dispatcher,
    endpoints, `RelayMailboxStreamWorker`/`RelayGroupStreamWorker` (+ their
    options singletons).
  - `AddSessionLifecycleInfrastructure` — `IHeaderStorage`,
    `IDuressWipeHandler`, `IVaultEnvelopePacker`, `IDatabaseCanaryProbe`.
    **Delete, do not migrate** (all verified dead in-session):
    `AuthenticatedSessionHolder`, the `IAuthenticatedSession` holder-binding,
    `IStage2HostFactory → Stage2HostFactory`, and the scoped
    `IDatabaseUnlockCoordinator` — resolving the coordinator in-session would
    recurse through `Stage2HostFactory` into `AddAuthenticatedInfrastructure`;
    nothing in the session graph resolves any of them.
  - `AddChatInfrastructure`/`AddDiscoveryInfrastructure`/
    `AddBlobStorageInfrastructure`/`AddFileTransferInfrastructure` — the
    helpers exist but are **incomplete subsets**: diff each against the
    monolith section and move the missing lines in (verified gaps:
    `IBlobStorageService` missing from the blob helper; `IProfileManager`
    missing from the discovery helper). Each already carries its own hosted
    services — keep them there.
  - Keep `AddAuthenticatedInfrastructure` as a thin aggregator calling every
    slice — test/simulator hosts keep a one-line composition.
- `DesktopStage2SessionFactory : ISessionHostFactory` in `Percolator.Desktop`
  (the composition root owns it):

  ```csharp
  var builder = Host.CreateDefaultBuilder();
  builder.UseDefaultServiceProvider((_, o) =>          // Stage2HostFactory precedent —
  {                                                  // keep the same strictness
      o.ValidateScopes = true;
      o.ValidateOnBuild = true;
  });
  builder.ConfigureServices((_, s) =>
  {
      s.AddSessionCoreInfrastructure(key, options);
      s.AddVaultPersistenceInfrastructure();
      s.AddTransportInfrastructure();
      s.AddSessionLifecycleInfrastructure();
      s.AddChatInfrastructure();
      s.AddDiscoveryInfrastructure();
      s.AddBlobStorageInfrastructure();
      s.AddFileTransferInfrastructure();
      s.AddDesktopSessionServices(_stage1);     // CTX-03 fills this
  });
  var host = builder.Build();
  return new AuthenticatedSession(host);   // holder deleted — nothing binds
  ```

  `DesktopStage2SessionFactory` ctor-injects the **Stage-1 root provider**
  (`IServiceProvider stage1`) and the Stage-1 `VaultStorageOptions` singleton
  (already registered by `AddBootstrapInfrastructure`) —
  `AddDesktopSessionServices` uses the provider to forward live Stage-1
  singletons into the session container (CTX-03). `App.axaml.cs` registers it
  as `services.AddSingleton<ISessionHostFactory, DesktopStage2SessionFactory>()`.

- `SqlCipherDatabaseUnlockCoordinator`: `_hostFactory` param type →
  `ISessionHostFactory`; `BuildAuthenticatedHost` calls → `CreateSession`;
  delete the second `new AuthenticatedSession(host)` — init, unlock, and
  duress-decoy paths all simplify identically.
- `AddBootstrapInfrastructure`: delete the `IStage2HostFactory →
  DefaultStage2HostFactory` line **and** change the coordinator's manual
  factory arg `sp.GetRequiredService<IStage2HostFactory>()` →
  `sp.GetRequiredService<ISessionHostFactory>()` — any host resolving
  `IDatabaseUnlockCoordinator` without registering one fails fast at
  resolution (intended). Infra's `IStage2HostFactory` interface deletes;
  `Stage2HostFactory`/`DefaultStage2HostFactory` classes shrink to test
  helpers or delete — `BaselineStage2HostFactory` already proves
  executable-owned composition is the test pattern.
- **Port lifetime audit** — any port ctor-injected into a session-long-lived
  object must resolve from the root provider (`ValidateScopes = true` turns
  violations into startup exceptions):
  - `IRelayMailboxCapabilityStore`, `IPeerTransportServer` — already
    singletons. No change.
  - **Migrate to singleton (verified — each ctor-injects `PercolatorDbContext`
    today; swap to `IDbContextFactory<PercolatorDbContext>` +
    `await using var db = _dbFactory.CreateDbContextAsync(ct)` per method,
    `SqliteRelayPreKeyRepository`/`SqlitePortReservationService` precedent):**
    `SqliteIdentityProfileRepository` (`IIdentityProfileRepository`),
    `SqliteLocalIdentityKeyStore` (`ILocalIdentityKeyStore`),
    `SqlitePrivatePreKeyStore` (`IPrivatePreKeyStore`),
    `SqliteRelayRegistrationRepository` (`IRelayRegistrationRepository`) —
    all consumed by the session-bound facades. `RelayDirectoryService`
    (`IRelayDirectory`) goes singleton with them — its other deps
    (`ICryptoEngine`, `IDateTimeProvider`) are already session singletons.
    `GrpcRelayMailboxProvisioner` (`IRelayMailboxProvisioner`) goes
    singleton too — swap its `IServiceProvider` dep for
    `IServiceScopeFactory`, one scope per drain (its
    `ISealedEnvelopeUnwrapper`/`IInboundIngressPipeline` resolves are
    per-inbound-message scoped). Note: the drain path is dormant in Desktop
    today — `RelayMailboxStreamWorker`'s `Func<CancellationToken, Task>`
    drain delegate is unregistered (defaults to a no-op delay) and
    `ISealedEnvelopeUnwrapper` is per-node by design (see the comment at its
    registration site) — wiring drain is a separate feature ticket.
  - **Stay scoped (handler-only):** every other `AddScoped` — ratchet/group/
    outbox/peer/channel repositories, `SqliteRelayProfileRepository`/
    `SqliteRelayLedgerRepository` (relay-host inbound path), query services,
    ingress filter, dispatcher, endpoints, chat/discovery/blob/file-transfer
    repositories — resolved only inside handler/worker scopes, never
    ctor-injected into session objects. The ambient scoped
    `PercolatorDbContext` registration stays for them.
  - `IPortReservationService` is **not** a cross-container collision — two
    interfaces share the name: `Percolator.Application.Ports` (persistent
    lease API → `SqlitePortReservationService`, Stage-2) vs
    `Percolator.Desktop.Common.Services` (transient socket probe →
    `PortReservationService`, Stage-1). Both stay; the Desktop prober is one
    of the Stage-1 imports CTX-03 forwards into the session container.
    Optional follow-up: rename one to kill the `using`-alias tax (tests
    already alias it).
- Multi-port atomicity note: factory-per-method means composite writes commit
  sequentially — true parity with today: **zero `BeginTransaction` calls
  exist in the codebase**, so the shared ambient context only ever provided
  shared change-tracking, never cross-repo atomicity. True multi-row
  transactions remain an explicit handler-scope concern.

### Acceptance

- Unlock/init/duress paths all return a session built by
  `DesktopStage2SessionFactory`; `session.Services` resolves every audited
  port from root without scope-validation exceptions;
  `BaselineStage2HostFactoryTests` and the SqlCipher unlock tests pass with
  factories updated to the new port.

---

## CTX-03 — `AddDesktopSessionServices` + real ctor injection

**Was:** CTX-02. **Depends on:** CTX-02 — same-PR compile gate: facades and
session VMs move containers together or nothing builds.

### Summary

Desktop's session-facing services and ViewModels register in the session
container via `AddDesktopSessionServices` — they ctor-inject ports directly
and lose every `session` parameter and every `CreateScope`/`GetRequiredService`
call. `session.Services` resolution survives only at composition seams (the
coordinator constructing post-unlock UI roots) — the legitimate service-
location sites.

### Implementation

- `Percolator.Desktop` composition file — `AddDesktopSessionServices(
  this IServiceCollection, IServiceProvider stage1)`.
  Session-resolved VMs keep Stage-1 deps, so the factory forwards **live
  instances** (not re-registrations) for the Stage-1 imports:

  ```csharp
  // session-bound Desktop services + VMs
  services.AddTransient<IExchangeDirectory, ExchangeDirectoryService>();
  services.AddTransient<IPersonaDirectory, PersonaDirectoryService>();
  services.AddSingleton<IDesktopSettingsRepository, VaultDesktopSettingsRepository>(); // STOR-03
  services.AddTransient<IdentityGateViewModel>();
  services.AddTransient<MainWindowViewModel>();     // resolved at the WIN-02 seam
  services.AddTransient<ExchangeManageViewModel>();
  services.AddTransient<ExchangeCreationViewModel>();
  services.AddTransient<CreatePersonaDialogViewModel>();
  services.AddTransient<DeletePersonaDialogViewModel>();
  services.AddTransient<StopUsingExchangeDialogViewModel>();
  // services.AddSingleton<IShellWindowGeometryStore, ShellWindowGeometryStore>();  — WIN-02

  // session-bound window manager: window scopes come off the SESSION root —
  // no `provider` parameter needed on Show/ShowDialogAsync and zero call-site
  // changes (the Stage-1 singleton keeps its own instance untouched)
  services.AddSingleton<IWindowManager>(sp =>
      new WindowManager(sp, sp.GetRequiredService<ILogger<WindowManager>>()));

  // Stage-1 live-instance imports (verified — the only Stage-1 deps any
  // session-bound VM ctor-injects)
  services.AddSingleton(_ => stage1.GetRequiredService<IWindowHandoffCoordinator>());
  services.AddSingleton(_ => stage1.GetRequiredService<IIdenticonGenerator>());
  services.AddSingleton(_ => stage1.GetRequiredService<Common.Services.IPortReservationService>());
  ```

  `ILogger<>` resolves free via `Host.CreateDefaultBuilder`; `ICryptoEngine`/
  `IDateTimeProvider` come from the session container's own core-slice
  registrations. `IDialogService`/`ILauncherService` need **no** import —
  their only consumers (`StartupHostViewModel`, `VaultSetupViewModel`) are
  Stage-1.

- `IExchangeDirectory`/`IPersonaDirectory`: **remove `IAuthenticatedSession`
  from every method** (8 + 4 signatures) — impls become ctor-DI'd transients
  (verified dep lists):
  `ExchangeDirectoryService(IRelayDirectory, IIdentityProfileRepository,
  IRelayMailboxCapabilityStore, IPeerTransportServer,
  IDesktopSettingsRepository)`;
  `PersonaDirectoryService(IIdentityProfileRepository,
  ILocalIdentityKeyStore, IPrivatePreKeyStore, ICryptoEngine,
  IDateTimeProvider, IDesktopSettingsRepository)`. Method bodies shrink to
  direct calls; both `CreateScope` helpers die.
- `IdentityGateViewModel` keeps a **single ctor** — its 8 deps span both
  containers (`IPersonaDirectory`, `IExchangeDirectory`,
  `IDesktopSettingsRepository` session-bound; `IWindowManager`,
  `IWindowHandoffCoordinator`, `IIdenticonGenerator`, Desktop
  `IPortReservationService` session/import; `ILogger<>` free). It still
  holds `_session` from `LoadAsync(session)` — needed to hand the session to
  `TransitionToMainShellAsync` (`IAuthenticatedSession` is not a DI service).
- `ExchangeManageViewModel`: `Initialize(_session, exchange, personas)` →
  `Initialize(exchange, personas)`; the `_session` field deletes — it only
  fed the facade's session params.
- `StartupHostViewModel`: `Func<IdentityGateViewModel>` dep + registration
  die. `NavigateToGateAsync(session)` does the seam resolution itself:
  `CurrentViewModel = session.Services.GetRequiredService<IdentityGateViewModel>()`
  then `LoadAsync(session)`; `NavigateToScreen` loses the `IdentityGate` arm.
- `VaultUnlockViewModel`: drops `IPersonaDirectory` from its ctor — the dep
  cannot exist pre-unlock. `TryAutoStartLaunchAsync` resolves
  `IPersonaDirectory` from `success.Session.Services` — the one legitimate
  later-than-ctor resolution in the plan. It keeps
  `IDatabaseUnlockCoordinator`, `IWindowHandoffCoordinator`, and the Desktop
  socket-probe `IPortReservationService`.
- `WindowHandoffCoordinator`: `TransitionToMainShellAsync` resolves
  `session.Services.GetRequiredService<MainWindowViewModel>()` (+ the
  geometry store in WIN-02) — with `LockSessionAsync` already on
  `Func<StartupHostViewModel>` (CTX-01), the ctor's `IServiceProvider` is
  now unused → **drop it**.
- Stage-1 deletes in `App.axaml.cs`: `IPersonaDirectory`, `IExchangeDirectory`,
  `IDesktopSettingsRepository` singletons; `Func<IdentityGateViewModel>`;
  and the transients for `IdentityGateViewModel`, `MainWindowViewModel`,
  and the five dialog VMs above. `Func<StartupHostViewModel>` (added in
  CTX-01) stays.
- Callers drop session arguments when calling facades — the bulk of the ~13
  call sites just lose a parameter. `IdentityGateViewModel`'s five
  `ShowDialogAsync` calls are untouched — the injected `IWindowManager` is
  the session-bound instance, so dialog scopes resolve `ExchangeManageViewModel`
  & co. from the session container.
- Tests: ctor-inject `Mock<IRelayDirectory>` etc. — first facade unit tests
  without `Mock<IAuthenticatedSession>` plumbing.

### Acceptance

- No `session` parameter on `IExchangeDirectory`/`IPersonaDirectory` or their
  impls; zero `GetRequiredService`/`CreateScope` inside either facade;
  `ValidateScopes`/`ValidateOnBuild` stay green; the 5 gate dialogs open and
  resolve their VMs from session scopes.

---

# Phase 2 — Session consumers

The plaintext-store rationale and hexagonal rules live in the Phase-0 STOR
preamble. These tickets rewire the Desktop consumers now that
`IVaultSettingsStore` exists and the session container can resolve them.

## STOR-03 — Session-aware `IDesktopSettingsRepository` facade + call-site rewiring

**Depends on:** STOR-02 + CTX-03 — the impl needs the port to exist and every
consumer must already resolve from the session container.
**QMS:** [CB-002] n/a (ViewModels unaffected — same injected interface).

### Summary

`IDesktopSettingsRepository` **keeps its parameterless signature** — the
session-param threading dies with executable-owned composition. The
implementation swaps to `VaultDesktopSettingsRepository`, a Stage-2 singleton
ctor-injecting `IVaultSettingsStore`, registered in `AddDesktopSessionServices`
(CTX-03). Every consumer resolves from the session container — the decoy vault
is a real session, so decoy personas/settings persist identically and die
with the decoy vault.

### Implementation

- `VaultDesktopSettingsRepository` (replaces `SqliteDesktopSettingsRepository`
  — delete that file; `Vault` prefix names *where* it stores, `Session` was
  mechanics-speak):

  ```csharp
  internal sealed class VaultDesktopSettingsRepository : IDesktopSettingsRepository
  {
      private readonly IVaultSettingsStore _store;   // ctor-injected — no session, no scope
  }
  ```
- Interface: method shapes unchanged (`GetAsync`, `GetAllAsync`, `SaveAsync`,
  `DeleteAsync`, `GetAppSettingAsync`, `SetAppSettingAsync` — no `session`
  parameter); the `Guid` parameters stay — the facade converts internally via
  `new PublicIdentityId(id)` / `.Value`.
- **Record migration:** `DesktopIdentitySettingsEntity` is deleted — the
  entity type in `Get*`/`SaveAsync` signatures swaps to
  `IdentityLaunchSettings` (its `IdentityId` is `PublicIdentityId`, not
  `Guid`). Resulting call-site deltas are simplifications:
  `new DesktopIdentitySettingsEntity(identityId.Value, …)` →
  `new IdentityLaunchSettings(identityId, …)`;
  `settings.ToDictionary(s => s.IdentityId)` now keys on `PublicIdentityId`,
  so `TryGetValue(profile.Id.Value)` lookups become `TryGetValue(profile.Id)`.
- `IdentityGateViewModel.OnboardingCompletedKey`/`FlagSetValue` constants move
  to `VaultSettingKeys`; the VM reads `VaultSettingKeys.GateOnboardingCompleted`.
- The change is *which object answers*: every consumer resolves from the
  session container and reaches vault storage — there is no session-less
  consumer left after CTX-03.
- `App.axaml.cs`: delete the Stage-1
  `AddSingleton<IDesktopSettingsRepository, SqliteDesktopSettingsRepository>`
  line — the registration moves into `AddDesktopSessionServices`.
- `VaultStorageOptions` ctor dependency is gone — the repo no longer knows a
  path.

### Acceptance

- `dotnet build` clean; gate + exchange flows behave identically.
- `ExchangeViewModelTests` / `IdentityGateViewModelTests` pass — the
  `Mock<IDesktopSettingsRepository>` setups are unchanged (no new parameter).

---

## STOR-04 — Settings-row lifecycle: parity test + exchange-row decision

**Depends on:** STOR-03 — the swap decides whether cleanup actually reaches
the vault.
**QMS:** none — no invariant covers row lifecycle; this ticket is the record.

### Summary

Research correction to the original premise: `PersonaDirectoryService.
ShredAndDeleteAsync` **already** calls `_settings.DeleteAsync(identityId.
Value, ct)` — post-STOR-03 that call routes through the swapped facade into
`IVaultSettingsStore.DeleteAsync` automatically, so persona settings rows
are already cleaned. And `IExchangeDirectory` has **no exchange-delete
operation**: `UnlinkPersonaAsync` removes a persona↔relay mailbox binding
while the relay registration (and its `identity_settings` row) persists —
intended, so re-linking keeps the exchange's preferred port.

### Implementation

- No new wiring. Add regression coverage: a `ShredAndDeleteAsync` test
  asserting the persona's settings row is gone afterward (assert through
  `IVaultSettingsStore.GetAsync` or the mock facade — pick one and keep it).
- Record the decision: relay-identity settings rows persist for the life of
  the relay registration (re-link semantics). When a delete-exchange feature
  lands, it must call `IDesktopSettingsRepository.DeleteAsync` inside the
  same operation — add a pointer comment in `UnlinkPersonaAsync`.

### Acceptance

- Persona shred → settings row gone (test green).
- The exchange-row persistence decision is documented here and in the
  `UnlinkPersonaAsync` doc-comment.

---

## WIN-02 — Persist & restore shell geometry + handoff lifecycle

**Merged:** former WIN-01 + WIN-04 — the store, the capture, the restore,
and the lock-close ordering are one feature and ship together.
**Depends on:** STOR-02 (`IVaultSettingsStore` exists), CTX-03 (registered
in `AddDesktopSessionServices`; `MainWindow` resolved from `session.Services`),
CTX-01 (non-nullable `TransitionToMainShellAsync`).
**Spec:** shell-flow.md §Responsive Design Principles ("remembers and restores
window dimensions, monitor coordinates, and maximized state") +
`IWindowHandoffCoordinator` contract ([INV-DESKTOP-TWO-STAGE-HANDOFF]).
**QMS:** [CB-001] (view mechanics only), [CB-003]/[CB-004] (Loaded/Unloaded
pairing), [VIEW-008] (no raw dimensions pumped into ViewModels).

### Summary

`MainWindow` restores its last size/position/maximized state when the shell
opens and persists them continuously while it is alive. Geometry is
presentation state stored in the encrypted vault via a thin
`IShellWindowGeometryStore` facade over `IVaultSettingsStore` — never in
ViewModels. The shell is created inside
`WindowHandoffCoordinator.TransitionToMainShellAsync` — restore happens
there, before `Show()`; `LockSessionAsync` closes the window and the
`Closing` save must land before the session tears down.

### Implementation

- **Store** — new `IShellWindowGeometryStore` in `Percolator.Desktop/Common`
  (**Stage-2 singleton**, ctor `IVaultSettingsStore` — registered in
  `AddDesktopSessionServices`, CTX-03). The facade owns its contract type
  and serialization shape (JSON into `app_settings` via
  `GetAppSettingAsync`/`SetAppSettingAsync`); the key is
  `VaultSettingKeys.ShellWindowGeometry`:

  ```csharp
  public sealed record ShellWindowGeometry(
      double X, double Y, double Width, double Height,   // DIPs — Avalonia geometry is double, not int
      WindowState State);                              // enum — no "Normal"/"Maximized" literals

  Task<ShellWindowGeometry?> LoadAsync(CancellationToken ct = default);
  Task SaveAsync(ShellWindowGeometry geometry, CancellationToken ct = default);
  ```

  `MainWindow` gains a second ctor parameter —
  `MainWindow(MainWindowViewModel vm, IShellWindowGeometryStore store) : this()`
  — chained on the parameterless previewer ctor, which stays ([CB-002]).
  Both deps resolve from `session.Services` at the composition seam; every
  path that reaches the shell carries a session — no store-less overload.

- **Capture** — subscribe to `PositionChanged` and `SizeChanged` in `Loaded`;
  write-through an R3 `Debounce(TimeSpan.FromMilliseconds(400))` so drags
  don't hammer SQLite, then a final write in `Closing` via the async-close
  pattern (the debounced write may not have landed, so `Closing` persists
  the last captured values, not just a debounce flush). Skip capture while
  `WindowState == WindowState.Minimized` (position is stale) and capture
  `RestoreBounds`-equivalents when `WindowState == WindowState.Maximized` —
  on Avalonia, `Bounds` reflects the maximized rect, so record `WindowState`
  separately and let the restore path apply Normal bounds *then* set
  `WindowState`.

- **Restore point** — `TransitionToMainShellAsync` resolves
  `MainWindowViewModel` + `IShellWindowGeometryStore` from
  `session.Services`, calls `LoadAsync` pre-`Show`, hands the result to
  `MainWindow.ApplyGeometry(ShellWindowGeometry?)` code-behind: record
  present → `WindowStartupLocation = Manual`, apply `Position` +
  `Width`/`Height`, clamp; `WindowState` applies inside `Opened` so
  maximization lands on the correct monitor in the same frame. Absent or
  unparseable record → keep the XAML default (`CenterScreen`, 1200×800).

- **Clamp to visible monitors** — the saved monitor may be disconnected or
  on a different DPI. All values are DIPs (Avalonia geometry is DIP-based,
  so `RenderScaling` differences do not corrupt the record). Pure helper,
  unit-testable without Avalonia:

  ```csharp
  static PixelRect ClampToWorkingArea(PixelRect candidate, IReadOnlyList<PixelRect> workingAreas)
  // 1. If candidate intersects any working area by >= 50% of its area: keep it.
  // 2. Else if it intersects partially: shift onto the intersecting screen.
  // 3. Else: return the primary working area's centered rect at the saved size
  //    (clamped to the working area size when the saved size exceeds it).
  ```

  Screens API: `window.Screens.All` → `Screen.WorkingArea` (excludes
  taskbar). Resolve screens at restore time — do not persist a monitor
  index; hardware order is not stable.

- **Close ordering** — the store dies with the session, so `LockSessionAsync`
  must let the `Closing` save land *before* `LockAsync` tears the session
  down; today it locks first. `Window.Close()` returns void while the
  async-close pattern is still mid-flight — the coordinator must `await`
  the window's `Closed` event (TCS wired at construction) before calling
  `LockAsync`.
- **Close semantics** — the `Closing` handler performs the final write with
  the standard async-close pattern (`e.Cancel = true` → `await SaveAsync`
  → clear flag → `Close()` again) — **no sync-over-async
  `.GetAwaiter().GetResult()`** on the UI thread. Wrap in try/catch:
  persistence failure never aborts lock/teardown — on failure just let the
  close proceed.
- **StartupWindow exclusion** — only `MainWindow` persists geometry; startup
  windows keep their fixed chrome.
- **First-run / decoy** — no record → `CenterScreen` default. The decoy
  vault is a real session (CTX-01), so decoy persistence works identically
  and dies with the decoy vault.
- **ShutdownMode** — verify
  `IClassicDesktopStyleApplicationLifetime.ShutdownMode`: closing
  `MainWindow` should terminate the app (`OnLastWindowClose`); confirm no
  regression where the process lingers after shell close.

### Edge cases

- Saved rect fully on a disconnected monitor → recenter on primary (rule 3).
- Saved `Maximized` on a monitor that no longer exists → restore Normal
  first (clamped), then maximize — Avalonia maximizes onto the screen
  containing the Normal bounds.
- Corrupt/partial JSON → treat as absent, log once at `Debug`.
- `MinWidth`/`MinHeight` (800×600) apply to stored values too — clamp on
  restore.

### Acceptance

- Move/resize/maximize → Lock Session → relaunch → shell restores all three
  with no visible center-then-jump.
- Unplug the monitor the window lived on → relaunch → fully on-screen.
- Drag continuously for several seconds → at most ~2 setting writes/sec,
  plus exactly one on close.
- Simulated settings-write failure during close → session lock still
  completes; closing `MainWindow` exits the process.
- Unit tests: `ClampToWorkingArea` rules 1–3, corrupt JSON, minimized-skip.

---

# Phase 3 — Cleanup

## STOR-05 — Retire the plaintext implementation & docs

**Depends on:** STOR-03 (+ STOR-04, WIN-02 in practice — the grep sweep
proves the migration is complete).

### Summary

- Delete `SqliteDesktopSettingsRepository.cs` (with its
  `EnsureTableAsync`/`OpenAsync`/`_connectionString` machinery and the
  `desktop-settings.db` literal) and `DesktopIdentitySettingsEntity.cs`.
  `VaultStorageOptions` **stays** — it is bootstrap config used by the
  unlock coordinator, `SqlCipherSchemaMigrator`, and
  `StartupHostViewModel.DataDirectoryPath`; only the repo's dependency on it
  dies.
- `implementation-plan.md`: DESK-03 registry row —
  `SqliteDesktopSettingsRepository` → `VaultDesktopSettingsRepository`
  (Stage-2 service) + `SqliteVaultSettingsStore` (Stage-2).
- `shell-window.md` WIN-02: updated — geometry rides
  `IShellWindowGeometryStore` (Stage-2) → `IVaultSettingsStore` under
  `VaultSettingKeys.ShellWindowGeometry` (no magic string).
- Grep-sweep for `SqliteDesktopSettingsRepository`,
  `DesktopIdentitySettingsEntity`, `desktop-settings`, and `EnsureTableAsync`
  stragglers (tests, docs, DI registrations).

### Acceptance

- Zero references to the deleted type names or `EnsureTableAsync` anywhere in
  the tree. No code path constructs or opens a second database file in
  `VaultStorageOptions.BaseDirectory`.
- Full `Percolator.Desktop.Tests` suite green.

## Resolved gaps & remaining implementation-time checks

Resolved during refinement (former `NEEDS DETAIL` flags):

- **STOR-01** — resolved: no EF migration needed. `SqliteDatabaseMigrator`'s
  `GenerateCreateScript()` + `CREATE ... IF NOT EXISTS` path creates new
  tables at unlock; EF-migrations tooling only matters for future ALTERs.
- **CTX-02** — resolved: slice signatures pinned
  (`AddSessionCoreInfrastructure` takes key+options; the rest read the
  registered `VaultStorageOptions`/`IMasterKeyHandle` via `sp`). The Stage-2
  scoped `IDatabaseUnlockCoordinator`, `IStage2HostFactory`,
  `AuthenticatedSessionHolder`, and the `IAuthenticatedSession`
  holder-binding are verified dead — delete.
- **CTX-03** — resolved differently: a session-bound `IWindowManager`
  instance (scopes off the session root) replaces the `provider`-parameter
  design — no interface change and zero call-site churn.
- **STOR-04** — resolved: no exchange-delete operation exists to wire; the
  ticket is now a parity test + a recorded row-lifecycle decision.

Remaining implementation-time verifications:

- **CTX-02** — the "stay scoped" bucket still needs a per-registration ctor
  audit: confirm nothing ctor-injects one of those scoped services from the
  root graph (the `ValidateScopes`/`ValidateOnBuild` pair turns violations
  into startup exceptions, so a bad classification fails fast rather than
  corrupting).
- **CTX-02** — diff each existing helper (`AddChatInfrastructure` et al.)
  against its monolith section; two known gaps already listed, there may
  be more.
- **WIN-01** — runtime check for flicker at the breakpoint boundary (add
  hysteresis only if observed).
- **WIN-02** — confirm `Window.PositionChanged`/`Screens.All` API names on
  the Avalonia version in use.

Final verification: full `Percolator.Desktop.Tests` + Infra suite at STOR-05.
