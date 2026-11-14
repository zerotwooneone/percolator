# Mailbox Drain Wiring — Implementation Plan

Wires the dormant relay-mailbox drain path end to end: session-tracked active
identities, an application-layer drain use case, a bounded scheduled drain pool,
and removal of the placeholder `Func<CancellationToken, Task>` seam.

Status legend: ✅ done · 🚧 in progress · ⬜ not started

---

## Background

`RelayMailboxStreamWorker` and `RelayGroupStreamWorker` are registered in
`AddTransportInfrastructure` (`InfrastructureServiceCollectionExtensions.cs`)
with an optional `Func<CancellationToken, Task>` runner that falls back to
`Task.Delay(Timeout.InfiniteTimeSpan)` — the workers loop a permanent sleep.
No composition root registers the delegate, so the drain path is fully built
(`GrpcRelayMailboxProvisioner.DrainMailboxOnceAsync` already fetches, unwraps,
and feeds ingress) but never invoked.

The current shape is wrong for the actual domain:

- **Orchestration leaked into the adapter.** `DrainMailboxOnceAsync` resolves
  `ISealedEnvelopeUnwrapper` and `IInboundIngressPipeline` via service-locator
  inside a gRPC adapter — that unwrap→ingress loop is Application-layer work.
  The per-node `recipientId` scoping gymnastics exist only because of this leak.
- **Worker-per-? is unbounded and static.** A single `Func` can't express
  per-mailbox lifecycle; one failure domain covers all mailboxes; there is no
  concurrency cap.
- **Activation state is invisible to the session.** `TransitionToMainShellAsync`
  receives `IReadOnlyList<PersonaLaunchDescriptor>` but parks them in a Stage-1
  field for lease release only; the Stage-2 container never learns who is active.

## Design Principles

- **Model activation as session domain state**, not DI plumbing. `PersonaActivated`
  / `PersonaDeactivated` are domain events; the active set is state-of-record.
- **Events are triggers, state is truth.** Consumers reconcile desired vs actual
  after every signal — a missed or duplicated event degrades to a no-op re-diff,
  never to drift (missed `Deactivated` = leaked worker for the whole session).
- **Recipient is a use-case parameter, not a ctor dependency.** `UnwrapFrame`
  already accepts `recipientId` — the unwrap/ingress loop moves to Application
  where the recipient arrives as an argument, deleting the scope-locator hack.
- **Bounded pool + scheduled targets**, not worker-per-target. `MaxConcurrentDrains`
  is a fixed ceiling; surplus targets queue; per-target backoff is a next-due
  timestamp, not a sleeping thread.
- **Thin hosted-service adapter.** `BackgroundService` only translates host
  lifetime into scheduler lifecycle — no drain logic inside it.

## Architecture

| Layer | Piece | Location |
|---|---|---|
| Domain/ports | `IActiveIdentitySet`, `ActiveIdentityHandle`, `IdentitySetChange` | `Percolator.Application/Ports/` |
| Domain/ports | `IRelayMailboxClient` (narrowed transport port) | `Percolator.Application/Delivery/Ports/` |
| Application | `DrainMailboxService` (use case: fetch → unwrap → ingress) | `Percolator.Application/Delivery/` |
| Application | `DrainScheduler` (reconcile + due-time scheduling) | `Percolator.Application/Delivery/` |
| Driven port | `IDrainWorkerPool` (bounded executor) | `Percolator.Application/Ports/` |
| Adapter | `GrpcRelayMailboxClient` (RPC only) | `Percolator.Infrastructure/Transport/` |
| Adapter | `BoundedDrainWorkerPool` | `Percolator.Infrastructure/Transport/Workers/` |
| Adapter | `RetryLoop` (shared backoff primitive) | `Percolator.Infrastructure/Transport/Workers/` |
| Adapter | `RelayMailboxDrainHost : BackgroundService` | `Percolator.Infrastructure/Transport/Workers/` |
| Composition | Registry population at handoff seam | `WindowHandoffCoordinator` (Desktop) |

## Component Specs

### 1. `IActiveIdentitySet` — session-scoped activation registry

```csharp
public interface IActiveIdentitySet
{
    IReadOnlyCollection<PublicIdentityId> Current { get; }
    ChannelReader<IdentitySetChange> Changes { get; }
    bool Add(PublicIdentityId identityId);
    bool Remove(PublicIdentityId identityId);
}

public sealed record IdentitySetChange(
    PublicIdentityId IdentityId,
    IdentitySetChangeKind Kind);   // Activated | Deactivated
```

- Session singleton, registered in `AddSessionCoreInfrastructure` so every host
  (Desktop, Simulator, tests) has it; decoy sessions get an isolated instance
  for free.
- `Add`/`Remove` mutate `Current` first, then write to `Changes` — consumers
  reading `Current` after a signal always see a consistent snapshot.
