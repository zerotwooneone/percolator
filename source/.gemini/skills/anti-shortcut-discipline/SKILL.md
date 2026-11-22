---
name: anti-shortcut-discipline
description: >-
  Enforces zero-tolerance rules against AI code shortcuts: prohibits nullable required
  dependencies, cross-host RAM peeking, synthetic entities, omniscient fanouts, disconnected UI
  state, CQRS read violations, service locators, unhandled subscriptions, and outbox bypass.
---

# Anti-Shortcut Engineering Discipline

This skill enforces zero-tolerance standards against AI code shortcuts in Percolator.

---

## The 22 Invariants at a Glance

| # | Anti-Shortcut Rule | Detection Heuristic | Required Invariant |
| :-: | :--- | :--- | :--- |
| **1** | **No Nullable Required Parameters** | `Type? dep = null` in constructors | Required dependencies must be non-nullable without default values. Update DI/tests explicitly. |
| **2** | **Observer Isolation (No RAM Peeking)** | `_worldState.GetPersonaHost(peerId).X` or `host.Services.CreateScope()` | Resolve peer state strictly from observer's local DB (`IPeerContactRepository`, `IPeerProfileRepository`). Never resolve a remote host's DI container. |
| **3** | **State Machine Integrity** | `DirectChannel.Create` inline on send/share | Fail fast if preconditions are missing. Aggregate creation belongs exclusively in dedicated protocol handlers. |
| **4** | **Actor Boundary Scoping** | `_worldState.GetAllPersonaHosts()` or `_worldState.Groups` | Broadcasts/lists must iterate only owned contacts (`contactRepo.GetAllForOwnerAsync(...)`) or joined channels. Never omnisciently query world state. |
| **5** | **Autopilot Host Parity** | `[ObservableProperty]` toggle without host sync | Every autopilot toggle must exist on `ISimulatedPersonaHost`, init from it, and mutate it via partial callbacks. |
| **6** | **Observable Failure Signaling** | Empty `catch { }` or unlogged `return false` | Boundary failures must log to `ISimulatorLogSink`, display toasts, and publish state signals to `ISimulatorStateBus`. Zero bare `catch { }`. |
| **7** | **Explicit Overrides vs Defaults** | `!string.IsNullOrEmpty(contact.Nickname.Value)` | Distinguish default fallback GUID strings from explicit user nicknames before shadowing synced profiles. |
| **8** | **CQRS Read-Path Separation** | Injecting `IChannelRepository` into transcripts | Read paths and UI lists must query dedicated `*QueryService` returning flat DTOs. Domain repos are write-only. |
| **9** | **Zero Service Locator Usage** | Injecting `IServiceProvider` or `sp.GetService` | Explicit constructor injection for all dependencies. Never inject `IServiceProvider` into domain/app services. |
| **10** | **Anchored Reactive Subscriptions** | `.Subscribe(...)` without `.AddTo(...)` | Every observable subscription on a singleton bus must anchor to `ref Disposables` to prevent memory leaks. |
| **11** | **Outbox Durability** | Direct external transport dispatch on egress | Outbound payloads must stage as durable `OutboxJob` records before or alongside outbox flush/drain operations. |
| **12** | **Semantic Test Assertions** | `services.Should().HaveCount(N)` | Assert specific capability containment (`Should().Contain(...)`) and execution contracts, never magic numbers. |
| **13** | **Zero In-Process Transport Bypasses** | `relay.Services.CreateScope()` in client/coordinator | In simulated multi-node environments, client-to-relay operations (e.g. roster mutations) must call real gRPC services (`AnonymousGroupRelayService`), never mutate the relay DB via DI. |
| **14** | **No Coordinator State Backdoors** | `_groupMembers` dictionary in coordinator | The coordinator/harness must not maintain side-channel state dictionaries to spoon-feed state out-of-band to nodes. Node state transitions must occur solely through protocol messages or local storage. |
| **15** | **Deterministic Signal Awaiting** | `await Task.Delay(150)` in scenario steps | Prohibit arbitrary sleep delays as synchronization substitutes. Await explicit reactive signals (`_stateBus.Signals`), outbox job completions, or deterministic task tokens. |
| **16** | **Zero Captive Scope Facades (`IDbContextFactory` Standard)** | `Desktop*QueryService` creating `scope.ServiceProvider.GetService<Sql*>() ?? GetServices<I*>()` | Prohibit captive-dependency facade wrappers that create scopes and perform fallback resolution gymnastics. In desktop/client applications, inject `IDbContextFactory<TDbContext>` directly into query services/repositories so they can be registered as thread-safe Singletons without intermediate facades. |
| **17** | **No `GlobalUsings.cs` Band-Aids** | Added `global using` directives in `GlobalUsings.cs` to mask moved/refactored types | Never mask broken imports with `global using` shims. Large symbol/namespace moves must update explicit `using` statements via a compiler-driven `dotnet build` loop. |
| **18** | **Pure Persistence Adapters** | `IEventPublisher` injected into `*Repository` or publishing inside `SaveAsync` | Persistence repositories must only persist and rehydrate state. Application/outbox event publishing belongs strictly in Application orchestrators after all direct writes/steps commit. |
| **19** | **Atomic Outbox Claiming & Zero Manual Dispatch Races** | Background `HostedService` + manual `Flush*` reading shared rows without atomic claim (`Pending -> InFlight`) | When introducing an `IHostedService` dispatcher (`OutboxDispatchHost`), delete manual `Flush*` entry points and enforce atomic DB claiming (`ClaimDuePendingJobsAsync`) so concurrent callers cannot double-dispatch. |
| **20** | **CQRS Query Integrity (No N+1, Permissive Fallbacks, or String Key Queries)** | `await db.*` inside `foreach`, querying unowned tables on empty results, or comparing raw string URLs | Batch child lookups into single projections; enforce ownership boundaries with zero fallback queries into unowned tables; normalize Value Objects (`RelayEndpoint.Parse`) before comparison. |
| **21** | **Immediate Deletion of Synthetic UI Fallbacks** | Preserving `ChannelId.New()` or fake channels when adding real CQRS read models | Delete synthetic entity/ID fabrication in the same step that delivers the real query/command model. Never keep dead/divergent UI fallback branches alive. |
| **22** | **Deterministic Event-Driven Testing (Zero Masking Timers)** | Testing event-driven handlers with short poll intervals (`PollInterval = 50ms`) or `await Task.Delay(...)` | When testing event-driven `BackgroundService` loops, disable background polling (`PollInterval = Timeout.InfiniteTimeSpan`) and synchronize via `TaskCompletionSource`/`SemaphoreSlim` so broken subscriptions fail immediately. |

