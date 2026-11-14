# Mailbox Drain — Implementation Plan

Status legend: ✅ done · 🚧 in progress · ⬜ not started

## Architecture

| Layer | Type | Location | Lifetime |
|---|---|---|---|
| Domain VO | `QueuedMailboxEnvelope(string QueueId, MailboxEnvelope Envelope)` | `Percolator.Domain/Delivery/ValueObjects/` | — |
| Domain VO | `PendingIngressRecord(string QueueId, QueuedMailboxEnvelope Queued, int Attempts, string? LastError)` | `Percolator.Domain/Delivery/ValueObjects/` | — |
| Domain Port | `IPendingIngressStore` | `Percolator.Domain/Delivery/Ports/` | scoped |
| App Port | `IActiveIdentitySet` | `Percolator.Application/Session/` | session singleton |
| App Events | `PersonaActivatedEvent`, `PersonaDeactivatedEvent`, `MailboxBindingChangedEvent` | `Percolator.Application/Session/Events/`, `…/Delivery/Events/` | — |
| App Service | `ActiveIdentitySet` | `Percolator.Application/Session/` | session singleton |
| App Port | `IRelayMailboxClient`, `IMailboxStreamSession` | `Percolator.Application/Delivery/Ports/` | — |
| App Port | `IRelayMailboxProvisioner` (Provision + Publish + `GetPreKeyStatusAsync`) | `Percolator.Application/Delivery/Ports/` | — |
| App Service | `MailboxDrainService` | `Percolator.Application/Delivery/` | scoped |
| App Service | `DrainScheduler`, `DrainSchedulerOptions` | `Percolator.Application/Delivery/` | session singleton |
| App Service | `PreKeyMaintenanceService` | `Percolator.Application/Delivery/` | session singleton |
| Adapter | `SqlitePendingIngressStore` | `Percolator.Infrastructure/Persistence/Repositories/` | scoped |
| Adapter | `GrpcRelayMailboxClient` | `Percolator.Infrastructure/Transport/` | singleton |
| Adapter | `GrpcRelayMailboxProvisioner` | `Percolator.Infrastructure/Transport/` | singleton |
| Adapter | `RelayMailboxDrainHost : BackgroundService` | `Percolator.Infrastructure/Transport/Workers/` | hosted |
| Seam | `WindowHandoffCoordinator.TransitionToMainShellAsync` | `Percolator.Desktop/Common/Windowing/` | — |

---

## Tickets

### DRAIN-01 ⬜ — Active-identity registry & handoff seam

**Files:**
- Create `Percolator.Application/Session/IActiveIdentitySet.cs`
- Create `Percolator.Application/Session/ActiveIdentitySet.cs`
- Create `Percolator.Application/Session/Events/PersonaActivatedEvent.cs`
- Create `Percolator.Application/Session/Events/PersonaDeactivatedEvent.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (`AddSessionCoreInfrastructure`)
- Modify `Percolator.Desktop/Common/Windowing/WindowHandoffCoordinator.cs` (`TransitionToMainShellAsync`)

**Contracts:**

```csharp
public sealed record PersonaActivatedEvent(PublicIdentityId IdentityId) : IApplicationEvent;
public sealed record PersonaDeactivatedEvent(PublicIdentityId IdentityId) : IApplicationEvent;

public interface IActiveIdentitySet
{
    IReadOnlyCollection<PublicIdentityId> Current { get; }
    bool Add(PublicIdentityId identityId);
    bool Remove(PublicIdentityId identityId);
}
```

**Implementation:**
- `ActiveIdentitySet(IEventPublisher publisher)`: lock-guarded `HashSet<PublicIdentityId>`. `Add`/`Remove` mutate the set under lock, snapshot `Current` to an immutable array, release the lock, then invoke `_publisher.Publish(new PersonaActivatedEvent(identityId))` / `PersonaDeactivatedEvent`. Returns `false` without publishing when state is unchanged.
- Register `AddSingleton<IActiveIdentitySet, ActiveIdentitySet>()` in `AddSessionCoreInfrastructure`.
- `WindowHandoffCoordinator.TransitionToMainShellAsync`: before `await session.StartAsync(ct)` (currently L77), resolve `session.Services.GetRequiredService<IActiveIdentitySet>()` and call `Add(new PublicIdentityId(d.IdentityId))` for each `PersonaLaunchDescriptor`.

**QMS:** `INV-DESKTOP-TWO-STAGE-HANDOFF`, `DEP-DESKTOP-PERSONA-ISOLATION`, `INV-APP-EVENT-BUS-DECOUPLING`, `DEC-EXECUTABLE-SESSION-COMPOSITION`.

---

### DRAIN-02 ⬜ — Transport port split & stream session contract

**Files:**
- Create `Percolator.Domain/Delivery/ValueObjects/QueuedMailboxEnvelope.cs`
- Create `Percolator.Application/Delivery/Ports/IRelayMailboxClient.cs`
- Create `Percolator.Application/Delivery/NullRelayMailboxClient.cs`
- Modify `Percolator.Application/Delivery/Ports/IRelayMailboxProvisioner.cs`
- Modify `Percolator.Application/Delivery/NullRelayMailboxProvisioner.cs`
- Modify `Percolator.Application.Tests/Delivery/RelayDirectoryServiceTests.cs`

**Contracts:**

```csharp
public sealed record QueuedMailboxEnvelope(string QueueId, MailboxEnvelope Envelope);