- Population seam: `WindowHandoffCoordinator.TransitionToMainShellAsync`
  resolves `session.Services.GetRequiredService<IActiveIdentitySet>()` and calls
  `Add` per `PersonaLaunchDescriptor.IdentityId` — moved **before**
  `session.StartAsync()` (currently line 77 runs Start first) so workers observe
  the set on their first reconcile.
- Dynamic by contract: `Add`/`Remove` + `Changes` from day one. Mid-session
  persona activation is a deferred feature ticket — the contract makes it a
  two-line call when it lands.

### 2. `IRelayMailboxClient` — narrowed driven port

Split `IRelayMailboxProvisioner` so the transport port is pure RPC:

```csharp
public interface IRelayMailboxClient
{
    Task<DomainResult> ProvisionMailboxAsync(...);          // unchanged
    Task<DomainResult> PublishPreKeyBundleAsync(...);       // unchanged
    Task<DomainResult<IReadOnlyList<SealedEnvelope>>> FetchEnvelopesAsync(
        RelayEndpoint endpoint, MailboxTokenTriad tokens, CancellationToken ct);
    Task StopMailboxStreamAsync(...);                       // unchanged
}
```

`GrpcRelayMailboxProvisioner` becomes `GrpcRelayMailboxClient`: `FetchEnvelopesAsync`
returns raw parsed envelopes — no `ISealedEnvelopeUnwrapper`, no
`IInboundIngressPipeline`, no `IServiceScopeFactory`. Delete the per-drain
`CreateScope` + `GetRequiredService` locator block and the CTX-02 comment
justifying it (the lifetime conflict it worked around no longer exists).

### 3. `DrainMailboxService` — the Application use case

```csharp
public sealed class DrainMailboxService
{
    public async Task<int> DrainOnceAsync(
        PublicIdentityId recipientId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct) { ... }
}
```

Registered **scoped** in `AddAuthenticatedInfrastructure` (Application services).
Ctor-injects `IRelayMailboxClient`, `ISealedEnvelopeUnwrapper`,
`IInboundIngressPipeline`, `IDateTimeProvider` — all plain DI now that the
recipient is a method parameter. Body = the existing loop lifted from
`DrainMailboxOnceAsync`: fetch, skip self-echoes (`SealedWireFraming.Decode`
sender check), `UnwrapFrame(recipientId, ...)`, `ProcessInboundAsync`, count
successes, swallow malformed envelopes.

**Prerequisite:** `ISealedEnvelopeUnwrapper` / `IInboundIngressPipeline` need
session-container registrations. Today only `SimulatedPersonaHost` registers
them (baked `PublicIdentityId`). The session slice registers them scoped
without the recipient — unwrapper ctor keeps `recipientId` but `UnwrapFrame`
uses the parameter; verify no code path depends on ctor `_recipientId` beyond
the `Unwrap`/`UnwrapDirect` convenience shim, and if so pass the drain-supplied
recipient through those too.

### 4. `DrainScheduler` — reconcile + due-time scheduling

Session singleton owning two structures:

```csharp
private readonly ConcurrentDictionary<MailboxDrainTarget, TargetState> _targets;
// TargetState: LastFailureCount, NextPollAt, in-flight flag
private readonly PriorityQueue<MailboxDrainTarget, DateTimeOffset> _due;
```

`MailboxDrainTarget = (PublicIdentityId OwnerId, PublicIdentityId RelayId,
RelayEndpoint Endpoint)`. Tokens are fetched per drain via
`IRelayDirectory.GetMailboxTokensAsync` rather than cached — rotation-safe.

Reconcile loop:

```csharp
await foreach (var _ in changesChannel.Reader.ReadAllAsync(ct))
    Reconcile(await ComputeDesiredAsync(ct));

// ComputeDesiredAsync: IRelayDirectory.ListAsync()
//   .SelectMany(e => e.LinkedOwnerIdentityIds.Where(activeSet.Current.Contains)
//       .Select(o => new MailboxDrainTarget(o, e.RelayIdentityId, e.Endpoint)))
```

Periodic timer tick also feeds reconcile — self-healing against missed
binding changes (`MailboxBound`/`MailboxUnbound` events aren't emitted today;
the timer closes the gap until they are).

### 5. `IDrainWorkerPool` + `BoundedDrainWorkerPool` — the cap

```csharp
public interface IDrainWorkerPool
{
    /// Bounded admission: schedules a drain; returns when a slot frees or ct fires.
    Task RunDueAsync(Func<CancellationToken, Task> work, CancellationToken ct);
}
```

Implementation: `SemaphoreSlim(MaxConcurrentDrains)` + K consumer loops pulling
due targets. `MaxConcurrentDrains` is an options field (default ~4); surplus
targets sit in the priority queue — a slow relay can't starve the pool because
each drain is timeout-bounded.