---

## Code Patterns: Bad vs. Good Micro-Diffs

### 1. Nullable Required Parameters
```csharp
// BAD: Lazy default null to avoid updating DI / call sites
public PersonaCardDependencies(IStatusToastService toast, ProfileSyncCoordinator? coord = null)
// GOOD: Explicit, mandatory dependency
public PersonaCardDependencies(IStatusToastService toast, ProfileSyncCoordinator coord)
    => (_toast, _coord) = (toast ?? throw new ArgumentNullException(nameof(toast)), coord ?? throw new ArgumentNullException(nameof(coord)));
```

### 2. Observer Isolation (No RAM Peeking)
```csharp
// BAD: Reaching into remote node's memory or remote DI container
var peerName = _worldState.GetPersonaHost(peerId).DisplayName;
using var scope = remoteHost.Services.CreateScope();
// GOOD: Querying local observer database
var profile = await profileRepo.GetByPeerIdAsync(peerId, ct);
var peerName = profile?.DisplayName ?? peerId.ToString()[..8];
```

### 3. State Machine Integrity
```csharp
// BAD: Fabricating channel to bypass handshake prerequisite
var ch = await repo.GetDirectByIdAsync(id, ct) ?? DirectChannel.Create(id, s, r, time).Value!;
// GOOD: Fail fast if handshake channel does not exist
var ch = await repo.GetDirectByIdAsync(id, ct) ?? throw new InvalidOperationException("Direct channel not established.");
```

### 4. Actor Boundary Scoping
```csharp
// BAD: Omniscient world fanout
foreach (var host in _worldState.GetAllPersonaHosts()) await ShareAsync(host);
foreach (var group in _worldState.Groups) list.Add(group);
// GOOD: Scoped strictly to actor's established contacts and joined channels
var contacts = await contactRepo.GetAllForOwnerAsync(host.PublicIdentityId, ct);
foreach (var contact in contacts) await ShareAsync(contact.RemotePeerId);
```

