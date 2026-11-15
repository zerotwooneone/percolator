# Mailbox Drain — Implementation Plan

Spec: [implementation-plan-spec](../spec/implementation-plan.spec.md) — read this spec before modifying this document.

Status legend: ✅ done · 🚧 in progress · ⬜ not started

Verification convention: any ticket that modifies `Percolator.Simulator` must run the full scenario suite before being marked done — `dotnet run --framework net10.0-windows10.0.19041.0 -- --replay Scenarios` (from `Percolator.Simulator/`, or `--project` from `source/`; use `net10.0` on non-Windows). See `Percolator.Simulator/README.md`.

Plan-wide QMS: `SYS-SEALED-SENDER-CAPABILITY-GATING`, `SYS-BLINDED-RELAY-ENVELOPES`, `INV-PAYLOAD-OPACITY`.

## Architecture

| Layer | Type | Location | Lifetime |
|---|---|---|---|
| Domain VO | `QueuedMailboxEnvelope(MailboxQueueId QueueId, MailboxEnvelope Envelope)` | `Percolator.Domain/Delivery/ValueObjects/` | — |
| Domain VO | `MailboxQueueId` (opaque relay-assigned ack key), `SignedPreKeyId` | `Percolator.Domain/Delivery/ValueObjects/`, `…/Identities/ValueObjects/` | — |
| Domain VO | `PendingIngressRecord(MailboxQueueId QueueId, QueuedMailboxEnvelope Queued, int Attempts, DomainError? LastError)` | `Percolator.Domain/Delivery/ValueObjects/` | — |
| Domain VO | `RelayQueuedEnvelopeRecord(MailboxQueueId QueueId, ReadOnlyMemory<byte> EnvelopePayload, DateTimeOffset EnqueuedAtUtc)` | `Percolator.Domain/Delivery/ValueObjects/` | — |
| Domain Port | `IPendingIngressStore` | `Percolator.Domain/Delivery/Ports/` | scoped |
| Domain Port | `IRelayMailboxCapabilityStore` (relocated from Infra in DRAIN-09) | `Percolator.Domain/Delivery/Ports/` | scoped |
| App Port | `IActiveIdentitySet` | `Percolator.Application/Session/Ports/` | session singleton |
| App Events | `PersonaActivatedEvent`, `PersonaDeactivatedEvent`, `MailboxBindingChangedEvent` | `Percolator.Application/Session/Events/`, `…/Delivery/Events/` | — |
| App Service | `ActiveIdentitySet` | `Percolator.Application/Session/` | session singleton |
| App Port | `IRelayMailboxClient`, `IMailboxStreamSession` | `Percolator.Application/Delivery/Ports/` | — |
| App Null | `NullRelayMailboxClient` | `Percolator.Application/Delivery/` | singleton |
| App Record | `MailboxDrainTarget(PublicIdentityId OwnerIdentityId, PublicIdentityId RelayIdentityId, RelayEndpoint Endpoint)` | `Percolator.Application/Delivery/` | — |
| App Record | `RelayPreKeyStatus(bool Found, uint OneTimePreKeyCount, SignedPreKeyId)` | `Percolator.Application/Delivery/Ports/` | — |
| Domain Record | `StoredSignedPreKey(KeyId, PrivateKey, PublicKey, Signature, CreatedAtUtc)` | `Percolator.Domain/Identities/Ports/` | — |
| App Port | `IRelayMailboxProvisioner` (Provision + Publish + `GetPreKeyStatusAsync`) | `Percolator.Application/Delivery/Ports/` | — |
| App Service | `MailboxDrainService` | `Percolator.Application/Delivery/` | scoped |
| App Service | `DrainScheduler`, `DrainSchedulerOptions` | `Percolator.Application/Delivery/` | session singleton |
| App Service | `PreKeyMaintenanceService` | `Percolator.Application/Delivery/` | session singleton |
| Adapter | `SqlitePendingIngressStore` | `Percolator.Infrastructure/Persistence/Repositories/` | scoped |
| Adapter | `GrpcRelayMailboxClient` | `Percolator.Infrastructure/Transport/` | singleton |
| Adapter | `GrpcRelayMailboxProvisioner` | `Percolator.Infrastructure/Transport/` | singleton |
| Adapter Entity | `OpsPendingIngressEnvelopeEntity` | `Percolator.Infrastructure/Persistence/Entities/` | — |
| Adapter | `RelayMailboxDrainHost : BackgroundService` | `Percolator.Infrastructure/Transport/Workers/` | hosted |
| Seam | `WindowHandoffCoordinator.TransitionToMainShellAsync` | `Percolator.Desktop/Common/Windowing/` | — |

---

## Tickets

### DRAIN-01 ✅ — Active-identity registry & handoff seam

**Files:**
- Create `Percolator.Application/Session/Ports/IActiveIdentitySet.cs`
- Create `Percolator.Application/Session/ActiveIdentitySet.cs`
- Create `Percolator.Application/Session/Events/PersonaActivatedEvent.cs`
- Create `Percolator.Application/Session/Events/PersonaDeactivatedEvent.cs`
- Create `Percolator.Desktop.Tests/Windowing/WindowHandoffCoordinatorTests.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (`AddSessionCoreInfrastructure`)
- Modify `Percolator.Desktop/Common/Windowing/IWindowHandoffCoordinator.cs` & `WindowHandoffCoordinator.cs` (`TransitionToMainShellAsync`)
- Modify `Percolator.Infrastructure/Vault/AuthenticatedSession.cs` (injected `ILogger`, eliminated bare catch)
- Refactor `PersonaLaunchDescriptor`, `PersonaDirectoryEntry`, `SelectablePersonaModel` to eliminate primitive obsession (`PublicIdentityId`)

**Contracts:**

```csharp
public sealed record PersonaActivatedEvent(PublicIdentityId IdentityId) : IApplicationEvent;
public sealed record PersonaDeactivatedEvent(PublicIdentityId IdentityId) : IApplicationEvent;

namespace Percolator.Application.Session.Ports;