`RetryLoop` extracted from `RelayMailboxStreamWorker.ExecuteAsync` into a static
reusable primitive (same backoff math); the pool wraps each drain execution.
On failure the scheduler bumps the target's `NextPollAt` exponentially — the
priority queue *is* the backoff, no sleeping per-target loop.

### 6. `RelayMailboxDrainHost` — thin adapter

```csharp
public sealed class RelayMailboxDrainHost : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _scheduler.RunAsync(stoppingToken);   // ~15 lines total
}
```

Registered `AddHostedService` in `AddTransportInfrastructure` **with an explicit
ctor dependency** on `DrainScheduler` — replaces the `Func` slot entirely.

### 7. Delete the dormant seam

- Remove `Func<CancellationToken, Task>` parameter + `?? Task.Delay(Infinite)`
  fallback from both `RelayMailboxStreamWorker` and `RelayGroupStreamWorker`
  registrations.
- `RelayMailboxStreamWorker` shrinks to a `RetryLoop` shim; group-stream wiring
  gets the same registry-driven treatment when group delivery lands (out of
  scope here — keep the worker type, remove the hack).

## Sequencing

1. **Ports + registry** — `IActiveIdentitySet`, `IdentitySetChange`, narrowed
   `IRelayMailboxClient`; registry registration in `AddSessionCoreInfrastructure`.
   No behavioral change yet.
2. **Layering repair** — extract `DrainMailboxService` from
   `GrpcRelayMailboxProvisioner.DrainMailboxOnceAsync`; add session registrations
   for `ISealedEnvelopeUnwrapper`/`IInboundIngressPipeline`; delete the scope
   locator. Provisioner keeps a thin `DrainMailboxOnceAsync` delegating to
   `FetchEnvelopesAsync` until callers migrate (one-step removal after).
3. **Scheduler + pool** — `DrainScheduler`, `IDrainWorkerPool`,
   `BoundedDrainWorkerPool`, `RetryLoop`, `RelayMailboxDrainHost` registration.
4. **Seam population** — reorder `TransitionToMainShellAsync`: populate registry,
   then `session.StartAsync()`; convert `Guid` → `PublicIdentityId` at the seam.
5. **Cleanup** — delete `Func` fallbacks, shrink stream-worker registrations,
   remove `_activePersonas`-only comment drift; update
   `shell-window.md` CTX-02 notes or supersede by this doc.

## Testing

- `IActiveIdentitySet` unit tests — add/remove/change-order, snapshot consistency.
- `DrainMailboxService` tests — fake `IRelayMailboxClient` + fake ingress;
  self-echo skip, malformed-envelope tolerance, success counting (existing
  provisioner test shape carries over).
- `DrainScheduler` tests — reconcile diff correctness, per-target backoff
  advancement, pool saturation under `MaxConcurrentDrains=1`.
- DI composition test — session container builds with `ValidateOnBuild`,
  `RelayMailboxDrainHost` resolves, no `Func` fallback remains.
- Simulator smoke — persona drains against `SimulatedRelayHost` mailbox
  (already exercised by the sim's event-driven path; now via the scheduled path).

## Acceptance Criteria

- [ ] `Add`/`Remove`/`Changes` registry lives in session container; populated
      before `session.StartAsync()` in `TransitionToMainShellAsync`.
- [ ] `GrpcRelayMailboxClient` has zero Application-ingress dependencies; no
      `IServiceScopeFactory`, no per-drain `CreateScope`.
- [ ] Drain executes for every active persona × linked relay within one poll
      interval of activation, bounded by `MaxConcurrentDrains`.
- [ ] A failing relay backs off its own `NextPollAt` without delaying other
      targets' schedules.
- [ ] `Func<CancellationToken, Task>` runner slot deleted from both stream
      workers; no optional-resolve fallbacks remain in transport registration.
- [ ] Decoy session drains only decoy personas (registry isolation).
- [ ] Desktop + Infrastructure test suites green; DI `ValidateOnBuild` passes.

## Deferred (explicit non-goals)

- **Mid-session persona activation** — contract-ready (`Add`/`Remove`/`Changes`);
  the feature needs gate-free port leasing + in-shell creation UI + duress
  review. Separate ticket.
- **Server-streaming subscriptions** — `IRelayMailboxCapabilityStore.SubscribeMailbox`
  shows the relay-side push shape already exists; when the remote RPC lands,
  long-lived streams join the same pool under the same cap.
- **`MailboxBound`/`MailboxUnbound` events** — timer reconcile covers it now;
  events are an optimization, not a correctness requirement.
- **`IPortReservationService` rename** — Application-ports lease vs Desktop
  socket-probe collision; alias tax remains.