public sealed record RelayPreKeyStatus(uint OneTimePreKeyCount, uint SignedPreKeyId);

public interface IMailboxStreamSession : IAsyncDisposable
{
    IAsyncEnumerable<QueuedMailboxEnvelope> Envelopes { get; }
    Task<DomainResult> AcknowledgeAsync(string queueId, CancellationToken ct = default);
}

public interface IRelayMailboxClient
{
    Task<DomainResult<IMailboxStreamSession>> OpenMailboxStreamAsync(
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default);
}

public interface IRelayMailboxProvisioner
{
    Task<DomainResult> ProvisionMailboxAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default);

    Task<DomainResult> PublishPreKeyBundleAsync(
        PublicIdentityId ownerIdentityId,
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        int newOneTimePreKeyCount = 100,
        CancellationToken ct = default);

    Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
         DeviceId deviceId,
        CancellationToken ct = default);
}
```

- Remove `DrainMailboxOnceAsync` and `StopMailboxStreamAsync` from `IRelayMailboxProvisioner` and `NullRelayMailboxProvisioner`.
- `NullRelayMailboxClient.OpenMailboxStreamAsync` returns `DomainError("PROVISIONER_UNAVAILABLE", ...)`.

**QMS:** `INV-PURE-DOMAIN-PORTS`, `INV-ADAPTER-HYDRATION-BOUNDARY`.

---

### DRAIN-03 ⬜ — `GrpcRelayMailboxClient` & provisioner separation

**Files:**
- Create `Percolator.Infrastructure/Transport/GrpcRelayMailboxClient.cs`
- Modify `Percolator.Infrastructure/Transport/GrpcRelayMailboxProvisioner.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (`AddTransportInfrastructure`)

**`GrpcRelayMailboxClient(IPeerGrpcChannelFactory channelFactory, IDateTimeProvider timeProvider)` (singleton):**
1. Open `AuthenticatedRelayServiceClient.ConnectMailboxStream(cancellationToken: ct)`.
2. Read `AuthChallengeProto` from `ResponseStream` (16-byte `nonce`).
3. Compute `HMACSHA256.HashData(tokens.DrainToken.Span, challenge.Nonce.Span)`; write `MailboxStreamClientMessage { AuthResponse = new AuthResponseProto { RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()), ChallengeHmac = ByteString.CopyFrom(hmac) } }`.
4. Read `AuthResultProto`; if `!Authenticated`, dispose call and return `DomainError("RELAY_AUTH_FAILED", ErrorMessage)`.
5. Return `GrpcMailboxStreamSession` owning the `AsyncDuplexStreamingCall`, a `Channel.CreateBounded<QueuedMailboxEnvelope>(new BoundedChannelOptions(256) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait })`, a pump `Task`, and a `SemaphoreSlim(1, 1)` write gate.
6. Pump loop: for each `MailboxStreamServerMessage` with `PayloadCase == Envelope`, parse `SealedEnvelopeProto.Parser.ParseFrom(msg.Envelope.EnvelopePayload.Span)`; construct `MailboxEnvelope(EnvelopeId.New(), tokens.RoutingToken, sealedProto.EncryptedPackage.ToByteArray(), msg.Envelope.EnqueuedAtUtc.ToDateTimeOffset(), ClampExpiry(sealedProto.ExpiresAtUtc, now))` wrapped in `QueuedMailboxEnvelope(msg.Envelope.QueueId, mailboxEnvelope)`; write to bounded channel.
7. `AcknowledgeAsync(string queueId, CancellationToken ct)`: acquire write gate, call `RequestStream.WriteAsync(new MailboxStreamClientMessage { Ack = new MailboxAckProto { QueueId = queueId, Acknowledged = true } }, ct)`. Translate `RpcException` → `DomainError("STREAM_ACK_FAILED", ex.Status.Detail)`.
8. `DisposeAsync`: call `RequestStream.CompleteAsync()` (best-effort), cancel pump CTS, await pump completion, drain remaining items from channel and call `CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(item.Envelope.Ciphertext.Span))` on backing arrays, dispose duplex call.

**`GrpcRelayMailboxProvisioner` changes:**
- Remove `IServiceScopeFactory` constructor parameter, `DrainMailboxOnceAsync`, and `StopMailboxStreamAsync`.
- Register `AddSingleton<IRelayMailboxClient, GrpcRelayMailboxClient>()` in `AddTransportInfrastructure`.

**QMS:** `DEP-INFRA-NATIVE-FAULT-TRANSLATION`, `DEP-INFRA-VOLATILE-ZEROIZATION`.

---

### DRAIN-04 ⬜ — Recipient-parameterized `ISealedEnvelopeUnwrapper`

