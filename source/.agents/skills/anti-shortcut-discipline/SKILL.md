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

## The 12 Invariants at a Glance

| # | Anti-Shortcut Rule | Detection Heuristic | Required Invariant |
| :-: | :--- | :--- | :--- |
| **1** | **No Nullable Required Parameters** | `Type? dep = null` in constructors | Required dependencies must be non-nullable without default values. Update DI/tests explicitly. |
| **2** | **Observer Isolation (No RAM Peeking)** | `_worldState.GetPersonaHost(peerId).X` | Resolve peer state strictly from observer's local DB (`IPeerContactRepository`, `IPeerProfileRepository`). |
| **3** | **State Machine Integrity** | `DirectChannel.Create` inline on send/share | Fail fast if preconditions are missing. Aggregate creation belongs exclusively in dedicated protocol handlers. |
| **4** | **Actor Boundary Scoping** | `_worldState.GetAllPersonaHosts()` for broadcasts | Broadcasts/rerolls must iterate only owned contacts (`contactRepo.GetAllForOwnerAsync(host.PublicIdentityId)`). |
| **5** | **Autopilot Host Parity** | `[ObservableProperty]` toggle without host sync | Every autopilot toggle must exist on `ISimulatedPersonaHost`, init from it, and mutate it via partial callbacks. |
| **6** | **Observable Failure Signaling** | Empty `catch { }` or unlogged `return false` | Boundary failures must log to `ISimulatorLogSink`, display toasts, and publish state signals to `ISimulatorStateBus`. |
| **7** | **Explicit Overrides vs Defaults** | `!string.IsNullOrEmpty(contact.Nickname.Value)` | Distinguish default fallback GUID strings from explicit user nicknames before shadowing synced profiles. |
| **8** | **CQRS Read-Path Separation** | Injecting `IChannelRepository` into transcripts | Read paths and UI lists must query dedicated `*QueryService` returning flat DTOs. Domain repos are write-only. |
| **9** | **Zero Service Locator Usage** | Injecting `IServiceProvider` or `sp.GetService` | Explicit constructor injection for all dependencies. Never inject `IServiceProvider` into domain/app services. |
| **10** | **Anchored Reactive Subscriptions** | `.Subscribe(...)` without `.AddTo(...)` | Every observable subscription on a singleton bus must anchor to `ref Disposables` to prevent memory leaks. |
| **11** | **Outbox Durability** | Direct external transport dispatch on egress | Outbound payloads must stage as durable `OutboxJob` records before or alongside outbox flush/drain operations. |
| **12** | **Semantic Test Assertions** | `services.Should().HaveCount(N)` | Assert specific capability containment (`Should().Contain(...)`) and execution contracts, never magic numbers. |

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
// BAD: Reaching into remote node's memory
var peerName = _worldState.GetPersonaHost(peerId).DisplayName;
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
// GOOD: Scoped strictly to actor's established contacts
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

---

## Anti-Shortcut Pre-Flight Checklist

Before completing any task, ticket, or PR, verify:

- [ ] **1. No Nullable Required Params**: Grep constructors for `= null`. Required dependencies must be mandatory.
- [ ] **2. Observer Isolation**: Peer data in transcripts/views resolved from local observer DB, never `_worldState.GetPersonaHost(peer).X`.
- [ ] **3. State Preconditions**: Zero synthetic `DirectChannel.Create` or fake sessions in application services.
- [ ] **4. Scoped Fanouts**: Updates iterate only `contactRepo.GetAllForOwnerAsync(host.PublicIdentityId)`.
- [ ] **5. Autopilot Parity**: UI toggles initialize from and mutate `ISimulatedPersonaHost`.
- [ ] **6. Observable Failures**: Catch blocks log to `ISimulatorLogSink` and publish failure signals on `ISimulatorStateBus`.
- [ ] **7. Explicit Nicknames**: Default GUID fallback does not mask synced profile display names.
- [ ] **8. CQRS Read Models**: Read paths use `*QueryService` returning DTOs, not domain aggregate repositories.
- [ ] **9. No Service Locator**: Zero `IServiceProvider` injection in domain/app classes.
- [ ] **10. Anchored Subscriptions**: All `.Subscribe(...)` calls anchored with `.AddTo(ref Disposables)`.
- [ ] **11. Outbox Durability**: Outgoing packets staged as `OutboxJob` before network dispatch.
- [ ] **12. Semantic Tests**: Zero `HaveCount(N)` assertions on service containers; assert specific capability containment.
- [ ] **Verification**: Run `dotnet run --project Percolator.Simulator\Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator\Scenarios` (all pass).
- [ ] **Regression**: Run all test projects (`dotnet test`) and verify 0 failures.
