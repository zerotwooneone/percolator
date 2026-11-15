using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Events;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Session;
using Percolator.Application.Session.Ports;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.Delivery;

[TestFixture]
public sealed class DrainSchedulerTests
{
    private FakeEventBus _bus = null!;
    private ActiveIdentitySet _active = null!;
    private FakeRelayDirectory _directory = null!;
    private FakeMailboxClient _mailboxClient = null!;
    private RecordingIngressStore _store = null!;
    private FakeMailboxProvisioner _provisioner = null!;
    private FakeServiceProvider _scopeProvider = null!;
    private TestDateTimeProvider _time = null!;
    private DrainSchedulerOptions _options = null!;
    private DrainScheduler _scheduler = null!;

    private static readonly PublicIdentityId OwnerA = PublicIdentityId.New();
    private static readonly PublicIdentityId OwnerB = PublicIdentityId.New();
    private static readonly PublicIdentityId Relay1 = PublicIdentityId.New();
    private static readonly PublicIdentityId Relay2 = PublicIdentityId.New();

    private static RelayEndpoint Endpoint(int port) => RelayEndpoint.FromHostAndPort("127.0.0.1", port);

    private static RelayDirectoryEntry Entry(PublicIdentityId relay, int port, params PublicIdentityId[] owners) =>
        new(relay, $"relay-{port}", Endpoint(port), IsLocalRelayHost: false, owners);

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeEventBus();
        _directory = new FakeRelayDirectory();
        _mailboxClient = new FakeMailboxClient();
        _store = new RecordingIngressStore();
        _provisioner = new FakeMailboxProvisioner();
        _time = new TestDateTimeProvider();
        _options = new DrainSchedulerOptions
        {
            MaxConcurrentSessions = 2,
            MaxSessionAge = TimeSpan.FromMinutes(5),
            ReconcileInterval = TimeSpan.FromSeconds(10),
            InitialBackoff = TimeSpan.FromMilliseconds(40),
            MaxBackoff = TimeSpan.FromSeconds(5),
            BackoffMultiplier = 2.0,
        };
        _active = new ActiveIdentitySet(_bus, NullLogger<ActiveIdentitySet>.Instance);

        var drainService = new MailboxDrainService(
            _store,
            new AlwaysOkUnwrapper(),
            new AlwaysOkIngress(),
            new NullPreKeyMaintenanceService(),
            _time,
            NullLogger<MailboxDrainService>.Instance);

        _scopeProvider = new FakeServiceProvider(new Dictionary<Type, object>
        {
            [typeof(IRelayDirectory)] = _directory,
            [typeof(IRelayMailboxClient)] = _mailboxClient,
            [typeof(IRelayMailboxProvisioner)] = _provisioner,
            [typeof(IPreKeyMaintenanceService)] = new NullPreKeyMaintenanceService(),
            [typeof(MailboxDrainService)] = drainService,
        });

        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, _time, _options, NullLogger<DrainScheduler>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _scopeProvider.Dispose();
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Reconcile_StartsDrain_ForActiveLinkedOwner()
    {
        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");
        _mailboxClient.Opened[0].Should().Be(Endpoint(5001));
        _scheduler.HasEstablishedSession(new MailboxDrainTarget(OwnerA, Relay1, Endpoint(5001)))
            .Should().BeTrue("connected stream marks the session established");

        // Replay happens before the stream opens (persist-then-ack spool drains first).
        _store.CallOrder.Should().StartWith("list");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task Reconcile_Ignores_UnlinkedOwners_AndInactiveIdentities()
    {
        // OwnerB linked but not active; OwnerA active but not linked anywhere.
        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerB));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await Task.Delay(200);
        _mailboxClient.Opened.Should().BeEmpty();

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task PersonaActivated_EventTriggersReconcile_StartsDrain()
    {
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);
        await Task.Delay(100);
        _mailboxClient.Opened.Should().BeEmpty();

        _active.Add(OwnerA); // publishes PersonaActivatedEvent → reconcile wake

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened after activation");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task PersonaDeactivated_TearsDownStream_WithoutRequeue()
    {
        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);
        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");
        var session = _mailboxClient.Sessions[0];