**Files:**
- Modify `Percolator.Application/Ports/ISealedEnvelopeUnwrapper.cs`
- Modify `Percolator.Infrastructure/Transport/SealedEnvelopeUnwrapper.cs`
- Modify `Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
- Modify `Percolator.ApplicationIntegrationTests/Adapters/SealedEnvelopeUnwrapper.cs`, `Builders/TestIdentityNode.cs`
- Modify `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs`
- Modify `Percolator.Infrastructure.Tests/Transport/Dispatching/SealedEnvelopeUnwrapperTests.cs`

**Contract:**

```csharp
public interface ISealedEnvelopeUnwrapper
{
    DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope, PublicIdentityId recipientId);
    DomainResult<InboundEnvelope> UnwrapDirect(ReadOnlyMemory<byte> payloadBytes, PublicIdentityId recipientId);
}
```

- `SealedEnvelopeUnwrapper(ISessionWirePacker wirePacker, IDateTimeProvider timeProvider)`: remove `_recipientId` field and ctor parameter; pass `recipientId` argument directly into `UnwrapFrame(rawPayload, recipientId, _timeProvider.UtcNow)`. Delete unused private `DefaultDateTimeProvider`.
- Register `AddScoped<ISealedEnvelopeUnwrapper, SealedEnvelopeUnwrapper>()` in `AddTransportInfrastructure`.
- `UnauthenticatedDeliveryEndpoint`: accept optional `PublicIdentityId? localRecipientId = null` ctor parameter; invoke `_envelopeUnwrapper.UnwrapDirect(request.EncryptedPackage.Memory, _localRecipientId.Value)` when non-null.

**QMS:** `INV-ADAPTER-HYDRATION-BOUNDARY`, `DEP-DESKTOP-PERSONA-ISOLATION`.

---

### DRAIN-05 ⬜ — `IPendingIngressStore` & `MailboxDrainService` (Signal persist-then-ack)

**Files:**
- Create `Percolator.Domain/Delivery/ValueObjects/PendingIngressRecord.cs`
- Create `Percolator.Domain/Delivery/Ports/IPendingIngressStore.cs`
- Create `Percolator.Infrastructure/Persistence/Repositories/SqlitePendingIngressStore.cs`
- Modify `Percolator.Infrastructure/Persistence/Entities/OpsEntities.cs` (replace unused `OpsInboundQueueEntity` with `VaultPendingIngressEnvelopeEntity`)
- Modify `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs`
- Create `Percolator.Application/Delivery/MailboxDrainService.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`

**Persistence schema (`vault_pending_ingress` — replaces unused `ops_inbound_queue`):**

```csharp
public sealed class VaultPendingIngressEnvelopeEntity
{
    public long SequenceNumber { get; set; }            // PK AUTOINCREMENT — preserves receive order
    public Guid OwnerIdentityId { get; set; }
    public string QueueId { get; set; } = string.Empty;
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
// Unique index: (OwnerIdentityId, QueueId) — dedup key
// Index: (OwnerIdentityId, SequenceNumber) — ordered replay scan
```

**Port & service contracts:**

```csharp
public interface IPendingIngressStore
{
    Task UpsertAsync(PublicIdentityId ownerId, QueuedMailboxEnvelope envelope, CancellationToken ct = default);
    Task<IReadOnlyList<PendingIngressRecord>> ListByOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default);
    Task DeleteAsync(PublicIdentityId ownerId, string queueId, CancellationToken ct = default);
    Task RecordTransientFailureAsync(PublicIdentityId ownerId, string queueId, string errorCode, CancellationToken ct = default);
}

public sealed class MailboxDrainService
{
    public const int MaxAttempts = 5;

    public Task<DomainResult> ReceiveAsync(
        MailboxDrainTarget target,
        QueuedMailboxEnvelope queued,
        IMailboxStreamSession session,
        CancellationToken ct = default);