public interface IActiveIdentitySet
{
    IReadOnlyCollection<PublicIdentityId> Current { get; }
    bool Contains(PublicIdentityId identityId);
    ValueTask<bool> AddAsync(PublicIdentityId identityId, CancellationToken ct = default);
    ValueTask<bool> RemoveAsync(PublicIdentityId identityId, CancellationToken ct = default);
    bool Add(PublicIdentityId identityId);
    bool Remove(PublicIdentityId identityId);
}
```

**Implementation:**
- `ActiveIdentitySet(IEventPublisher publisher, ILogger<ActiveIdentitySet> logger)`: lock-guarded `HashSet<PublicIdentityId>`. Async methods `AddAsync`/`RemoveAsync` await `_publisher.PublishAsync` deterministically; non-blocking synchronous overloads dispatch background publishing safely logging errors via `ILogger` without unhandled task discards.
- Register `AddSingleton<IActiveIdentitySet, ActiveIdentitySet>()` in `AddSessionCoreInfrastructure`.
- Clean hexagonal architecture: port located strictly in `Percolator.Application.Session.Ports`.
- `WindowHandoffCoordinator.TransitionToMainShellAsync`: takes non-nullable `IReadOnlyList<PersonaLaunchDescriptor>` and `IReadOnlyList<ExchangeLaunchDescriptor>`. Before `await session.StartAsync(ct)`, resolves `session.Services.GetRequiredService<IActiveIdentitySet>()` and awaits `AddAsync(descriptor.IdentityId, ct)` for each persona.
- `WindowHandoffCoordinatorTests`: covers ordering (active set seeded before `session.StartAsync()`), parameter validation, and socket lease disposal on session lock.

**Verified:** Shipped & verified with unit test suite (`dotnet test Percolator.sln`) and full 8-scenario simulator suite (`dotnet run --project Percolator.Simulator\Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator\Scenarios`).

**QMS:** `INV-DESKTOP-TWO-STAGE-HANDOFF`, `DEP-DESKTOP-PERSONA-ISOLATION`, `INV-APP-EVENT-BUS-DECOUPLING`, `DEC-EXECUTABLE-SESSION-COMPOSITION`, `ANTI-SHORTCUT-ZERO-TOLERANCE`.

---

### DRAIN-02 ✅ — Transport port split & stream session contract

**Files:**
- Create `Percolator.Domain/Delivery/ValueObjects/QueuedMailboxEnvelope.cs`
- Create `Percolator.Domain/Delivery/ValueObjects/MailboxQueueId.cs`
- Create `Percolator.Domain/Identities/ValueObjects/SignedPreKeyId.cs`
- Create `Percolator.Application/Delivery/Ports/MailboxTokenTriad.cs`
- Create `Percolator.Application/Delivery/Ports/RelayPreKeyStatus.cs`
- Create `Percolator.Application/Delivery/Ports/IMailboxStreamSession.cs`
- Create `Percolator.Application/Delivery/Ports/IRelayMailboxClient.cs`
- Create `Percolator.Application/Delivery/NullRelayMailboxClient.cs`
- Create `Percolator.Domain.Tests/Delivery/MailboxQueueIdTests.cs`
- Create `Percolator.Domain.Tests/Identities/SignedPreKeyIdTests.cs`
- Create `Percolator.Application.Tests/Delivery/NullRelayMailboxClientTests.cs`
- Create `Percolator.Application.Tests/Delivery/NullRelayMailboxProvisionerTests.cs`
- Modify `Percolator.Application/Delivery/Ports/IRelayMailboxProvisioner.cs`
- Modify `Percolator.Application/Delivery/NullRelayMailboxProvisioner.cs`
- Modify `Percolator.Application.Tests/Delivery/RelayDirectoryServiceTests.cs`

**Contracts:**

```csharp
public sealed record QueuedMailboxEnvelope(MailboxQueueId QueueId, MailboxEnvelope Envelope);

public sealed record RelayPreKeyStatus(bool Found, uint OneTimePreKeyCount, SignedPreKeyId SignedPreKeyId);

public sealed record MailboxTokenTriad(
    BlindedRoutingToken RoutingToken,
    DeliveryToken DeliveryToken,
    DeliveryToken DrainToken,
    DeliveryToken? PreviousDrainToken = null);

public interface IMailboxStreamSession : IAsyncDisposable
{
    IAsyncEnumerable<QueuedMailboxEnvelope> Envelopes { get; }
    Task<DomainResult> AcknowledgeAsync(MailboxQueueId queueId, CancellationToken ct = default);
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
        uint newOneTimePreKeyCount,
        CancellationToken ct = default);

    Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default);
}
```

- Remove `DrainMailboxOnceAsync` and `StopMailboxStreamAsync` from `IRelayMailboxProvisioner` and `NullRelayMailboxProvisioner`.
- `NullRelayMailboxClient.OpenMailboxStreamAsync` returns `DomainError("RELAY_MAILBOX_CLIENT_UNAVAILABLE", ...)`.
- Single-type-per-file hexagonal separation: `MailboxTokenTriad`, `RelayPreKeyStatus`, and `IMailboxStreamSession` reside in dedicated port files.
- `SignedPreKeyId.None` defines the standard invalid/unfound sentinel (`Value == 0`, `IsValid == false`).
- `newOneTimePreKeyCount` is typed `uint` across ports, eliminating signed/unsigned cast friction with `RelayPreKeyStatus.OneTimePreKeyCount`.

**Verified:** Shipped & verified with unit test suite (`dotnet test Percolator.sln`) covering domain VOs, null client/provisioner deterministic errors, and full 8-scenario simulator suite (`dotnet run --project Percolator.Simulator\Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator\Scenarios`).

**QMS:** `INV-PURE-DOMAIN-PORTS`, `INV-ADAPTER-HYDRATION-BOUNDARY`, `DEP-INFRA-MAILBOX-RETRIEVAL`, `ANTI-SHORTCUT-ZERO-TOLERANCE`.

---

### DRAIN-03 ✅ — `GrpcRelayMailboxClient` & provisioner separation

**Files:**
- Create `Percolator.Infrastructure/Transport/GrpcRelayMailboxClient.cs`
- Modify `Percolator.Infrastructure/Transport/GrpcRelayMailboxProvisioner.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (`AddTransportInfrastructure`)

**`GrpcRelayMailboxClient(IPeerGrpcChannelFactory channelFactory, IDateTimeProvider timeProvider)` (singleton):**
1. Open `AuthenticatedRelayServiceClient.ConnectMailboxStream(cancellationToken: ct)`.
2. Read `AuthChallengeProto` from `ResponseStream` (32-byte `nonce`).
3. Compute `HMACSHA256.HashData(tokens.DrainToken.Span, challenge.Nonce.Span)`; write `MailboxStreamClientMessage { AuthResponse = new AuthResponseProto { RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()), ChallengeHmac = ByteString.CopyFrom(hmac) } }`.
4. Read `AuthResultProto`; if `!Authenticated`, dispose call and return `DomainError("RELAY_AUTH_FAILED", ErrorMessage)`.
5. Return `GrpcMailboxStreamSession` owning the `AsyncDuplexStreamingCall`, a `Channel.CreateBounded<QueuedMailboxEnvelope>(new BoundedChannelOptions(256) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait })`, a pump `Task`, and a `SemaphoreSlim(1, 1)` write gate.
6. Pump loop: for each `MailboxStreamServerMessage` with `PayloadCase == Envelope`, parse `SealedEnvelopeProto.Parser.ParseFrom(msg.Envelope.EnvelopePayload.Span)`; parse `MailboxQueueId.Create(msg.Envelope.QueueId)` — drop envelopes whose id fails to parse (un-ackable); construct `MailboxEnvelope(EnvelopeId.New(), tokens.RoutingToken, sealedProto.EncryptedPackage.ToByteArray(), msg.Envelope.EnqueuedAtUtc.ToDateTimeOffset(), ClampExpiry(sealedProto.ExpiresAtUtc, now))` wrapped in `QueuedMailboxEnvelope(queueId, mailboxEnvelope)`; write to bounded channel.
7. `AcknowledgeAsync(MailboxQueueId queueId, CancellationToken ct)`: acquire write gate, call `RequestStream.WriteAsync(new MailboxStreamClientMessage { Ack = new MailboxAckProto { QueueId = queueId.Value, Acknowledged = true } }, ct)`. Translate `RpcException` → `DomainError("STREAM_ACK_FAILED", ex.Status.Detail)`.
8. `DisposeAsync`: call `RequestStream.CompleteAsync()` (best-effort), cancel pump CTS, await pump completion, drain remaining items from channel and call `CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(item.Envelope.Ciphertext).Span)` on backing arrays, dispose duplex call.