### 5. Autopilot Host Parity
```csharp
// BAD: VM-only toggle disconnected from host
[ObservableProperty] private bool _autoShareProfile = true;
// GOOD: Bidirectional sync between VM and simulated host
_autoShareProfile = host.AutoShareProfile;
partial void OnAutoShareProfileChanged(bool value) => _host.AutoShareProfile = value;
```

### 6. Observable Failure Signaling
```csharp
// BAD: Swallowing exceptions silently
catch { return false; }
catch { }
// GOOD: Logged and signaled on state bus
catch (Exception ex) {
    _logSink.Log(new SimulatorLogEntry(DateTimeOffset.UtcNow, LogLevel.Error, Name, Id, "Category", ex.Message));
    _stateBus.Publish(new SimulatorStateSignal(Id, SignalCategory.OperationFailed, Details: ex.Message));
    return false;
}
```

### 7. Explicit Overrides vs Defaults
```csharp
// BAD: Default GUID string masks synced profile name
var name = !string.IsNullOrWhiteSpace(contact.Nickname.Value) ? contact.Nickname.Value : profile.DisplayName;
// GOOD: Distinguish default fallback GUID from user-assigned nickname
var isCustom = contact != null && contact.Nickname.Value != peerId.ToString();
var name = isCustom ? contact.Nickname.Value : (profile?.DisplayName ?? peerId.ToString()[..8]);
```

### 8. CQRS Read-Path Separation
```csharp
// BAD: Loading write-side aggregate root for a read-only list
var channel = await _channelRepo.GetDirectByIdAsync(id, ct);
// GOOD: Querying read-optimized query service returning flat DTOs
var messages = await _chatQueryService.GetTranscriptAsync(id, ct);
```

### 9. Zero Service Locator Usage
```csharp
// BAD: Service locator injection
public MyService(IServiceProvider sp) => _client = sp.GetRequiredService<IClient>();
// GOOD: Explicit constructor injection
public MyService(IClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));
```

### 10. Anchored Reactive Subscriptions
```csharp
// BAD: Dropped subscription token pins VM in singleton memory forever
_bus.Signals.Subscribe(_ => Refresh());
// GOOD: Subscription token anchored to ViewModel lifecycle
_bus.Signals.Subscribe(_ => Refresh()).AddTo(ref Disposables);
```

### 11. Outbox Durability
```csharp
// BAD: Direct ephemeral network dispatch
await _transport.SendAsync(endpoint, envelope, ct);
// GOOD: Committed to durable outbox before dispatch
var job = OutboxJob.Create(channelId, sender, recipient, route, payload, timeProvider).Value!;
await _outboxRepo.SaveAsync(job, ct);
await _outboxFlusher.FlushPendingAsync(ct);
```

### 12. Semantic Test Assertions
```csharp
// BAD: Brittle magic number breaks on unrelated additions
services.Should().HaveCount(14);
// GOOD: Assert capability containment and execution contracts
services.Select(s => s.ServiceType).Should().Contain(typeof(IChatService));
await host.StartAsync(); await host.StopAsync();
```

### 13. Zero In-Process Transport Bypasses (Simulator Physical Boundary)
```csharp
// BAD: Peeking into Relay's DI container and directly mutating its SQLite DB
using var relayScope = relay.Services.CreateScope();
var ledgerRepo = relayScope.ServiceProvider.GetRequiredService<IRelayLedgerRepository>();
await ledgerRepo.SaveAsync(ledger, ct);

// GOOD: Calling the real gRPC endpoint over loopback TLS
var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);
var response = await client.CommitRosterMutationAsync(request, cancellationToken: ct);
```

### 14. No Coordinator State Backdoors
```csharp
// BAD: Coordinator maintains a private member dictionary to inject roster into joining nodes
_groupMembers[channelId] = members;
...
if (_groupMembers.TryGetValue(channelId, out var m)) groupChannel.AddMember(m);

// GOOD: Roster state transmitted via protocol payloads or synced through invitations
var invite = new GroupInvitationDto(..., initialMembers);
```

### 15. Deterministic Signal Awaiting (Zero Magic Sleeps)
```csharp
// BAD: Arbitrary sleep delays hoping async work finished
await Task.Delay(200, ct);

// GOOD: Await deterministic signal on state bus with timeout
await _stateBus.Signals
    .Where(s => s.NodeId == recipient.Id && s.Category == SignalCategory.ChatTranscriptChanged)
    .FirstAsync(cancellationToken: ct);
```