    public Task<int> ReplayPendingAsync(
        MailboxDrainTarget target,
        CancellationToken ct = default);
}
```

**Execution steps (`ReceiveAsync`):**
1. `_pendingStore.UpsertAsync(target.OwnerIdentityId, queued, ct)` — idempotent on `(OwnerIdentityId, QueueId)`; persists encrypted bytes before network ACK. If this throws, do not ACK; propagate so the stream reconnects and the relay redelivers.
2. `var ack = await session.AcknowledgeAsync(queued.QueueId, ct)` — if `ack.IsFailure`, return `ack` (scheduler ends session and reconnects; step 1's upsert makes redelivery a no-op).
3. Run `ProcessPersistedAsync(target, queued.QueueId, queued.Envelope, ct)`.

**Execution steps (`ProcessPersistedAsync` — shared by `ReceiveAsync` step 3 and `ReplayPendingAsync`):**
1. If `envelope.ExpiresAtUtc <= _timeProvider.UtcNow` → `await _pendingStore.DeleteAsync(ownerId, queueId, ct)` and return.
2. `var unwrap = _unwrapper.Unwrap(envelope, ownerId)`:
   - Failure → log warning, `DeleteAsync` (malformed frame is terminal).
3. If `unwrap.Value.SenderIdentityId == ownerId` → `DeleteAsync` (self-echo).
4. Call `_ingress.ProcessInboundAsync(unwrap.Value, ct)`:
   - `IsSuccess`: `DeleteAsync`; if `unwrap.Value is InboundHandshakeEnvelope { OneTimePreKeyId: not null }`, call `_preKeyMaintenance.NotifyOneTimePreKeyConsumed(target)`.
   - `IsFailure` (any `DomainError`: `UNSUPPORTED_ENVELOPE`, `PAYLOAD_TOO_LARGE`, `EMPTY_INNER_PAYLOAD`, `DECRYPTION_FAILED`, `SESSION_NOT_FOUND`, `GROUP_SESSION_NOT_FOUND`, `HANDSHAKE_REPLAY_DETECTED`, `CONTACT_BLOCKED`, `SIGNED_PREKEY_MISSING`, `HANDSHAKE_DECRYPT_FAILED`, filter rejection): log warning, `DeleteAsync` (deterministic domain rejection cannot succeed on retry).
   - Exception (excluding `OperationCanceledException` when `ct.IsCancellationRequested`): call `RecordTransientFailureAsync`; if `attempts + 1 >= MaxAttempts`, `DeleteAsync` and log error.
5. `SqlitePendingIngressStore.DeleteAsync` overwrites `Ciphertext` with zeros before row removal (`CryptographicOperations.ZeroMemory`).

**QMS:** `SYS-RESULT-DRIVEN-INGRESS`, `DEC-RESULT-ORIENTED-HOTPATH`, `INV-CHANNEL-SERIALIZATION-GATE`, `SYS-ATOMIC-STATE-PERSISTENCE`, `SYS-ASYNC-STORE-AND-FORWARD`, `INV-PLAINTEXT-ZEROIZATION`, `DEP-INFRA-VOLATILE-ZEROIZATION`, `HAZ-CRASH-STATE-DESYNC`.

---

### DRAIN-06 ⬜ — `DrainScheduler` & `RelayMailboxDrainHost`

**Files:**
- Create `Percolator.Application/Delivery/MailboxDrainTarget.cs`
- Create `Percolator.Application/Delivery/DrainSchedulerOptions.cs`
- Create `Percolator.Application/Delivery/DrainScheduler.cs`
- Create `Percolator.Infrastructure/Transport/Workers/RelayMailboxDrainHost.cs`
- Delete `Percolator.Infrastructure/Transport/Workers/RelayMailboxStreamWorker.cs`
- Delete `Percolator.Infrastructure.Tests/Transport/Workers/RelayMailboxStreamWorkerTests.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
- Modify `Percolator.Infrastructure.Tests/Composition/DependencyInjectionTests.cs`

**Contracts:**

```csharp
public sealed record MailboxDrainTarget(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RelayIdentityId,
    RelayEndpoint Endpoint);

public sealed record DrainSchedulerOptions
{
    public int MaxConcurrentSessions { get; init; } = 64;
    public TimeSpan MaxSessionAge { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public double BackoffMultiplier { get; init; } = 2.0;
}
```

**`DrainScheduler` mechanics:**
- Ctor: `IActiveIdentitySet`, `IEventSubscriber`, `IServiceScopeFactory`, `IDateTimeProvider`, `DrainSchedulerOptions`.
- Subscribe in `RunAsync(CancellationToken stoppingToken)` to `PersonaActivatedEvent`, `PersonaDeactivatedEvent`, `MailboxBindingChangedEvent`; retain `IDisposable` tokens and dispose on exit. Each handler signals a `Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite })`. A `PeriodicTimer(ReconcileInterval)` writes to the same channel.
- `ReconcileAsync`:
  1. Create scope; call `IRelayDirectory.ListAsync(ct)`; compute desired set: `entries.SelectMany(e => e.LinkedOwnerIdentityIds.Where(active.Contains).Select(o => new MailboxDrainTarget(o, e.RelayIdentityId, e.Endpoint))).ToHashSet()`.
  2. For each removed target: cancel its supervisor `CancellationTokenSource` and await task completion.
  3. For each added target: enqueue into FIFO due-queue with `dueAt = now`.
- Admission loop: while `activeSessions.Count < MaxConcurrentSessions` and the earliest due target has `dueAt <= now`, dequeue and start `SuperviseTargetAsync(target, targetCts.Token)`.
- `SuperviseTargetAsync`:
  1. On first activation of `target` in this session, run ensure-provisioned (DRAIN-07) and trigger `PreKeyMaintenanceService.EvaluateAsync(target, forced: false)` (DRAIN-10).
  2. Create scope → resolve `IRelayDirectory`, `IRelayMailboxClient`, `MailboxDrainService`.
  3. Run `await drainService.ReplayPendingAsync(target, ct)`.
  4. Resolve fresh `MailboxTokenTriad` via `relayDirectory.GetMailboxTokensAsync(target.OwnerIdentityId, target.RelayIdentityId, ct)` (never cached across reconnects).
  5. Open stream with a linked CTS combining `targetCts.Token` and `CancelAfter(MaxSessionAge)`.
  6. `await foreach (var queued in session.Envelopes.WithCancellation(sessionCts.Token))`: create per-envelope scope, call `drainService.ReceiveAsync(target, queued, session, targetCts.Token)` (note: pass `targetCts.Token`, not `sessionCts.Token`, so an in-flight envelope finishes cleanly when `MaxSessionAge` elapses).
  7. On normal `MaxSessionAge` expiry: reset failure count to 0, re-enqueue target at tail of FIFO due-queue with `dueAt = now`.
  8. On stream fault / auth failure: increment target failure count, compute `backoff = min(MaxBackoff, InitialBackoff * pow(BackoffMultiplier, failures - 1))`, re-enqueue with `dueAt = now + backoff`.