**`GrpcRelayMailboxProvisioner` changes:**
- `StopMailboxStreamAsync` was already removed in DRAIN-02 — nothing to do here.
- `DrainMailboxOnceAsync` stays on the concrete class (simulator bridge until DRAIN-09), so the `IServiceScopeFactory` and `IDateTimeProvider` ctor parameters must remain; they are removed in DRAIN-09 instead.
- Register `AddSingleton<IRelayMailboxClient, GrpcRelayMailboxClient>()` in `AddTransportInfrastructure`.

**Verified:** shipped — steps corrected in place for nonce size, `queue_id` parse-drop, and `AsMemory` zeroization. `AuthResultProto.reason` feeds `RELAY_AUTH_FAILED`; `GrpcMailboxStreamSession` is a private nested class; expiry clamped to `now + EnvelopeExpiryWindow` (7d). `DrainMailboxOnceAsync` stays on the concrete provisioner until DRAIN-09. Test backfill landed in DRAIN-04 (`GrpcRelayMailboxClientTests`).

**QMS:** `DEP-INFRA-NATIVE-FAULT-TRANSLATION`, `DEP-INFRA-VOLATILE-ZEROIZATION`, `DEP-INFRA-MAILBOX-RETRIEVAL`.

---

### DRAIN-04 ✅ — Recipient-parameterized `ISealedEnvelopeUnwrapper`

**Files:**
- Modify `Percolator.Application/Ports/ISealedEnvelopeUnwrapper.cs`
- Modify `Percolator.Infrastructure/Transport/SealedEnvelopeUnwrapper.cs`
- Modify `Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
- Modify `Percolator.ApplicationIntegrationTests/Adapters/SealedEnvelopeUnwrapper.cs`, `Builders/TestIdentityNode.cs`
- Modify `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs`
- Modify `Percolator.Infrastructure.Tests/Transport/Dispatching/SealedEnvelopeUnwrapperTests.cs`
- Create `Percolator.Infrastructure.Tests/Transport/GrpcRelayMailboxClientTests.cs` (DRAIN-03 backfill — challenge/HMAC auth, `RELAY_AUTH_FAILED` mapping, serialized `AcknowledgeAsync`, zeroization on `DisposeAsync`)

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
- `SimulatedPersonaHost`: delete the per-node `ISealedEnvelopeUnwrapper` factory registration (~L254–259: `new SealedEnvelopeUnwrapper(wirePacker, PublicIdentityId, time)`) — the `AddTransportInfrastructure` scoped registration replaces it, so the sim shares the same registration as Desktop.

**Verified:** `dotnet test Percolator.Infrastructure.Tests` (affected fixtures, 25 passed), `Percolator.ApplicationIntegrationTests` (19 passed), full scenario suite (8/8). One forward constraint: the `PublicIdentityId?` scoped registration in `SimulatedPersonaHost` must survive — see DRAIN-06/DRAIN-09.

**QMS:** `INV-ADAPTER-HYDRATION-BOUNDARY`, `DEP-DESKTOP-PERSONA-ISOLATION`.

---

### DRAIN-05 ✅ — `IPendingIngressStore` & `MailboxDrainService` (Signal persist-then-ack)

**Files:**
- Create `Percolator.Domain/Delivery/ValueObjects/PendingIngressRecord.cs`
- Create `Percolator.Domain/Delivery/Ports/IPendingIngressStore.cs`
- Create `Percolator.Infrastructure/Persistence/Repositories/SqlitePendingIngressStore.cs`
- Modify `Percolator.Infrastructure/Persistence/Entities/OpsEntities.cs` (replace unused `OpsInboundQueueEntity` with `OpsPendingIngressEnvelopeEntity`)
- Modify `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs`
- Create `Percolator.Application/Delivery/MailboxDrainTarget.cs`
- Create `Percolator.Application/Delivery/MailboxDrainService.cs`
- Create `Percolator.Application/Delivery/Ports/IPreKeyMaintenanceService.cs` (forward port — DRAIN-10 implements it)
- Create `Percolator.Application/Delivery/NullPreKeyMaintenanceService.cs`
- Modify `Percolator.Application/Percolator.Application.csproj` (`Microsoft.Extensions.Logging.Abstractions` for `ILogger`)
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
- Create `Percolator.Application.Tests/Delivery/MailboxDrainServiceTests.cs`
- Create `Percolator.Infrastructure.Tests/Persistence/SqlitePendingIngressStoreTests.cs`

**Persistence schema (`ops_pending_ingress` — replaces unused `ops_inbound_queue`):**

```csharp
public sealed class OpsPendingIngressEnvelopeEntity
{
    public long SequenceNumber { get; set; }            // PK AUTOINCREMENT — preserves receive order
    public Guid OwnerIdentityId { get; set; }
    public string QueueId { get; set; } = string.Empty; // MailboxQueueId.Value — opaque relay ack key
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDescription { get; set; }
}
// Unique index: (OwnerIdentityId, QueueId) — dedup key
// Index: (OwnerIdentityId, SequenceNumber) — ordered replay scan
```

- Naming: `Ops*`/`ops_*` (not `vault_pending_ingress`) — the spool is operational ingress state carrying only encrypted ciphertext; `Vault*` is reserved for persona crypto rows.

**Port & service contracts:**

```csharp
public sealed record MailboxDrainTarget(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RelayIdentityId,
    RelayEndpoint Endpoint);

public sealed record PendingIngressRecord(
    MailboxQueueId QueueId,
    QueuedMailboxEnvelope Queued,
    int Attempts,
    DomainError? LastError);

public interface IPendingIngressStore
{
    // Returns the canonical stored record — a dedup hit keeps the pre-existing row's Attempts
    Task<PendingIngressRecord> UpsertAsync(PublicIdentityId ownerId, QueuedMailboxEnvelope envelope, CancellationToken ct = default);
    Task<IReadOnlyList<PendingIngressRecord>> ListByOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default);
    Task DeleteAsync(PublicIdentityId ownerId, MailboxQueueId queueId, CancellationToken ct = default);
    Task RecordTransientFailureAsync(PublicIdentityId ownerId, MailboxQueueId queueId, DomainError error, CancellationToken ct = default);
}

public sealed class MailboxDrainService
{
    public const int MaxAttempts = 5;

    public Task<DomainResult> ReceiveAsync(
        MailboxDrainTarget target,
        QueuedMailboxEnvelope queued,
        IMailboxStreamSession session,
        CancellationToken ct = default);