        _active.Remove(OwnerA); // deactivation → desired set empty → supervisor cancelled

        await WaitForAsync(() => session.DisposeCount > 0, "session torn down");
        await Task.Delay(150);
        _mailboxClient.Opened.Count.Should().Be(1, "removed target must not re-enqueue");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task BindingChanged_Unlink_StopsDrain()
    {
        _active.Add(OwnerA);
        var entry = Entry(Relay1, 5001, OwnerA);
        _directory.AddEntry(entry);

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);
        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");
        var session = _mailboxClient.Sessions[0];

        _directory.ClearEntries();
        await _bus.PublishAsync(new MailboxBindingChangedEvent(OwnerA, Relay1, MailboxBindingChangeKind.Unlinked));

        await WaitForAsync(() => session.DisposeCount > 0, "session torn down after unlink");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task Admission_HonorsMaxConcurrentSessions_ThenAdmitsOnSlotFree()
    {
        _options = _options with { MaxConcurrentSessions = 1 };
        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, _time, _options, NullLogger<DrainScheduler>.Instance);
        _active.Add(OwnerA);
        _active.Add(OwnerB);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));
        _directory.AddEntry(Entry(Relay1, 5002, OwnerB));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "first stream opened");
        await Task.Delay(150);
        _mailboxClient.Opened.Count.Should().Be(1, "session cap must hold at one supervisor");

        // First session ends normally → slot frees → queued target admits (FIFO).
        _mailboxClient.Sessions[0].Complete();

        await WaitForAsync(() => _mailboxClient.Opened.Count >= 2, "second target admitted after slot freed");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task MaxSessionAge_Rotation_Reconnects_AndInFlightEnvelopeCompletes()
    {
        _options = _options with { MaxSessionAge = TimeSpan.FromMilliseconds(60) };
        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, _time, _options, NullLogger<DrainScheduler>.Instance);
        _store.UpsertGate = new TaskCompletionSource<PendingIngressRecord>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);
        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");
        var session = _mailboxClient.Sessions[0];

        // An envelope in flight when the session-age cap fires must still complete:
        // ReceiveAsync runs on the target token, not the session token.
        var queued = new QueuedMailboxEnvelope(
            MailboxQueueId.Parse("q-age"),
            new MailboxEnvelope(
                EnvelopeId.New(), BlindedRoutingToken.New(),
                new byte[] { 1 }, _time.UtcNow, _time.UtcNow.AddDays(1)));
        session.Push(queued);
        await WaitForAsync(() => _store.UpsertCalls == 1, "envelope upsert started");

        await Task.Delay(120); // MaxSessionAge elapses while ReceiveAsync is blocked
        _store.ReleaseUpsert();

        await WaitForAsync(() => session.Acks.Contains("q-age"), "in-flight envelope acked");
        await WaitForAsync(() => _mailboxClient.Opened.Count >= 2, "reconnect after rotation");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task StreamFault_RequeuesWithBackoff_WithoutAffectingOtherTargets()
    {
        _options = _options with { InitialBackoff = TimeSpan.FromMilliseconds(40) };
        // Live clock: backoff requeue times must elapse in real time (the frozen
        // TestDateTimeProvider would leave dueAt permanently in the future).
        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, new LiveDateTimeProvider(), _options, NullLogger<DrainScheduler>.Instance);
        _active.Add(OwnerA);
        _active.Add(OwnerB);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));
        _directory.AddEntry(Entry(Relay2, 5002, OwnerB));

        _mailboxClient.Override = endpoint => endpoint.Port == 5002
            ? DomainResult<IMailboxStreamSession>.Failure(new DomainError("STREAM", "refused"))
            : null;

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(
            () => _mailboxClient.Opened.Count(e => e.Port == 5002) >= 2,
            $"faulted target retried (opens5002={_mailboxClient.Opened.Count(e => e.Port == 5002)}, total={_mailboxClient.Opened.Count})");
        await WaitForAsync(
            () => _mailboxClient.Opened.Count(e => e.Port == 5001) == 1,
            $"healthy target opened once (opens5001={_mailboxClient.Opened.Count(e => e.Port == 5001)})");

        var retries = _mailboxClient.OpenTimesFor(5002);
        (retries[1] - retries[0]).Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(30), "retry respects InitialBackoff");

        // Healthy target stays connected — no churn from the faulted neighbour.
        await Task.Delay(150);
        _mailboxClient.Opened.Count(e => e.Port == 5001).Should().Be(1);

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task PeriodicTimer_Reconciles_WhenBindingAppearsWithoutEvent()
    {
        _options = _options with { ReconcileInterval = TimeSpan.FromMilliseconds(60) };
        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, _time, _options, NullLogger<DrainScheduler>.Instance);
        _active.Add(OwnerA);

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);
        await Task.Delay(100);
        _mailboxClient.Opened.Should().BeEmpty();

        // Binding appears with no event — the periodic tick must pick it up.
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened on periodic reconcile");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task EnsureProvisioned_RunsBeforeStreamOpen()
    {
        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");

        _directory.EnsureCallCount.Should().Be(1);
        _directory.EnsureTimes()[0].Should().BeBefore(_mailboxClient.OpenTimesFor(5001)[0],
            "provision-on-connect precedes the stream open");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task EnsureProvisionedFailure_RequeuesWithBackoff_AndNoStream()
    {
        _options = _options with { InitialBackoff = TimeSpan.FromMilliseconds(40) };
        // Live clock: backoff requeue times must elapse in real time.
        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, new LiveDateTimeProvider(), _options, NullLogger<DrainScheduler>.Instance);

        var calls = 0;
        _directory.EnsureOverride = _ => Interlocked.Increment(ref calls) <= 2
            ? DomainResult.Failure(new DomainError("PROVISION_FAILED", "relay refused"))
            : DomainResult.Success();

        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _directory.EnsureCallCount >= 3, "provision retried");
        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opens after provision succeeds");

        var attempts = _directory.EnsureTimes();
        attempts.Should().HaveCountGreaterThanOrEqualTo(3);
        (attempts[1] - attempts[0]).Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(30), "failed provision retries respect InitialBackoff");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task EnsureProvisioned_CalledPerConnect_AcrossSessionRotation()
    {
        _options = _options with { MaxSessionAge = TimeSpan.FromMilliseconds(60) };
        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, _time, _options, NullLogger<DrainScheduler>.Instance);

        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count >= 2, "rotation reconnects");
        _directory.EnsureCallCount.Should().BeGreaterThanOrEqualTo(2,
            "every connect re-checks the provisioned mark; idempotency lives in the service");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task FirstConnect_BundleNotFound_PublishesPreKeyBundle()
    {
        _provisioner.StatusOverride = DomainResult<RelayPreKeyStatus>.Success(
            new RelayPreKeyStatus(false, 0, SignedPreKeyId.None));

        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");

        _provisioner.Publications().Should().ContainSingle()
            .Which.OpkCount.Should().Be(100, "first-connect seeds a full OPK batch");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task FirstConnect_StatusCheckFails_PublishesAnyway()
    {
        _provisioner.StatusOverride = DomainResult<RelayPreKeyStatus>.Failure(
            new DomainError("RELAY_PREKEY_STATUS_UNAVAILABLE", "status RPC not implemented"));

        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");

        _provisioner.Publications().Should().ContainSingle(
            "a failed status check means unknown bundle state — publish merges OPKs idempotently");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task FirstConnect_BundleFound_SkipsPublish()
    {
        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count == 1, "stream opened");

        _provisioner.StatusCallCount.Should().Be(1);
        _provisioner.PublicationCount.Should().Be(0, "a found bundle needs no seeding");

        cts.Cancel();
        await run;
    }

    [Test]
    public async Task PrekeySeed_ChecksOnlyOnFirstConnect()
    {
        _options = _options with { MaxSessionAge = TimeSpan.FromMilliseconds(60) };
        _scheduler = new DrainScheduler(
            _active, _bus, _scopeProvider, _time, _options, NullLogger<DrainScheduler>.Instance);

        _active.Add(OwnerA);
        _directory.AddEntry(Entry(Relay1, 5001, OwnerA));

        using var cts = new CancellationTokenSource();
        var run = _scheduler.RunAsync(cts.Token);

        await WaitForAsync(() => _mailboxClient.Opened.Count >= 2, "rotation reconnects");

        _provisioner.StatusCallCount.Should().Be(1,
            "the bundle seed runs once per session, not per connect");

        cts.Cancel();
        await run;
    }

    // ---------- test doubles ----------

    private sealed class FakeEventBus : IEventBus
    {
        private readonly List<(Type Type, Func<IApplicationEvent, CancellationToken, ValueTask> Invoker)> _subs = new();

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
            where TEvent : IApplicationEvent
        {
            var entry = (typeof(TEvent), new Func<IApplicationEvent, CancellationToken, ValueTask>(
                (e, ct) => handler((TEvent)e, ct)));
            lock (_subs) { _subs.Add(entry); }
            return new ActionDisposable(() => { lock (_subs) { _subs.Remove(entry); } });
        }

        public IDisposable RegisterFaultHandler(Action<EventDispatchError> handler) =>
            new ActionDisposable(() => { });

        public async ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IApplicationEvent
        {
            List<Func<IApplicationEvent, CancellationToken, ValueTask>> handlers;
            lock (_subs)
            {
                handlers = _subs.Where(s => s.Type == typeof(TEvent)).Select(s => s.Invoker).ToList();
            }
            foreach (var handler in handlers)
            {
                await handler(@event, cancellationToken);
            }
        }
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _dispose;
        public ActionDisposable(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }

    private sealed class FakeRelayDirectory : IRelayDirectory
    {
        public List<RelayDirectoryEntry> Entries { get; } = new();
        public int TokenCalls { get; private set; }
        private readonly List<(PublicIdentityId RelayId, PublicIdentityId OwnerId, DateTimeOffset At)> _ensureCalls = new();
        public Func<(PublicIdentityId RelayId, PublicIdentityId OwnerId), DomainResult>? EnsureOverride { get; set; }

        public int EnsureCallCount
        {
            get { lock (_ensureCalls) { return _ensureCalls.Count; } }
        }

        public List<DateTimeOffset> EnsureTimes()
        {
            lock (_ensureCalls) { return _ensureCalls.Select(c => c.At).ToList(); }
        }

        public Task<DomainResult> EnsureMailboxProvisionedAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default)
        {
            lock (_ensureCalls) { _ensureCalls.Add((relayIdentityId, ownerIdentityId, DateTimeOffset.UtcNow)); }
            var result = EnsureOverride?.Invoke((relayIdentityId, ownerIdentityId)) ?? DomainResult.Success();
            return Task.FromResult(result);
        }

        public void AddEntry(RelayDirectoryEntry entry)
        {
            lock (Entries) { Entries.Add(entry); }
        }

        public void ClearEntries()
        {
            lock (Entries) { Entries.Clear(); }
        }

        public Task<IReadOnlyList<RelayDirectoryEntry>> ListAsync(CancellationToken ct = default)
        {
            lock (Entries)
            {
                IReadOnlyList<RelayDirectoryEntry> snapshot = Entries.ToList();
                return Task.FromResult(snapshot);
            }
        }

        public Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(
            PublicIdentityId ownerIdentityId, PublicIdentityId relayIdentityId, CancellationToken ct = default)
        {
            TokenCalls++;
            var tokens = new MailboxTokenTriad(
                BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom());
            return Task.FromResult(DomainResult<MailboxTokenTriad>.Success(tokens));
        }

        public Task<DomainResult<RelayDirectoryEntry>> CreateLocalRelayHostAsync(
            PublicIdentityId relayIdentityId, string displayName, RelayEndpoint endpoint,
            IEnumerable<PublicIdentityId> initialLinkedOwnerIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DomainResult<RelayDirectoryEntry>> RegisterRemoteRelayAsync(
            PublicIdentityId relayIdentityId, RelayEndpoint endpoint, CertificatePin? pinnedCertificate = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DomainResult<RelayMailboxBinding>> LinkIdentityAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId,
            BlindedRoutingToken? routingToken = null, DeliveryToken? drainToken = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DomainResult> UnlinkIdentityAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DomainResult> ProvisionLinkedMailboxesAsync(
            PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMailboxProvisioner : IRelayMailboxProvisioner
    {
        private readonly object _gate = new();
        private readonly List<(PublicIdentityId OwnerId, PublicIdentityId RelayId)> _provisions = new();
        private readonly List<(PublicIdentityId OwnerId, DeviceId DeviceId, uint OpkCount)> _publications = new();
        private int _statusCalls;

        public DomainResult<RelayPreKeyStatus>? StatusOverride { get; set; }

        public int StatusCallCount
        {
            get { lock (_gate) { return _statusCalls; } }
        }

        public int PublicationCount
        {
            get { lock (_gate) { return _publications.Count; } }
        }

        public List<(PublicIdentityId OwnerId, DeviceId DeviceId, uint OpkCount)> Publications()
        {
            lock (_gate) { return _publications.ToList(); }
        }

        public Task<DomainResult> ProvisionMailboxAsync(
            PublicIdentityId ownerIdentityId, PublicIdentityId relayIdentityId,
            RelayEndpoint endpoint, MailboxTokenTriad tokens, CancellationToken ct = default)
        {
            lock (_gate) { _provisions.Add((ownerIdentityId, relayIdentityId)); }
            return Task.FromResult(DomainResult.Success());
        }

        public Task<DomainResult> PublishPreKeyBundleAsync(
            PublicIdentityId ownerIdentityId, DeviceId deviceId, RelayEndpoint endpoint,
            MailboxTokenTriad tokens, uint newOneTimePreKeyCount, CancellationToken ct = default)
        {
            lock (_gate) { _publications.Add((ownerIdentityId, deviceId, newOneTimePreKeyCount)); }
            return Task.FromResult(DomainResult.Success());
        }

        public Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
            DeviceId deviceId, RelayEndpoint endpoint, MailboxTokenTriad tokens, CancellationToken ct = default)
        {
            lock (_gate) { _statusCalls++; }
            return Task.FromResult(StatusOverride
                ?? DomainResult<RelayPreKeyStatus>.Success(new RelayPreKeyStatus(true, 50, new SignedPreKeyId(1))));
        }
    }

    private sealed class FakeMailboxClient : IRelayMailboxClient
    {
        private readonly object _gate = new();
        private readonly List<(RelayEndpoint Endpoint, DateTimeOffset At)> _opens = new();
        public List<FakeStreamSession> Sessions { get; } = new();
        public Func<RelayEndpoint, DomainResult<IMailboxStreamSession>?>? Override { get; set; }

        public IReadOnlyList<RelayEndpoint> Opened
        {
            get { lock (_gate) { return _opens.Select(o => o.Endpoint).ToList(); } }
        }

        public List<DateTimeOffset> OpenTimesFor(int port)
        {
            lock (_gate) { return _opens.Where(o => o.Endpoint.Port == port).Select(o => o.At).ToList(); }
        }

        public Task<DomainResult<IMailboxStreamSession>> OpenMailboxStreamAsync(
            RelayEndpoint endpoint, MailboxTokenTriad tokens, CancellationToken ct = default)
        {
            lock (_gate) { _opens.Add((endpoint, DateTimeOffset.UtcNow)); }
            var result = Override?.Invoke(endpoint);
            if (result.HasValue)
            {
                return Task.FromResult(result.Value);
            }
            var session = new FakeStreamSession();
            lock (Sessions) { Sessions.Add(session); }
            return Task.FromResult(DomainResult<IMailboxStreamSession>.Success(session));
        }
    }

    private sealed class FakeStreamSession : IMailboxStreamSession
    {
        private readonly Channel<QueuedMailboxEnvelope> _channel = Channel.CreateUnbounded<QueuedMailboxEnvelope>();
        public List<string> Acks { get; } = new();
        public int DisposeCount;

        public void Push(QueuedMailboxEnvelope envelope) => _channel.Writer.TryWrite(envelope);
        public void Complete() => _channel.Writer.TryComplete();

        public IAsyncEnumerable<QueuedMailboxEnvelope> Envelopes => _channel.Reader.ReadAllAsync();

        public Task<DomainResult> AcknowledgeAsync(MailboxQueueId queueId, CancellationToken ct = default)
        {
            lock (Acks) { Acks.Add(queueId.Value); }
            return Task.FromResult(DomainResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingIngressStore : IPendingIngressStore
    {
        private readonly Dictionary<(Guid, string), PendingIngressRecord> _rows = new();
        public List<string> CallOrder { get; } = new();
        public int UpsertCalls { get; private set; }
        public TaskCompletionSource<PendingIngressRecord>? UpsertGate { get; set; }

        public async Task<PendingIngressRecord> UpsertAsync(
            PublicIdentityId ownerId, QueuedMailboxEnvelope envelope, CancellationToken ct = default)
        {
            lock (CallOrder) { CallOrder.Add("upsert"); UpsertCalls++; }
            if (UpsertGate != null)
            {
                return await UpsertGate.Task.WaitAsync(ct);
            }
            var record = new PendingIngressRecord(envelope.QueueId, envelope, 0, null);
            lock (_rows) { _rows[(ownerId.Value, envelope.QueueId.Value)] = record; }
            return record;
        }

        public void ReleaseUpsert()
        {
            var record = new PendingIngressRecord(
                MailboxQueueId.Parse("q-age"),
                new QueuedMailboxEnvelope(
                    MailboxQueueId.Parse("q-age"),
                    new MailboxEnvelope(
                        EnvelopeId.New(), BlindedRoutingToken.New(),
                        new byte[] { 1 }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))),
                0, null);
            UpsertGate!.TrySetResult(record);
        }

        public Task<IReadOnlyList<PendingIngressRecord>> ListByOwnerAsync(
            PublicIdentityId ownerId, CancellationToken ct = default)
        {
            lock (CallOrder) { CallOrder.Add("list"); }
            IReadOnlyList<PendingIngressRecord> rows;
            lock (_rows) { rows = _rows.Values.ToList(); }
            return Task.FromResult(rows);
        }

        public Task DeleteAsync(PublicIdentityId ownerId, MailboxQueueId queueId, CancellationToken ct = default)
        {
            lock (_rows) { _rows.Remove((ownerId.Value, queueId.Value)); }
            return Task.CompletedTask;
        }

        public Task RecordTransientFailureAsync(
            PublicIdentityId ownerId, MailboxQueueId queueId, DomainError error, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class AlwaysOkUnwrapper : ISealedEnvelopeUnwrapper
    {
        public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope, PublicIdentityId recipientId) =>
            DomainResult<InboundEnvelope>.Success(new InboundHandshakeEnvelope(
                recipientId, PublicIdentityId.New(), DeviceId.Primary,
                IdentityKey.FromSpan(new byte[32]),
                DhPublicKey.FromSpan(new byte[32]),
                1, null, new byte[] { 1 }, DateTimeOffset.UtcNow));

        public DomainResult<InboundEnvelope> UnwrapDirect(ReadOnlyMemory<byte> payloadBytes, PublicIdentityId recipientId) =>
            throw new NotSupportedException();
    }

    private sealed class AlwaysOkIngress : IInboundIngressPipeline
    {
        public ValueTask<DomainResult> ProcessInboundAsync(InboundEnvelope envelope, CancellationToken ct = default) =>
            new(DomainResult.Success());
    }

    private sealed class LiveDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class FakeServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        private readonly Dictionary<Type, object> _services;
        public FakeServiceProvider(Dictionary<Type, object> services) => _services = services;
        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }
    }
}