- `RelayMailboxDrainHost : BackgroundService`: `ExecuteAsync(CancellationToken stoppingToken) => _scheduler.RunAsync(stoppingToken);`.
- Remove `RelayMailboxStreamWorker` and its optional `Func<CancellationToken, Task>` fallback registration. Update `DependencyInjectionTests` expected hosted-service type list (`typeof(RelayMailboxStreamWorker)` → `typeof(RelayMailboxDrainHost)`).

**QMS:** `SYS-PRE-ACTIVATION-STEALTH`, `DEP-DESKTOP-PRE-ACTIVATION-STEALTH`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`, `SYS-ASYNC-STORE-AND-FORWARD`, `DEP-INFRA-CONCURRENCY-MODEL`, `SYS-BOUNDED-RELAY-QUOTAS`, `HAZ-INGRESS-RESOURCE-EXHAUSTION`, `SYS-LOCK-TEARDOWN-AND-ZEROIZATION`, `DEP-DESKTOP-SESSION-LOCK-ZEROIZATION`, `HAZ-POST-LOCK-MEMORY-RESIDUE`.

---

### DRAIN-07 ⬜ — Ensure-provisioned on first target connect

**Files:**
- Modify `Percolator.Application/Delivery/RelayDirectoryService.cs`
- Modify `Percolator.Application/Delivery/DrainScheduler.cs`

- Change default `oneTimePreKeyCount` in `RelayDirectoryService.ProvisionLinkedMailboxesAsync` and `IRelayMailboxProvisioner.PublishPreKeyBundleAsync` from `25` to `100` (matches `RelayHostedPreKeyBundle.MaxOneTimePreKeyCapacity = 100` and Signal's batch size).
- In `DrainScheduler.SuperviseTargetAsync` step 1: on the first connect for a `MailboxDrainTarget` within the session, call `ProvisionMailboxAsync`; if the target has no relay-side bundle yet (`GetPreKeyStatusAsync` returns `OneTimePreKeyCount == 0 && SignedPreKeyId == 0`), call `PublishPreKeyBundleAsync(..., newOneTimePreKeyCount: 100)`. This guarantees `VaultUnlockViewModel.TryAutoStartLaunchAsync` (which bypasses `IdentityGateViewModel.LaunchPersonasAsync`) provisions and publishes identically to the gate path.

**QMS:** `SYS-ASYNC-SESSION-INIT`, `HAZ-PREKEY-POOL-STARVATION`.

---

### DRAIN-08 ⬜ — `ConnectMailboxStream` server hardening

**Files:**
- Modify `Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs`
- Modify `Percolator.Infrastructure.Tests/Transport/Endpoints/AuthenticatedRelayEndpointTests.cs`

1. Replace `Channel.CreateUnbounded<QueuedSealedEnvelopeProto>()` at L150 with `Channel.CreateBounded<QueuedSealedEnvelopeProto>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait })`.
2. Fix data race on `initialQueueIds`: populate the `HashSet<string>` completely from `queuedEnvelopes` *before* starting `liveReadTask` (keeping the subscription registered before `GetEnvelopesAsync` so no live enqueue is missed, while `initialQueueIds` is immutable by the time `liveReadTask` reads it).
3. In the ACK loop (L199): only invoke `AcknowledgeEnvelopeAsync` when `msg.Ack.Acknowledged && !string.IsNullOrEmpty(msg.Ack.QueueId)`.

**QMS:** `DEC-BOUNDED-CHANNEL-EVENT-BUS`, `HAZ-INGRESS-RESOURCE-EXHAUSTION`.

---

### DRAIN-09 ⬜ — Remove destructive unary `DrainMailbox` & migrate callers

**Files:**
- Modify `Percolator.Infrastructure/Protos/authenticated_relay.proto` (delete `rpc DrainMailbox`, `DrainMailboxRequest`, `DrainMailboxResponse`)
- Modify `Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs` (delete `DrainMailbox` override)
- Modify `Percolator.Infrastructure/Transport/IRelayMailboxCapabilityStore.cs` (rename `DrainEnvelopesAsync` → `PurgeMailboxAsync(ReadOnlyMemory<byte> routingToken, CancellationToken ct = default)` returning `ValueTask<int>`)
- Modify `Percolator.Infrastructure/Persistence/Repositories/SqliteRelayMailboxCapabilityStore.cs` (`PurgeMailboxAsync` uses `ExecuteDeleteAsync` directly without materializing payloads)
- Modify `Percolator.Desktop/Common/Services/ExchangeDirectoryService.cs` (`UnlinkPersonaAsync` L195: call `_capabilities.PurgeMailboxAsync`)
- Modify `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` (`DrainMailboxAsync`: open stream via `IRelayMailboxClient`, read available envelopes with short idle timeout, run `MailboxDrainService.ReceiveAsync` per envelope, dispose session)
- Modify `Percolator.Infrastructure.Tests/Fakes/FakeRelayMailboxCapabilityStore.cs`, `Percolator.Infrastructure.Windows.Tests/Fakes/FakeRelayMailboxCapabilityStore.cs`, `Percolator.Infrastructure.Tests/Persistence/SqliteRelayMailboxCapabilityStoreTests.cs`, `Percolator.Infrastructure.Tests/Transport/Endpoints/AuthenticatedRelayEndpointTests.cs`, `Percolator.Infrastructure.Tests/Protos/CoreProtoCompilationTests.cs`

- Note: `RelayMailboxQueue` (in-memory domain aggregate) and `TestIdentityNode.DrainMailboxAsync` remain unchanged — they back in-memory `Percolator.ApplicationIntegrationTests` and do not touch gRPC or SQLite.

**QMS:** `HAZ-CRASH-STATE-DESYNC`.

---

### DRAIN-10 ⬜ — Prekey status RPC, OPK low-watermark replenishment & 48h SPK rotation

**Files:**
- Modify `Percolator.Infrastructure/Protos/authenticated_relay.proto`
- Modify `Percolator.Domain/Identities/Ports/IPrivatePreKeyStore.cs`
- Modify `Percolator.Domain/Delivery/Hosting/RelayHostedPreKeyBundle.cs`
- Modify `Percolator.Domain/Identities/ValueObjects/PreKeyBundle.cs`
- Modify `Percolator.Infrastructure/Persistence/Entities/VaultKeyEntities.cs`, `RelayEntities.cs`, `PercolatorDbContext.cs`
- Modify `Percolator.Infrastructure/Persistence/Repositories/SqlitePrivatePreKeyStore.cs`, `SqliteRelayPreKeyRepository.cs`
- Modify `Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs`, `UnauthenticatedDeliveryEndpoint.cs`
- Modify `Percolator.Infrastructure/Adapters/PreKeyBundleProviderAdapter.cs`
- Modify `Percolator.Application/Handshake/HandshakeService.cs`
- Modify `Percolator.Infrastructure/Transport/GrpcRelayMailboxProvisioner.cs`
- Create `Percolator.Application/Delivery/PreKeyMaintenanceService.cs`
- Modify all in-memory `IPrivatePreKeyStore` test doubles (`Percolator.Application.Tests`, `Percolator.Domain.IntegrationTests`, `Percolator.ApplicationIntegrationTests`)

**1. Protobuf (`authenticated_relay.proto`):**

```protobuf
message GetPreKeyStatusRequest {
  bytes routing_token = 1;
  bytes drain_token = 2;
  uint32 device_id = 3;
}