    public Task<DomainResult<int>> ReplayPendingAsync(
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

**Verified:** shipped — `MailboxDrainServiceTests` 15/15, `SqlitePendingIngressStoreTests` 9/9, full `Percolator.Infrastructure.Tests` 265/265, `dotnet build Percolator.sln` clean. Deviations from the spec text above: `UpsertAsync` returns `Task<PendingIngressRecord>` (the canonical stored record, so the retry budget enforces persisted attempts on dedup hits); `ProcessPersistedAsync` takes the `PendingIngressRecord`, not the raw envelope; `ReplayPendingAsync` counts resolved (left-spool) records; `EnvelopeId` is not persisted — a fresh one is generated on rehydration (local metadata only; dedup key is `QueueId`); `SqlitePendingIngressStore` uses the ambient scoped `PercolatorDbContext` like the other handler-only repositories. The inbound ingress chain (`IChannelLockService`→`KeyedSemaphoreLockService`, `IHandshakeReplayFilter`→`MemoryHandshakeReplayFilter`, `IContactRequestCoordinator`, `IRoutingCoordinator`, `IPayloadSender`→`OutboundEgressPipeline`, `IGroupKeyDistributionService`, `AppRouter`/`IAppRouter` enumerable factory, `IHandshakeService`, `IInboundIngressPipeline`) moved into shared `AddTransportInfrastructure` so `MailboxDrainService` resolves — sim's later per-host registrations still win (last-registration-wins). `IPreKeyMaintenanceService` shipped as a forward port; a `NullPreKeyMaintenanceService` singleton backs it until DRAIN-10.

**QMS:** `SYS-RESULT-DRIVEN-INGRESS`, `DEC-RESULT-ORIENTED-HOTPATH`, `INV-CHANNEL-SERIALIZATION-GATE`, `SYS-ATOMIC-STATE-PERSISTENCE`, `SYS-ASYNC-STORE-AND-FORWARD`, `DEP-INFRA-MAILBOX-RETRIEVAL`, `DEC-PENDING-INGRESS-SPOOL`, `INV-PLAINTEXT-ZEROIZATION`, `DEP-INFRA-VOLATILE-ZEROIZATION`, `HAZ-CRASH-STATE-DESYNC`.

---

### DRAIN-06 ✅ — `DrainScheduler` & `RelayMailboxDrainHost`

**Files:**
- Create `Percolator.Application/Delivery/DrainSchedulerOptions.cs`
- Create `Percolator.Application/Delivery/Events/MailboxBindingChangedEvent.cs` (moved forward from DRAIN-11 — the scheduler subscribes to it)
- Create `Percolator.Application/Delivery/DrainScheduler.cs`
- Create `Percolator.Infrastructure/Transport/Workers/RelayMailboxDrainHost.cs`
- Delete `Percolator.Infrastructure/Transport/Workers/RelayMailboxStreamWorker.cs`
- Delete `Percolator.Infrastructure.Tests/Transport/Workers/RelayMailboxStreamWorkerTests.cs`
- Modify `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
- Modify `Percolator.Infrastructure.Tests/Composition/DependencyInjectionTests.cs`
- Create `Percolator.Application.Tests/Delivery/DrainSchedulerTests.cs`
- Modify `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` (simulator drain-parity wiring — see below)
- Modify `Percolator.Simulator/Features/Topology/ISimulatedPersonaHost.cs` (`HomeRelay` → get-only + `AssignHomeRelayAsync`)
- Modify `Percolator.Simulator/Features/RelayedChat/RelayProvisioningCoordinator.cs` (call site → `AssignHomeRelayAsync`)
- Modify `Percolator.Desktop/Common/Session/DesktopSessionServiceCollectionExtensions.cs` (app dispatch composition — see below)

**Contracts:**

```csharp
public sealed record DrainSchedulerOptions
{
    public int MaxConcurrentSessions { get; init; } = 64;
    public TimeSpan MaxSessionAge { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public double BackoffMultiplier { get; init; } = 2.0;
}

public enum MailboxBindingChangeKind { Linked, Unlinked }

public sealed record MailboxBindingChangedEvent(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RelayIdentityId,
    MailboxBindingChangeKind Kind) : IApplicationEvent;
// MailboxDrainTarget defined in DRAIN-05.
```

**`DrainScheduler` mechanics:**
- Ctor: `IActiveIdentitySet`, `IEventSubscriber`, `IServiceScopeFactory`, `IDateTimeProvider`, `DrainSchedulerOptions`, `ILogger<DrainScheduler>`.
- Subscribe in `RunAsync(CancellationToken stoppingToken)` to `PersonaActivatedEvent`, `PersonaDeactivatedEvent`, `MailboxBindingChangedEvent`; retain `IDisposable` tokens and dispose on exit. Each handler signals a `Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite })`. A `PeriodicTimer(ReconcileInterval)` writes to the same channel.
- `ReconcileAsync`:
  1. Create scope; call `IRelayDirectory.ListAsync(ct)`; compute desired set: `entries.SelectMany(e => e.LinkedOwnerIdentityIds.Where(active.Contains).Select(o => new MailboxDrainTarget(o, e.RelayIdentityId, e.Endpoint))).ToHashSet()`.
  2. For each removed target: cancel its supervisor `CancellationTokenSource` and await task completion.
  3. For each added target: enqueue into FIFO due-queue with `dueAt = now`.
- Admission loop: while `activeSessions.Count < MaxConcurrentSessions` and the earliest due target has `dueAt <= now`, dequeue and start `SuperviseTargetAsync(target, targetCts.Token)`.
- `SuperviseTargetAsync`:
  1. Track first activation per target per session (`HashSet<MailboxDrainTarget>`) — this is the hook point where DRAIN-07 adds ensure-provisioned and DRAIN-10 adds `IPreKeyMaintenanceService.EvaluateAsync(target, forced: false)` (the port shipped in DRAIN-05 backed by `NullPreKeyMaintenanceService`); the real service doesn't exist yet, so DRAIN-06 only records activation.
  2. Create scope → resolve `IRelayDirectory`, `IRelayMailboxClient`, `MailboxDrainService`.
  3. Run `await drainService.ReplayPendingAsync(target, ct)`.
  4. Resolve fresh `MailboxTokenTriad` via `relayDirectory.GetMailboxTokensAsync(target.OwnerIdentityId, target.RelayIdentityId, ct)` (never cached across reconnects).
  5. Open stream with a linked CTS combining `targetCts.Token` and `CancelAfter(MaxSessionAge)`.
  6. `await foreach (var queued in session.Envelopes.WithCancellation(sessionCts.Token))`: create per-envelope scope, call `drainService.ReceiveAsync(target, queued, session, targetCts.Token)` (note: pass `targetCts.Token`, not `sessionCts.Token`, so an in-flight envelope finishes cleanly when `MaxSessionAge` elapses).
  7. On normal `MaxSessionAge` expiry: reset failure count to 0, re-enqueue target at tail of FIFO due-queue with `dueAt = now`.
  8. On stream fault / auth failure: increment target failure count, compute `backoff = min(MaxBackoff, InitialBackoff * pow(BackoffMultiplier, failures - 1))`, re-enqueue with `dueAt = now + backoff`.
- `RelayMailboxDrainHost : BackgroundService`: `ExecuteAsync(CancellationToken stoppingToken) => _scheduler.RunAsync(stoppingToken);`.
- Remove `RelayMailboxStreamWorker` and its optional `Func<CancellationToken, Task>` fallback registration. Update `DependencyInjectionTests` expected hosted-service type list (`typeof(RelayMailboxStreamWorker)` → `typeof(RelayMailboxDrainHost)`).

**Simulator wiring** (decision: sim runs the same scheduler as Desktop — rejected pull-only sim drain, which would leave the scheduler uncovered by scenarios):
- `SimulatedPersonaHost.StartAsync`: resolve `IActiveIdentitySet` from `_host.Services` and `Add(PublicIdentityId)` before `await _host.StartAsync(ct)` (mirrors `TransitionToMainShellAsync` ordering — set populated before hosted services run).
- `AssignHomeRelayAsync(ISimulatedRelayHost relay, ct)` (new; `HomeRelay` setter is private, `ISimulatedPersonaHost.HomeRelay` get-only): set `HomeRelay`, then ensure a `RelayRegistration` exists — `IRelayDirectory.ListAsync` + `RegisterRemoteRelayAsync(relay.RelayIdentityId, RelayEndpoint.FromHostAndPort("127.0.0.1", relay.BoundPort))` when absent (`IRelayDirectory` has no `GetByIdAsync`), then `LinkIdentityAsync(relay.RelayIdentityId, PublicIdentityId, MailboxRoutingToken, MailboxDrainToken)` — passing the sim's own tokens keeps `GetMailboxTokensAsync` aligned with what `ProvisionMailboxAsync` registered relay-side. `RelayProvisioningCoordinator` calls it; `StartAsync` calls it once if `HomeRelay` is already set.
- Subscribe `IsListening` changes → `IActiveIdentitySet.Add`/`Remove(PublicIdentityId)` (R3 `Subscribe`, initial emit is a no-op `Add`), and `Remove` on `StopAsync`: paused personas go offline (scheduler cancels the stream, relay retains mail, pending-ingress spool replays on resume).
- `DrainMailboxAsync` remains the pull-now API for coordinators, but it is a **drain-completion wait**, purely observational: poll until `DrainScheduler.HasEstablishedSession(target)` is true and `IPendingIngressStore.ListByOwnerAsync` is empty and stable (~200ms), bounded at 15s, then flush the outbox and publish transcript/group signals. It must NOT process envelopes itself — any `ProcessPersistedAsync`/`ReceiveAsync`/unary pull here is a second consumer on the queue or spool and double-processes (the unary `DrainEnvelopesAsync` steals stream-delivered-unacked items; an external replay races the stream's in-flight `ReceiveAsync` on the same row — both produced ratchet `SKIPPED_KEY_NOT_FOUND` + `AssertZeroErrors` scenario failures). Single consumer = the scheduler stream; the unary RPC is no longer invoked anywhere (deleted in DRAIN-09).
- `DrainScheduler.HasEstablishedSession(MailboxDrainTarget)` (new public surface): true while the target's supervisor holds a connected stream (initial backlog pushed); false while queued/reconnecting/backing off — the drain-completion wait's liveness signal.
- Simulator limitation (deliberate, deferred): sim personas are 1:1 with relays (`HomeRelay`, `AssignHomeRelayAsync`, one `MailboxRoutingToken`/`DeliveryToken`/`DrainToken` triad per persona). `DrainScheduler`/`IRelayDirectory` are already N:M — the lift is sim-only: per-relay token triads (sharing one routing token across relays is a sender-side correlation vector), `HomeRelay`→collection, `AssignHomeRelayAsync`→`AssignRelayAsync`/`HomeRelays`, `DrainMailboxAsync`/`ProvisionMailboxAsync`/`PublishProfileAsync` per-relay or all-relays, `RelayProvisioningCoordinator` multi-assign. DRAIN-09's sim drain surface should already be shaped per-relay (`DrainMailboxAsync(relay?)`) so it isn't designed around the singleton again.
- Preserve the `services.AddScoped(typeof(PublicIdentityId?), _ => PublicIdentityId)` registration (added in DRAIN-04 — the Type-based overload is required since `AddScoped<TService>` is `class`-constrained): it feeds `UnauthenticatedDeliveryEndpoint.localRecipientId`, and without it `DeliverDirect` silently skips unwrap→ingress on persona hosts.

**Desktop handler composition (DRAIN-05 outcome — shipped):** `IInboundIngressPipeline` and the `AppRouter` enumerable factory resolve from shared `AddTransportInfrastructure`; `AddDesktopSessionServices` now registers `AddChatApp()` verbatim plus a manual file-transfer dispatch surface (`FileTransferPlugin` singleton, `FileTransferPayloadHandler` **scoped**, `NullTransferStreamPool`). `AddFileTransferApp` must NOT be called verbatim on the Desktop session: `FileTransferPayloadHandler`/`FileTransferIngressFilter` are singletons with scoped repo deps (captive under `ValidateScopes`), and the filter's `IsEnabled` default of `false` would discard every FT payload — no filter is registered on Desktop until the privacy-toggle work lands (fix the singleton lifetimes upstream when it does).

**Verified:** shipped — `DrainSchedulerTests` 9/9 (reconcile on activate/deactivate/unlink, FIFO cap admission, `MaxSessionAge` rotation with in-flight envelope completing on the target token, per-target backoff isolation, periodic-timer reconcile) + `DependencyInjectionTests` hosted-service swap + Application.Tests 127/127 + Infrastructure.Tests 264/264 + **scenario suite 8/8**. Two two-consumer races found and fixed during verification: (1) a fast-faulting supervisor completed before `AdmitReadyTargets` registered it — ghost `_supervisors` entry blocked re-enqueue and filled the cap (fixed by `task.IsCompleted` guard + duplicate-due-entry drop); (2) the coordinator unary `DrainMailbox` RPC and an external spool replay both raced the live stream — `DrainMailboxAsync` became observational (see above). `MailboxBindingChangedEvent` publish on link/unlink/create-local shipped here — required for the scheduler to see post-activation links before the periodic tick; DRAIN-11's remaining work is the `RelayDirectoryServiceTests` publish assertions. `HasEstablishedSession` added for the drain-completion wait.

**QMS:** `SYS-PRE-ACTIVATION-STEALTH`, `DEP-DESKTOP-PRE-ACTIVATION-STEALTH`, `INV-DESKTOP-STEALTH-ACTIVATION-GATE`, `SYS-ASYNC-STORE-AND-FORWARD`, `DEP-INFRA-CONCURRENCY-MODEL`, `SYS-BOUNDED-RELAY-QUOTAS`, `HAZ-INGRESS-RESOURCE-EXHAUSTION`, `SYS-LOCK-TEARDOWN-AND-ZEROIZATION`, `DEP-DESKTOP-SESSION-LOCK-ZEROIZATION`, `HAZ-POST-LOCK-MEMORY-RESIDUE`.

---

### DRAIN-07 ✅ — Ensure-provisioned on first target connect + first-connect prekey seed

**Files:**
- Modified `Percolator.Application/Delivery/Ports/IRelayDirectory.cs` — added `EnsureMailboxProvisionedAsync(relayIdentityId, ownerIdentityId, ct)`.
- Modified `Percolator.Application/Delivery/RelayDirectoryService.cs` — `EnsureMailboxProvisionedAsync` impl (RELAY_NOT_FOUND / missing-binding errors, 24h `ProvisioningFreshnessWindow` skip, `ProvisionMailboxAsync` + `MarkMailboxProvisioned` + save); `ProvisionLinkedMailboxesAsync` count `25` → `100`.
- Modified `Percolator.Domain/Delivery/Model/RelayMailboxBinding.cs` + `RelayRegistration.cs` — `MailboxProvisionedUtc` + `MarkMailboxProvisioned`.
- Modified `Percolator.Infrastructure/Persistence/Entities/RelayEntities.cs` + `Repositories/SqliteRelayRegistrationRepository.cs` — timestamp mapping.
- Modified `Percolator.Application/Delivery/DrainScheduler.cs` — supervisor order: `EnsureMailboxProvisionedAsync` → `GetMailboxTokensAsync` → first-connect prekey block → `ReplayPendingAsync` → stream.
- Modified `Percolator.Infrastructure/Transport/GrpcRelayMailboxProvisioner.cs` — impl default `newOneTimePreKeyCount` → `100`; `_opkAllocationGate` `SemaphoreSlim` serializes `GetNextOneTimePreKeyIdAsync` → generate → `StoreOneTimePreKeysAsync` (concurrent supervisors previously collided on `vault_onetime_prekeys` UNIQUE — scenario replay caught it).
- Modified `Percolator.Domain/Delivery/Hosting/RelayHostedPreKeyBundle.cs` — `ReplenishOneTimePreKeys` is newest-wins within the cap: overflow evicts oldest unconsumed keys (a full bucket must not wedge the bundle at a stale key set — publishers allocate fresh ids per batch); a single batch exceeding the cap still returns `PREKEY_QUOTA_EXCEEDED`.
- Modified `Percolator.Simulator/Features/RelayedChat/RelayProvisioningCoordinator.cs` — awaits `EnsureMailboxProvisionedAsync` before the prekey admission gate + visibility registration; initial signed-prekey publication (0 OPKs) retained for peer-directory admission.
- Modified `Percolator.Simulator/Scenarios/02-relayed-stealth-and-chat.json` (`24` → `99`), `03-prekey-depletion.json` (`49` → `100`) — hosted-OPK expectations rebased onto the seeded bucket.

- `IRelayMailboxProvisioner.PublishPreKeyBundleAsync` interface default was already `100`; all publish sites now use it (interface default, impl default, `ProvisionLinkedMailboxesAsync` explicit arg).
- Supervisor first-connect block (`_firstActivationSeen` per target): `GetPreKeyStatusAsync` → publish 100 OPKs when `Found == false` **or** when the status call fails (`RELAY_PREKEY_STATUS_UNAVAILABLE` until DRAIN-10 lands the RPC — failure = unknown bundle state, publish anyway; the merge is idempotent). Publish failure unmarks the target so the backoff retry re-runs the seed. This guarantees `VaultUnlockViewModel.TryAutoStartLaunchAsync` provisions and publishes identically to the gate path.
- `EnsureMailboxProvisionedAsync` is a **durable** provision boundary (persisted `MailboxProvisionedUtc`, 24h freshness), stronger than the per-session call originally spec'd — safe across restarts and repeated supervisor connects.

**Verified:** `DrainSchedulerTests` — ensure-order/retry/rotation + first-connect publishes on `Found == false` and on status failure, skips on `Found`, status checked only on first connect across rotation; `RelayDirectoryServiceTests` — ensure errors/fresh-skip/stale-reprovision/retry + bulk count 100; `RelayHostedPreKeyBundleTests` — evict-oldest + oversized-batch rejection. Domain.Tests 206/206, Application.Tests 139/139, Infrastructure.Tests 264/264, **scenario suite 8/8**. Two defects found by replay and fixed: (1) concurrent `PublishPreKeyBundleAsync` OPK-id collision → allocation gate; (2) seeded bucket overflowing merge publishes → evict-oldest replenish.

**QMS:** `SYS-ASYNC-SESSION-INIT`, `HAZ-PREKEY-POOL-STARVATION`.

---

### DRAIN-08 ✅ — `ConnectMailboxStream` server hardening

**Files:**
- Modify `Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs`
- Modify `Percolator.Infrastructure.Tests/Transport/Endpoints/AuthenticatedRelayEndpointTests.cs`

1. Replace `Channel.CreateUnbounded<QueuedSealedEnvelopeProto>()` at L150 with `Channel.CreateBounded<QueuedSealedEnvelopeProto>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait })`.
2. Fix data race on `initialQueueIds`: populate the `HashSet<string>` completely from `queuedEnvelopes` *before* starting `liveReadTask` (keeping the subscription registered before `GetEnvelopesAsync` so no live enqueue is missed, while `initialQueueIds` is immutable by the time `liveReadTask` reads it).
3. In the ACK loop (L199): only invoke `AcknowledgeEnvelopeAsync` when `msg.Ack.Acknowledged && !string.IsNullOrEmpty(msg.Ack.QueueId)`.
4. Dedupe `OneTimePrekeyProto.KeyId` within the incoming `PublishPreKeyBundleRequest` batch — the existing filter only rejects ids already hosted, so a malformed publish enqueues the same key id twice and `ConsumeBundle` could serve a consumed id to a second fetcher. Self-harm only (the publisher owns the bundle), but it's a cheap endpoint check: reject or collapse duplicate ids before calling `PublishHostedBundle`/`ReplenishHostedBundle`.

**Verify:** `AuthenticatedRelayEndpointTests` — bounded live-channel backpressure, concurrent enqueue during initial replay delivered once, `Acknowledged = false` / empty `queue_id` ignored, batch containing duplicate `key_id`s rejected/deduped.

**Verified:** `EnvelopeArrivingDuringInitialReplay_DeliveredExactlyOnce` (gated-snapshot decorator holds `GetEnvelopesAsync` so an enqueue lands in both snapshot and live channel), `LiveEnqueueFloodBeyondCapacity_NoEnvelopeLost` (300 > 256 bound; dropped notifications redeliver on reconnect replay), `MalformedAcks_DoNotDeleteEnvelopes`, `PublishPreKeyBundle_DuplicateKeyIdsWithinBatch_CollapsedToUnique` (collapse-keep-first — spec allowed reject *or* collapse; collapse matches the idempotent merge). Endpoint tests 13/13; Infrastructure.Tests 268/268.

**Deviation carried forward to DRAIN-09:** `FullMode = Wait` does not engage — all `SubscribeMailbox` implementations notify via `TryWrite`, which never waits. Deliberate contract: store persists before notify, so a full channel drops only the live push; the envelope still arrives on the next connect's replay.

**QMS:** `DEC-BOUNDED-CHANNEL-EVENT-BUS`, `HAZ-INGRESS-RESOURCE-EXHAUSTION`.

---

### DRAIN-09 ✅ — Remove destructive unary `DrainMailbox` & migrate callers

**Files:**
- Modify `Percolator.Infrastructure/Protos/authenticated_relay.proto` (delete `rpc DrainMailbox`, `DrainMailboxRequest`, `DrainMailboxResponse`)
- Modify `Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs` (delete `DrainMailbox` override)
- Modify `Percolator.Infrastructure/Transport/GrpcRelayMailboxProvisioner.cs` (delete `DrainMailboxOnceAsync` — kept since DRAIN-02 as the simulator bridge — plus `_scopeFactory`, `_timeProvider`, `EnvelopeExpiryWindow`, and now-unused usings)
- Move `Percolator.Infrastructure/Transport/IRelayMailboxCapabilityStore.cs` → `Percolator.Domain/Delivery/Ports/IRelayMailboxCapabilityStore.cs` — it is consumed by `ExchangeDirectoryService`/`SimulatedPersonaHost` and is a Domain port, not an infra detail. De-proto its contract at the same time: `EnqueueEnvelopeAsync`/`GetEnvelopesAsync`/`DrainEnvelopesAsync`/`SubscribeMailbox` replace `QueuedSealedEnvelopeProto` with a new `RelayQueuedEnvelopeRecord(MailboxQueueId QueueId, ReadOnlyMemory<byte> EnvelopePayload, DateTimeOffset EnqueuedAtUtc)` VO; token params become `BlindedRoutingToken`/`DeliveryToken`; `string queueId` becomes `MailboxQueueId`; `DrainEnvelopesAsync` → `PurgeMailboxAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)` returning `ValueTask<int>`. `SqliteRelayMailboxCapabilityStore` and `AuthenticatedRelayEndpoint` map proto↔VO at the gRPC boundary. Preserve the DRAIN-08 notification contract when de-protoing `SubscribeMailbox`: subscriber notification stays `TryWrite` (persist-first, best-effort live push — do not change it to `WriteAsync`, which would block `EnqueueEnvelopeAsync` on a stalled drain stream).
- Modify `Percolator.Infrastructure/Persistence/Repositories/SqliteRelayMailboxCapabilityStore.cs` (`PurgeMailboxAsync` uses `ExecuteDeleteAsync` directly without materializing payloads)
- Modify `Percolator.Desktop/Common/Services/ExchangeDirectoryService.cs` (`UnlinkPersonaAsync` L195: call `_capabilities.PurgeMailboxAsync`)
- Modify `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` (small cleanup only — the DRAIN-02 `is GrpcRelayMailboxProvisioner` bridge was already removed in DRAIN-06: `DrainMailboxAsync` no longer reads the relay queue at all; it is a drain-completion wait on the scheduler's stream + spool, which fixed the two-consumer unacked-item race). When the sim goes multi-relay (limitation noted in DRAIN-06), reshape this surface per-relay (`DrainMailboxAsync(relay?)`). Preserve the `PublicIdentityId?` scoped registration (constraint spec'd in DRAIN-06).
- Modify `Percolator.Infrastructure.Tests/Fakes/FakeRelayMailboxCapabilityStore.cs`, `Percolator.Infrastructure.Windows.Tests/Fakes/FakeRelayMailboxCapabilityStore.cs`, `Percolator.Infrastructure.Tests/Persistence/SqliteRelayMailboxCapabilityStoreTests.cs`, `Percolator.Infrastructure.Tests/Transport/Endpoints/AuthenticatedRelayEndpointTests.cs`, `Percolator.Infrastructure.Tests/Protos/CoreProtoCompilationTests.cs`

- Note: `RelayMailboxQueue` (in-memory domain aggregate) and `TestIdentityNode.DrainMailboxAsync` remain unchanged — they back in-memory `Percolator.ApplicationIntegrationTests` and do not touch gRPC or SQLite.

**Verify:** unit tests + full scenario suite (`--replay Scenarios`) — sim `DrainMailboxAsync` is re-bridged here.

**Outcome / deviations (verified 2026-11-15):**
- `dotnet build Percolator.sln`: 0 errors; `dotnet test`: 12 assemblies, 1,059 tests, 0 failures; `--replay Scenarios`: ALL 8 PASSED.
- Behavior change beyond spec — malformed token lengths now fail fast: `RegisterMailbox`/`EnqueueMailbox`/`FetchPreKeyBundle` require exact 16/32-byte tokens (`InvalidArgument`; were `IsEmpty`-only checks), `ConnectMailboxStream` treats a wrong-length routing token as auth failure, and unparsable ACK queue ids are ignored. Required by VO construction — `FromBytes`/`FromSpan` throw on wrong length.
- Spec'd files needing no change: `CoreProtoCompilationTests` (never referenced the drain messages). `AnonymousGroupRelayEndpointTests` inspect held envelopes via `GetEnvelopesAsync` — the destructive read no longer exists.
- Simulator decorator signal renamed `EnvelopesDrained` → `MailboxPurged`, emitted only when count > 0.

**QMS:** `HAZ-CRASH-STATE-DESYNC`, `DEC-PENDING-INGRESS-SPOOL`.

---

### DRAIN-10 ✅ — Prekey status RPC, OPK low-watermark replenishment & 48h SPK rotation

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
- Create `Percolator.Application/Delivery/PreKeyMaintenanceService.cs` (implements `IPreKeyMaintenanceService` — the port shipped in DRAIN-05; replace the `NullPreKeyMaintenanceService` singleton registration in `AddTransportInfrastructure`)
- Create `Percolator.Application.Tests/Delivery/PreKeyMaintenanceServiceTests.cs`
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

- Server handler: drain-token authenticated; `found=false` when no hosted bundle exists for the routing token + device. Follow the DRAIN-09 boundary convention: length-validate wire tokens (16-byte routing / 32-byte drain → `InvalidArgument`), construct `BlindedRoutingToken`/`DeliveryToken`, then `VerifyDrainTokenAsync(BlindedRoutingToken, DeliveryToken)` on the domain port. Replace the `RELAY_PREKEY_STATUS_UNAVAILABLE` stub added in DRAIN-02 inside `GrpcRelayMailboxProvisioner.GetPreKeyStatusAsync` with the real call; map `found=false` to `Success(new RelayPreKeyStatus(false, 0, new SignedPreKeyId(0)))`; `found=true` maps `signed_prekey_id` into the `SignedPreKeyId` VO (shipped in DRAIN-02).

**DRAIN-07 shipped outcomes that affect this ticket:**
- `DrainScheduler`'s first-connect block already calls `GetPreKeyStatusAsync` and treats failure as publish — once the real RPC lands the seed starts skipping when `Found` with **no scheduler change**; verify the existing `DrainSchedulerTests` still pass and adjust the status-failure test expectations only if the stub is removed.
- `RelayHostedPreKeyBundle.ReplenishOneTimePreKeys` is **newest-wins within the cap** since DRAIN-07 (overflow evicts oldest unconsumed keys; only a single batch > cap returns `PREKEY_QUOTA_EXCEEDED`). `PreKeyMaintenanceService` top-up publishes therefore can't be wedged by a full bucket — `toGenerate` may be published as-is without headroom math.
- `GrpcRelayMailboxProvisioner._opkAllocationGate` (`SemaphoreSlim`) serializes `GetNextOneTimePreKeyIdAsync` → generate → `StoreOneTimePreKeysAsync` since DRAIN-07; replenishment publishes share the same path and are already safe from OPK-id collisions. If a per-owner/device gate is preferred over the instance-wide one, swap it in the provisioner — `IPrivatePreKeyStore` keeps its split allocate/store shape.

**2. End-to-end `SignedPreKeyId` & multi-SPK retention (Signal parity):**
- `VaultSignedPreKeyEntity`: change PK from `(OwnerIdentityId, DeviceId)` to `(OwnerIdentityId, DeviceId, KeyId)`; add `uint KeyId { get; set; } = 1;`, `bool IsCurrent { get; set; } = true;`.
- `RelayPreKeyEntity`: add `uint SignedPreKeyId { get; set; } = 1;`.
- `RelayHostedPreKeyBundle`: add `SignedPreKeyId SignedPreKeyId { get; private set; }` (passed to `Create`, `Rehydrate`, and `UpdateSignedPreKey(SignedPreKeyId signedPreKeyId, ...)`).
- `PreKeyBundle`: add `SignedPreKeyId SignedPreKeyId` parameter (default `SignedPreKeyId.First` — key id 1; add the `First` static to the DRAIN-02 VO if absent); `UnauthenticatedDeliveryEndpoint.FetchPreKeyBundle` sets `SignedPrekeyId = consumed.SignedPreKeyId` (replaces hardcoded `1`); `PreKeyBundleProviderAdapter` maps `response.SignedPrekeyId`; `HandshakeService.InitiateHandshakeAsync` L149 uses `remoteBundle.SignedPreKeyId` (replaces hardcoded `1`); `HandshakeService.ProcessActiveInvitationAsync` L267 resolves the private key by `envelope.SignedPreKeyId`.
- `IPrivatePreKeyStore`:

```csharp
public sealed record StoredSignedPreKey(
    SignedPreKeyId KeyId,
    EphemeralPrivateKey PrivateKey,
    DhPublicKey PublicKey,
    DeviceLinkProof Signature,
    DateTimeOffset CreatedAtUtc);