### 16. Zero Captive Scope Facades (IDbContextFactory Standard)
```csharp
// BAD: Facade manually managing scopes with recursive fallback hacks
public sealed class DesktopPeerContactQueryService : IPeerContactQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    public async Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<SqlPeerContactQueryService>()
            ?? scope.ServiceProvider.GetServices<IPeerContactQueryService>().FirstOrDefault(s => s is not DesktopPeerContactQueryService);
        return await inner.GetContactSummariesAsync(id, ct);
    }
}

// GOOD: Query service directly injects IDbContextFactory and creates context per call; registered as Singleton
public sealed class SqlPeerContactQueryService : IPeerContactQueryService
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    public SqlPeerContactQueryService(IDbContextFactory<PercolatorDbContext> contextFactory) => _contextFactory = contextFactory;

    public async Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        return await db.PeerContacts.AsNoTracking()...;
    }
}
```

### 17. No `GlobalUsings.cs` Band-Aids
```csharp
// BAD: Adding global usings across projects to avoid fixing broken references after moving types
// Percolator.Application/GlobalUsings.cs
global using Percolator.PluginSdk.Events;

// GOOD: Update explicit using directives file-by-file driven by compiler errors
// MyFile.cs
using Percolator.PluginSdk.Events;
```

### 18. Pure Persistence Adapters (No Repository Event Publishing)
```csharp
// BAD: Repository publishing application events or notifying background workers inside SaveAsync
public sealed class SqliteOutboxRepository : IOutboxRepository
{
    private readonly IEventPublisher _publisher;
    public async Task SaveAsync(OutboxJob job, CancellationToken ct)
    {
        // ... persist job ...
        await _publisher.PublishAsync(new OutboxJobEnqueuedEvent(job.JobId), ct); // RACE & HEXAGONAL VIOLATION
    }
}

// GOOD: Repository only persists. Application orchestrator publishes after direct execution/commit
public sealed class OutboundEgressPipeline
{
    public async Task<DomainResult> ProcessEgressAsync(...)
    {
        await _outboxRepo.SaveAsync(job, ct);
        // ... attempt Phase 5 direct write if online ...
        if (job.Status == OutboxJobStatus.Pending)
        {
            await _publisher.PublishAsync(new OutboxJobEnqueuedEvent(job.JobId), ct);
        }
    }
}
```

### 19. Atomic Outbox Claiming & Zero Manual Dispatch Races
```csharp
// BAD: Manual FlushOutboxAsync runs concurrently with background worker reading same pending jobs
await host.FlushOutboxAsync(); // Non-atomic dual-worker race condition

// GOOD: OutboxDispatchHost is sole dispatcher; queries atomically claim due pending jobs
var claimed = await _outboxRepo.ClaimDuePendingJobsAsync(batchSize, workerInstanceId, leaseDuration, ct);
// All callers simply stage OutboxJob and let OutboxDispatchHost wake up via event
```

### 20. CQRS Query Integrity (No N+1, Permissive Fallbacks, or String Key Queries)
```csharp
// BAD: N+1 database queries, permissive fallback on empty results, and raw string comparison
foreach (var ch in channels) {
    var last = await db.ChatMessages.Where(m => m.ChannelId == ch.Id)...FirstOrDefaultAsync();
}
if (channels.Count == 0) {
    // SECURITY BUG: queries all messages regardless of ownership fallback
    return await db.ChatMessages...ToListAsync();
}
var match = relayEndpoint != null && ch.RelayEndpoint == relayEndpoint.ToString();

// GOOD: Batch child queries in one round-trip, strict ownership boundary, normalized value objects
var channelIds = channels.Select(c => c.Id).ToList();
var latestMessages = await db.ChatMessages.AsNoTracking()
    .Where(m => channelIds.Contains(m.ChannelId))
    .GroupBy(m => m.ChannelId)
    .Select(g => g.OrderByDescending(x => x.TimestampUnixMs).First())
    .ToDictionaryAsync(m => m.ChannelId, ct);
var match = relayEndpoint != null && RelayEndpoint.TryParse(ch.RelayEndpoint, out var ep) && ep == relayEndpoint;
```