message GetPreKeyStatusResponse {
  bool found = 1;
  uint32 one_time_prekey_count = 2;
  uint32 signed_prekey_id = 3;
}
// In service AuthenticatedRelayService:
rpc GetPreKeyStatus(GetPreKeyStatusRequest) returns (GetPreKeyStatusResponse);
```

**2. End-to-end `SignedPreKeyId` & multi-SPK retention (Signal parity):**
- `VaultSignedPreKeyEntity`: change PK from `(OwnerIdentityId, DeviceId)` to `(OwnerIdentityId, DeviceId, KeyId)`; add `uint KeyId { get; set; } = 1;`, `bool IsCurrent { get; set; } = true;`.
- `RelayPreKeyEntity`: add `uint SignedPreKeyId { get; set; } = 1;`.
- `RelayHostedPreKeyBundle`: add `uint SignedPreKeyId { get; private set; }` (passed to `Create`, `Rehydrate`, and `UpdateSignedPreKey(uint signedPreKeyId, ...)`).
- `PreKeyBundle`: add `uint SignedPreKeyId = 1` parameter; `UnauthenticatedDeliveryEndpoint.FetchPreKeyBundle` sets `SignedPrekeyId = consumed.SignedPreKeyId` (replaces hardcoded `1`); `PreKeyBundleProviderAdapter` maps `response.SignedPrekeyId`; `HandshakeService.InitiateHandshakeAsync` L149 uses `remoteBundle.SignedPreKeyId` (replaces hardcoded `1`); `HandshakeService.ProcessActiveInvitationAsync` L267 resolves the private key by `envelope.SignedPreKeyId`.
- `IPrivatePreKeyStore`:

```csharp
public sealed record StoredSignedPreKey(
    uint KeyId,
    EphemeralPrivateKey PrivateKey,
    DhPublicKey PublicKey,
    DeviceLinkProof Signature,
    DateTimeOffset CreatedAtUtc);