public interface IPrivatePreKeyStore
{
    Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId, DeviceId deviceId, SignedPreKeyId? signedPreKeyId = null, CancellationToken ct = default);
    Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        PublicIdentityId ownerId, DeviceId deviceId, CancellationToken ct = default); // returns IsCurrent == true
    Task<SignedPreKeyId> StoreSignedPreKeyAsync(
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

**Verify:** `PreKeyMaintenanceServiceTests` (12h throttle vs forced evaluation, top-up `<= 35` → `100`, 48h SPK rotation + 7d/min-3 prune, SPK-mismatch republish) + `AuthenticatedRelayEndpointTests` (`GetPreKeyStatus` auth & counts) + updated `IPrivatePreKeyStore` test doubles compile.

**Outcome / deviations:**
- Verified: `dotnet build` green; all unit suites pass (incl. new `PreKeyMaintenanceServiceTests`, `GetPreKeyStatus` endpoint tests, multi-SPK `SqlitePrivatePreKeyStoreTests`); `--replay Scenarios`: ALL 8 SCENARIOS PASSED.
- `PreKeyBundle.SignedPreKeyId` is a **required** positional parameter (not defaulted as spec'd); wire-side callers normalize `signed_prekey_id == 0` → `SignedPreKeyId.First` for legacy bundles.
- `lastChecked` is recorded only after the status RPC reaches the relay (spec step 5 recorded unconditionally) — token/status failures retry on the next evaluation rather than being throttled away.
- **No legacy-schema upgrade path shipped**: an `ApplyPreKeySchemaUpgradesAsync` ALTER path was built then deliberately deleted — no data-bearing vaults exist, and hand-rolled upgrades proved fragile (ordering bugs). Old-schema vaults fail fast at `DATABASE_MIGRATION_FAILED` (create-script index can't build on legacy columns) — delete and recreate. Schema evolution becomes a real decision the first time a live vault exists.
- **In-memory store doubles must clone `EphemeralPrivateKey`**: `Dispose()` zeroizes the shared byte array, so doubles storing/returning the same object corrupt on caller disposal (handshake `using`, maintenance finally-block, provisioner cleanup). All four doubles now clone on store and on shared-return paths, mirroring `SqlitePrivatePreKeyStore`'s byte-copy semantics.
- **Pre-delete zeroization requires UPDATE-then-DELETE**: EF issues `DELETE`-only on `Remove`, so clearing tracked entity bytes never reaches the file — freed cells kept ciphertext/keys. Both `PruneSignedPreKeysAsync` and `SqlitePendingIngressStore.DeleteAsync` now `ExecuteUpdate` the payload to empty before `Remove` (tracked copy also zeroed). Any future delete-with-zeroization must follow the same two-step.

**QMS:** `HAZ-PREKEY-POOL-STARVATION`, `SYS-ATOMIC-STATE-PERSISTENCE`, `DEP-INFRA-VOLATILE-ZEROIZATION`.

---

### DRAIN-11 ✅ — `MailboxBindingChangedEvent` publish on link/unlink

**Files:**
- Modify `Percolator.Application/Delivery/RelayDirectoryService.cs`
- Modify `Percolator.Application.Tests/Delivery/RelayDirectoryServiceTests.cs`

- The `IEventPublisher` ctor injection and all three publish sites (`LinkIdentityAsync`, `UnlinkIdentityAsync`, `CreateLocalRelayHostAsync` per initial owner) **shipped in DRAIN-06** — the scheduler could not see post-activation links without them and scenario runs proved it (binding changes previously reached the scheduler only via the 60s periodic tick). Remaining work here is test coverage only: assert `MailboxBindingChangedEvent` published with correct `Kind` (`Linked`/`Unlinked`) on each site in `RelayDirectoryServiceTests` (a `FakeEventPublisher` exists under `TestDoubles`).

**Verify:** `RelayDirectoryServiceTests` — event published with correct `Kind` on `LinkIdentityAsync`, `UnlinkIdentityAsync`, and per initial owner in `CreateLocalRelayHostAsync`.

**Outcome:** Publish sites shipped in DRAIN-06; review found they emitted events (and `SaveAsync` writes) on *unchanged* bindings — fixed upstream in `RelayDirectoryService`:
- `LinkIdentityAsync` and `UnlinkIdentityAsync` compare binding presence before/after the (documented idempotent/no-op) domain calls — save + publish only fire when a binding was actually added/removed. Re-link and unlink-of-unlinked now emit nothing.
- `CreateLocalRelayHostAsync` dedups `initialLinkedOwnerIds` — a repeated owner id no longer yields two `Linked` events / a dup `LinkedOwnerIdentityIds` entry.
- **Contract pinned**: `MailboxBindingChangedEvent` = *fact that a binding changed*, never an idempotent hint. Consumers may treat `Unlinked` as a delta (binding gone), not just a wake-up. `DrainScheduler` today only `Wake()`s on any `Kind` — unaffected.
- Tests: 5 new in `RelayDirectoryServiceTests` (per-owner Linked on create, Linked on link, Linked→Unlinked ordering, no event on re-link, no event on unlink-of-unlinked) + no-event guard on `LinkIdentityAsync_UnknownRelay`. 19 passing; Application.Tests 155, ApplicationIntegrationTests 19 — all green.
- Residual: `RelayRegistration.RotateDrainToken` mutates a binding with no event — no production caller today; if token rotation becomes observable to consumers, `Kind` needs a `TokensRotated` variant.

**QMS:** `INV-APP-EVENT-BUS-DECOUPLING`.