### 21. Immediate Deletion of Synthetic UI Fallbacks
```csharp
// BAD: Retaining synthetic fallback channel IDs in UI service when wiring real CQRS query
var channelId = summary?.ChannelId ?? ChannelId.New(); // Creates ghost channels

// GOOD: Require valid channel from query service; fail fast or surface pending state cleanly
var channelId = summary?.ChannelId ?? throw new InvalidOperationException($"No channel established for peer {peerId}.");
```

### 22. Deterministic Event-Driven Testing (Zero Masking Timers)
```csharp
// BAD: Short polling interval or Task.Delay masks missing event subscriptions in unit tests
var options = new OutboxDispatchHostOptions { PollInterval = TimeSpan.FromMilliseconds(50) };
await host.StartAsync();
await Task.Delay(200); // Test passes because of polling timer even if event subscription failed!

// GOOD: Disable polling loop; test event-driven wakeup deterministically with TCS / Semaphore
var options = new OutboxDispatchHostOptions { PollInterval = Timeout.InfiniteTimeSpan };
var processedTcs = new TaskCompletionSource<bool>();
// Assert subscription triggers and finishes without reliance on timers
await processedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
```

---

## Anti-Shortcut Pre-Flight Checklist

Before completing any task, ticket, or PR, verify:

- [ ] **1. No Nullable Required Params**: Grep constructors for `= null`. Required dependencies must be mandatory.
- [ ] **2. Observer Isolation**: Peer data in transcripts/views resolved from local observer DB, never `_worldState.GetPersonaHost(peer).X` or `remoteHost.Services`.
- [ ] **3. State Preconditions**: Zero synthetic `DirectChannel.Create` or fake sessions in application services.
- [ ] **4. Scoped Fanouts**: Updates iterate only `contactRepo.GetAllForOwnerAsync(...)` or joined channels.
- [ ] **5. Autopilot Parity**: UI toggles initialize from and mutate `ISimulatedPersonaHost`.
- [ ] **6. Observable Failures**: Catch blocks log to `ISimulatorLogSink` and publish failure signals on `ISimulatorStateBus`. Zero bare `catch { }`.
- [ ] **7. Explicit Nicknames**: Default GUID fallback does not mask synced profile display names.
- [ ] **8. CQRS Read Models**: Read paths use `*QueryService` returning DTOs, not domain aggregate repositories.
- [ ] **9. No Service Locator**: Zero `IServiceProvider` injection in domain/app classes.
- [ ] **10. Anchored Subscriptions**: All `.Subscribe(...)` calls anchored with `.AddTo(ref Disposables)`.
- [ ] **11. Outbox Durability**: Outgoing packets staged as `OutboxJob` before network dispatch.
- [ ] **12. Semantic Tests**: Zero `HaveCount(N)` assertions on service containers; assert specific capability containment.
- [ ] **13. No Transport Bypasses**: Client-to-relay operations go through real gRPC endpoints, never `relay.Services`.
- [ ] **14. No Coordinator Backdoors**: No central coordinator dictionaries to side-channel state between nodes.
- [ ] **15. Deterministic Awaiting**: Scenarios await explicit reactive signals, never `Task.Delay(N)`.
- [ ] **16. Zero Captive Scope Facades**: Query services inject `IDbContextFactory<TDbContext>` directly; no `Desktop*QueryService` facades resolving concrete types via `IServiceScopeFactory`.
- [ ] **17. No GlobalUsings Band-Aids**: Zero added `GlobalUsings.cs` shims; all moved types have explicit `using` statements.
- [ ] **18. Pure Persistence Adapters**: Zero `IEventPublisher` in repositories; outbox/application events published from Application orchestrators.
- [ ] **19. Atomic Outbox & Zero Manual Races**: Zero manual `FlushOutboxAsync`; `OutboxDispatchHost` owns dispatch and queries atomically claim jobs.
- [ ] **20. CQRS Query Integrity**: No N+1 queries, zero fallback querying unowned tables, and Value Objects normalized before comparison.
- [ ] **21. Zero Synthetic UI Fallbacks**: Zero `ChannelId.New()` or fabricated channel aggregates in UI state services.
- [ ] **22. Deterministic Event Tests**: `PollInterval = Timeout.InfiniteTimeSpan` and zero `Task.Delay` when testing event-driven workers.
- [ ] **Verification**: Run `dotnet run --project Percolator.Simulator\Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator\Scenarios` (all pass).
- [ ] **Regression**: Run all test projects (`dotnet test`) and verify 0 failures.