public interface IPrivatePreKeyStore
{
    Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId, DeviceId deviceId, uint? signedPreKeyId = null, CancellationToken ct = default);
    Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        PublicIdentityId ownerId, DeviceId deviceId, CancellationToken ct = default); // returns IsCurrent == true
    Task<uint> StoreSignedPreKeyAsync(
        PublicIdentityId ownerId, DeviceId deviceId, EphemeralPrivateKey privateKey,
        DhPublicKey publicKey, DeviceLinkProof signature, CancellationToken ct = default); // assigns next KeyId, marks current, sets CreatedAtUtc = UtcNow
    Task<int> PruneSignedPreKeysAsync(
        PublicIdentityId ownerId, DeviceId deviceId, DateTimeOffset olderThanUtc, int minRetainedCount = 3, CancellationToken ct = default);
    Task<int> CountAvailableOneTimePreKeysAsync(
        PublicIdentityId ownerId, DeviceId deviceId, CancellationToken ct = default);
    // existing OPK methods unchanged
}
```

- Signal SPK retention rule (`TSPreKeyManager.m` / Signal-Desktop `cleanSignedPreKeys`): retain previous signed prekeys for **7 days** and always keep at least the **3 most recent** SPKs so in-flight handshakes encrypted against a just-rotated SPK still decrypt; zeroize private key bytes before deleting pruned rows.

**3. `PreKeyMaintenanceService` (Application, session singleton):**
- Constants (matching Signal `TSPreKeyManager.m`): `LowWatermark = 35`, `TargetPoolSize = 100`, `StatusCheckInterval = TimeSpan.FromHours(12)`, `SignedPreKeyRotationAge = TimeSpan.FromHours(48)`, `SignedPreKeyRetentionAge = TimeSpan.FromDays(7)`, `MinRetainedSignedPreKeys = 3`.
- `NotifyOneTimePreKeyConsumed(MailboxDrainTarget target)` → schedules an immediate forced evaluation for `target`.
- `EvaluateAsync(MailboxDrainTarget target, bool forced, CancellationToken ct)`:
  1. Skip if `!forced` and `now - lastChecked[target] < StatusCheckInterval`.
  2. Fetch current SPK via `_preKeyStore.GetSignedPreKeyAsync(owner, DeviceId.Primary, ct)`. If `now - spk.CreatedAtUtc >= SignedPreKeyRotationAge`: generate new keypair via `_crypto.GenerateEphemeralKeyPair()`, sign with identity private key, call `StoreSignedPreKeyAsync`, then `PruneSignedPreKeysAsync(owner, DeviceId.Primary, now - SignedPreKeyRetentionAge, MinRetainedSignedPreKeys, ct)`.
  3. Call `_provisioner.GetPreKeyStatusAsync(target.Endpoint, tokens, DeviceId.Primary, ct)`.
  4. If `!status.Found` or `status.OneTimePreKeyCount <= LowWatermark` or `status.SignedPreKeyId != currentSpk.KeyId`: compute `toGenerate = status.OneTimePreKeyCount <= LowWatermark ? (int)(TargetPoolSize - status.OneTimePreKeyCount) : 0`; call `_provisioner.PublishPreKeyBundleAsync(owner, DeviceId.Primary, target.Endpoint, tokens, toGenerate, ct)`.
  5. Record `lastChecked[target] = now`.

**QMS:** `HAZ-PREKEY-POOL-STARVATION`, `SYS-ATOMIC-STATE-PERSISTENCE`, `DEP-INFRA-VOLATILE-ZEROIZATION`.

---

### DRAIN-11 ⬜ — `MailboxBindingChangedEvent` on link/unlink

**Files:**
- Create `Percolator.Application/Delivery/Events/MailboxBindingChangedEvent.cs`
- Modify `Percolator.Application/Delivery/RelayDirectoryService.cs`
- Modify `Percolator.Application.Tests/Delivery/RelayDirectoryServiceTests.cs`

```csharp
public enum MailboxBindingChangeKind { Linked, Unlinked }

public sealed record MailboxBindingChangedEvent(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RelayIdentityId,
    MailboxBindingChangeKind Kind) : IApplicationEvent;
```

- Inject `IEventPublisher? eventPublisher = null` into `RelayDirectoryService`; publish after `_relayRepository.SaveAsync` in `LinkIdentityAsync`, `UnlinkIdentityAsync`, and `CreateLocalRelayHostAsync` (for each initial linked owner).

**QMS:** `INV-APP-EVENT-BUS-DECOUPLING`.

---

## Execution Order

1. **DRAIN-01** — Registry, events, handoff seam ordering.
2. **DRAIN-02, DRAIN-03, DRAIN-04** — Port split, `GrpcRelayMailboxClient`, recipient-parameterized unwrapper.
3. **DRAIN-05, DRAIN-06, DRAIN-07** — `IPendingIngressStore` + `MailboxDrainService`, `DrainScheduler` + `RelayMailboxDrainHost`, first-connect provisioning.
4. **DRAIN-08** — Server-side stream bounded channel, `initialQueueIds` race fix, ACK guard.
5. **DRAIN-09** — Delete unary `DrainMailbox` RPC, rename `DrainEnvelopesAsync` → `PurgeMailboxAsync`, migrate `SimulatedPersonaHost` and `ExchangeDirectoryService`.
6. **DRAIN-10, DRAIN-11** — `GetPreKeyStatus` RPC, `SignedPreKeyId` end-to-end wiring + multi-SPK retention, `PreKeyMaintenanceService`, `MailboxBindingChangedEvent`.

---

## Verification

| Suite / Target | Coverage |
|---|---|
| `ActiveIdentitySetTests` | Idempotent `Add`/`Remove`, mutate-before-publish snapshot ordering. |
| `GrpcRelayMailboxClientTests` | Challenge/HMAC auth, `RELAY_AUTH_FAILED` mapping, bounded channel ordering, serialized `AcknowledgeAsync`, buffer zeroization on `DisposeAsync`. |
| `SqlitePendingIngressStoreTests` | `(OwnerIdentityId, QueueId)` upsert deduplication, `SequenceNumber` ordering, `Ciphertext` zeroization on delete, attempt increment. |
| `MailboxDrainServiceTests` | Persist-before-ack ordering, no-ack on persist failure, terminal `DomainError` deletion, transient exception retry up to `MaxAttempts = 5`, expiry drop, self-echo deletion, OPK-consumed maintenance trigger. |
| `DrainSchedulerTests` | Event + timer reconciliation diff, FIFO admission at `MaxConcurrentSessions`, graceful `MaxSessionAge` rotation after in-flight envelope, per-target exponential backoff isolation, persona deactivation teardown. |
| `PreKeyMaintenanceServiceTests` | 12h throttle vs forced evaluation, top-up math at `<= 35` to `100`, 48h SPK rotation + 7d/min-3 pruning, SPK mismatch republish. |
| `AuthenticatedRelayEndpointTests` | Bounded live channel backpressure, concurrent enqueue during initial replay delivered once, `GetPreKeyStatus` auth & counts, `Acknowledged = false` ignored. |
| `DependencyInjectionTests` | Stage-2 container builds with `ValidateScopes` + `ValidateOnBuild`; `RelayMailboxDrainHost` registered; no `RelayMailboxStreamWorker` or `Func<CancellationToken, Task>` fallback. |

---

## QMS Traceability

| Ticket | Realized Requirements / Invariants / Controlled Hazards |
|---|---|
| DRAIN-01 | `INV-DESKTOP-TWO-STAGE-HANDOFF`, `DEP-DESKTOP-PERSONA-ISOLATION`, `INV-APP-EVENT-BUS-DECOUPLING`, `DEC-EXECUTABLE-SESSION-COMPOSITION` |
| DRAIN-02, DRAIN-03 | `INV-PURE-DOMAIN-PORTS`, `INV-ADAPTER-HYDRATION-BOUNDARY`, `DEP-INFRA-NATIVE-FAULT-TRANSLATION`, `DEP-INFRA-VOLATILE-ZEROIZATION` |
| DRAIN-04 | `INV-ADAPTER-HYDRATION-BOUNDARY`, `DEP-DESKTOP-PERSONA-ISOLATION` |
| DRAIN-05 | `SYS-RESULT-DRIVEN-INGRESS`, `DEC-RESULT-ORIENTED-HOTPATH`, `INV-CHANNEL-SERIALIZATION-GATE`, `SYS-ATOMIC-STATE-PERSISTENCE`, `SYS-ASYNC-STORE-AND-FORWARD`, `INV-PLAINTEXT-ZEROIZATION`, `DEP-INFRA-VOLATILE-ZEROIZATION`, `HAZ-CRASH-STATE-DESYNC` |
| DRAIN-06 | `SYS-PRE-ACTIVATION-STEALTH`, `DEP-DESKTOP-PRE-ACTIVATION-STEALTH`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`, `SYS-ASYNC-STORE-AND-FORWARD`, `DEP-INFRA-CONCURRENCY-MODEL`, `SYS-BOUNDED-RELAY-QUOTAS`, `HAZ-INGRESS-RESOURCE-EXHAUSTION`, `SYS-LOCK-TEARDOWN-AND-ZEROIZATION`, `DEP-DESKTOP-SESSION-LOCK-ZEROIZATION`, `HAZ-POST-LOCK-MEMORY-RESIDUE` |
| DRAIN-07, DRAIN-10 | `SYS-ASYNC-SESSION-INIT`, `HAZ-PREKEY-POOL-STARVATION`, `SYS-ATOMIC-STATE-PERSISTENCE`, `DEP-INFRA-VOLATILE-ZEROIZATION` |
| DRAIN-08 | `DEC-BOUNDED-CHANNEL-EVENT-BUS`, `HAZ-INGRESS-RESOURCE-EXHAUSTION` |
| DRAIN-09 | `HAZ-CRASH-STATE-DESYNC` |
| DRAIN-11 | `INV-APP-EVENT-BUS-DECOUPLING` |
| All | `SYS-SEALED-SENDER-CAPABILITY-GATING`, `SYS-BLINDED-RELAY-ENVELOPES`, `INV-PAYLOAD-OPACITY` |

