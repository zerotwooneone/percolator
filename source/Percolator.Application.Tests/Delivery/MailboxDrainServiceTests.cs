using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.Delivery;

[TestFixture]
public sealed class MailboxDrainServiceTests
{
    private static readonly PublicIdentityId Owner = PublicIdentityId.New();
    private static readonly PublicIdentityId Peer = PublicIdentityId.New();

    private List<string> _callOrder = null!;
    private InMemoryPendingIngressStore _store = null!;
    private StubUnwrapper _unwrapper = null!;
    private StubIngressPipeline _ingress = null!;
    private StubStreamSession _session = null!;
    private RecordingPreKeyMaintenance _preKeyMaintenance = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private MailboxDrainService _service = null!;
    private MailboxDrainTarget _target = null!;

    [SetUp]
    public void SetUp()
    {
        _callOrder = new List<string>();
        _store = new InMemoryPendingIngressStore(_callOrder);
        _unwrapper = new StubUnwrapper();
        _ingress = new StubIngressPipeline();
        _session = new StubStreamSession(_callOrder);
        _preKeyMaintenance = new RecordingPreKeyMaintenance();
        _timeProvider = new TestDateTimeProvider();
        _service = new MailboxDrainService(
            _store,
            _unwrapper,
            _ingress,
            _preKeyMaintenance,
            _timeProvider,
            NullLogger<MailboxDrainService>.Instance);
        _target = new MailboxDrainTarget(Owner, PublicIdentityId.New(), RelayEndpoint.FromHostAndPort("127.0.0.1", 5000));
        _unwrapper.Result = DomainResult<InboundEnvelope>.Success(Handshake(Peer, opkId: null));
    }

    [TearDown]
    public async Task TearDown() => await _session.DisposeAsync();

    private QueuedMailboxEnvelope Queued(string queueId, DateTimeOffset? expiresAt = null) =>
        new(
            MailboxQueueId.Parse(queueId),
            new MailboxEnvelope(
                EnvelopeId.New(),
                BlindedRoutingToken.New(),
                new byte[] { 1, 2, 3, 4 },
                _timeProvider.UtcNow.AddMinutes(-5),
                expiresAt ?? _timeProvider.UtcNow.AddDays(1)));

    private InboundHandshakeEnvelope Handshake(PublicIdentityId sender, uint? opkId) =>
        new(
            RecipientIdentityId: Owner,
            SenderIdentityId: sender,
            SenderDeviceId: DeviceId.Primary,
            SenderIdentityKey: IdentityKey.FromSpan(new byte[32]),
            SenderEphemeralKey: DhPublicKey.FromSpan(new byte[32]),
            SignedPreKeyId: 1,
            OneTimePreKeyId: opkId,
            EncryptedPayload: new byte[] { 9, 9 },
            ReceivedAtUtc: _timeProvider.UtcNow);

    [Test]
    public async Task ReceiveAsync_PersistsBeforeAck_ThenProcessesAndDeletes()
    {
        var result = await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        result.IsSuccess.Should().BeTrue();
        _callOrder.Should().Equal("upsert", "ack", "delete");
        _ingress.Received.Should().HaveCount(1);
        _store.ListAll(Owner).Should().BeEmpty();
    }

    [Test]
    public void ReceiveAsync_UpsertThrows_DoesNotAck()
    {
        _store.UpsertFault = new InvalidOperationException("db gone");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ReceiveAsync(_target, Queued("q-1"), _session));

        ex!.Message.Should().Be("db gone");
        _session.Acks.Should().BeEmpty();
    }

    [Test]
    public async Task ReceiveAsync_AckFails_ReturnsFailure_AndKeepsSpooledRow()
    {
        var ackError = new DomainError("ACK_FAILED", "stream closed");
        _session.AckResult = DomainResult.Failure(ackError);

        var result = await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ACK_FAILED");
        _ingress.Received.Should().BeEmpty();
        _store.ListAll(Owner).Should().HaveCount(1);
    }

    [Test]
    public async Task ReceiveAsync_RedeliveredEnvelope_DedupsUpsert()
    {
        await _service.ReceiveAsync(_target, Queued("q-1"), _session);
        _callOrder.Clear();
        _ingress.Fault = new InvalidOperationException("transient");
        var result = await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        result.IsFailure.Should().BeTrue();
        _callOrder.Should().Equal("upsert", "ack", "fail");
        var record = _store.ListAll(Owner).Should().ContainSingle().Subject;
        record.Attempts.Should().Be(1);
        record.LastError!.Value.Code.Should().Be("INGRESS_TRANSIENT_FAILURE");
    }

    [Test]
    public async Task ReceiveAsync_UnwrapFailure_DropsSpooledRow_WithoutIngress()
    {
        _unwrapper.Result = DomainResult<InboundEnvelope>.Failure(new DomainError("BAD_FRAME", "garbage"));

        var result = await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        result.IsSuccess.Should().BeTrue();
        _ingress.Received.Should().BeEmpty();
        _store.ListAll(Owner).Should().BeEmpty();
    }

    [Test]
    public async Task ReceiveAsync_SelfEchoEnvelope_DropsWithoutIngress()
    {
        _unwrapper.Result = DomainResult<InboundEnvelope>.Success(Handshake(Owner, opkId: null));

        var result = await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        result.IsSuccess.Should().BeTrue();
        _ingress.Received.Should().BeEmpty();
        _store.ListAll(Owner).Should().BeEmpty();
    }

    [Test]
    public async Task ReceiveAsync_ExpiredEnvelope_DropsWithoutUnwrap()
    {
        var expired = Queued("q-old", expiresAt: _timeProvider.UtcNow.AddSeconds(-1));

        var result = await _service.ReceiveAsync(_target, expired, _session);

        result.IsSuccess.Should().BeTrue();
        _unwrapper.Calls.Should().Be(0);
        _ingress.Received.Should().BeEmpty();
        _store.ListAll(Owner).Should().BeEmpty();
    }

    [Test]
    public async Task ReceiveAsync_DeterministicIngressRejection_DropsSpooledRow()
    {
        _ingress.Result = DomainResult.Failure(new DomainError("SESSION_NOT_FOUND", "no channel"));

        var result = await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        result.IsSuccess.Should().BeTrue();
        _store.ListAll(Owner).Should().BeEmpty();
    }

    [Test]
    public async Task ReceiveAsync_TransientIngressException_RecordsFailureAndRetains()
    {
        _ingress.Fault = new InvalidOperationException("disk hiccup");

        var result = await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INGRESS_TRANSIENT_FAILURE");
        var record = _store.ListAll(Owner).Should().ContainSingle().Subject;
        record.Attempts.Should().Be(1);
        record.LastError!.Value.Description.Should().Contain("disk hiccup");
    }

    [Test]
    public async Task ReceiveAsync_TransientFailureAtMaxAttempts_DeadLetters()
    {
        // Push the record to Attempts = MaxAttempts - 1 so this delivery exhausts the budget.
        var queued = Queued("q-1");
        await _store.UpsertAsync(Owner, queued);
        for (var i = 0; i < MailboxDrainService.MaxAttempts - 1; i++)
        {
            await _store.RecordTransientFailureAsync(Owner, queued.QueueId, new DomainError("E", "x"));
        }

        _callOrder.Clear();
        _ingress.Fault = new InvalidOperationException("still broken");

        var result = await _service.ReceiveAsync(_target, queued, _session);

        result.IsSuccess.Should().BeTrue();
        _callOrder.Should().Equal("upsert", "ack", "delete");
        _callOrder.Should().NotContain("fail");
        _store.ListAll(Owner).Should().BeEmpty();
    }

    [Test]
    public void MailboxDrainTarget_InvalidParameters_Throws()
    {
        var validId = PublicIdentityId.New();
        var validEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5000);

        Assert.Throws<ArgumentException>(() => new MailboxDrainTarget(default, validId, validEndpoint));
        Assert.Throws<ArgumentException>(() => new MailboxDrainTarget(validId, default, validEndpoint));
        Assert.Throws<ArgumentNullException>(() => new MailboxDrainTarget(validId, validId, null!));
    }

    [Test]
    public void PendingIngressRecord_InvalidParameters_Throws()
    {
        var queued = Queued("q-1");

        Assert.Throws<ArgumentNullException>(() => new PendingIngressRecord(null!, queued, 0, null));
        Assert.Throws<ArgumentNullException>(() => new PendingIngressRecord(queued.QueueId, null!, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PendingIngressRecord(queued.QueueId, queued, -1, null));
    }

    [Test]
    public async Task ReceiveAsync_HandshakeWithConsumedOpk_NotifiesPreKeyMaintenance()
    {
        _unwrapper.Result = DomainResult<InboundEnvelope>.Success(Handshake(Peer, opkId: 42));

        await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        _preKeyMaintenance.Notified.Should().Equal(_target);
    }

    [Test]
    public async Task ReceiveAsync_HandshakeWithoutOpk_DoesNotNotify()
    {
        await _service.ReceiveAsync(_target, Queued("q-1"), _session);

        _preKeyMaintenance.Notified.Should().BeEmpty();
    }

    [Test]
    public async Task ReplayPendingAsync_ProcessesSpoolInReceiveOrder()
    {
        await _store.UpsertAsync(Owner, Queued("q-3"));
        await _store.UpsertAsync(Owner, Queued("q-1"));
        await _store.UpsertAsync(Owner, Queued("q-2"));

        var result = await _service.ReplayPendingAsync(_target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3);
        _ingress.Received.Should().HaveCount(3);
        _store.ListAll(Owner).Should().BeEmpty();
    }

    [Test]
    public async Task ReplayPendingAsync_TransientFailure_RetainsRow_AndIsNotCounted()
    {
        var queued = Queued("q-1");
        await _store.UpsertAsync(Owner, queued);
        _ingress.Fault = new InvalidOperationException("still broken");

        var result = await _service.ReplayPendingAsync(_target);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
        var record = _store.ListAll(Owner).Should().ContainSingle().Subject;
        record.Attempts.Should().Be(1);
    }

    [Test]
    public async Task ReplayPendingAsync_ListThrows_ReturnsFailure()
    {
        _store.ListFault = new InvalidOperationException("db gone");

        var result = await _service.ReplayPendingAsync(_target);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PENDING_INGRESS_LIST_FAILED");
    }

    private sealed class InMemoryPendingIngressStore : IPendingIngressStore
    {
        private readonly List<(Guid Owner, string QueueId)> _order = new();
        private readonly Dictionary<(Guid Owner, string QueueId), PendingIngressRecord> _rows = new();
        private readonly List<string> _callOrder;

        public InMemoryPendingIngressStore(List<string> callOrder) => _callOrder = callOrder;

        public Exception? UpsertFault { get; set; }
        public Exception? ListFault { get; set; }

        public Task<PendingIngressRecord> UpsertAsync(
            PublicIdentityId ownerId, QueuedMailboxEnvelope envelope, CancellationToken ct = default)
        {
            _callOrder.Add("upsert");
            if (UpsertFault != null)
            {
                throw UpsertFault;
            }

            var key = (ownerId.Value, envelope.QueueId.Value);
            if (_rows.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing);
            }

            var record = new PendingIngressRecord(envelope.QueueId, envelope, Attempts: 0, LastError: null);
            _rows[key] = record;
            _order.Add(key);
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<PendingIngressRecord>> ListByOwnerAsync(
            PublicIdentityId ownerId, CancellationToken ct = default)
        {
            if (ListFault != null)
            {
                throw ListFault;
            }

            IReadOnlyList<PendingIngressRecord> records = _order
                .Where(k => k.Owner == ownerId.Value)
                .Select(k => _rows[k])
                .ToList();
            return Task.FromResult(records);
        }

        public Task DeleteAsync(PublicIdentityId ownerId, MailboxQueueId queueId, CancellationToken ct = default)
        {
            _callOrder.Add("delete");
            var key = (ownerId.Value, queueId.Value);
            _rows.Remove(key);
            _order.Remove(key);
            return Task.CompletedTask;
        }

        public Task RecordTransientFailureAsync(
            PublicIdentityId ownerId, MailboxQueueId queueId, DomainError error, CancellationToken ct = default)
        {
            _callOrder.Add("fail");
            var key = (ownerId.Value, queueId.Value);
            if (_rows.TryGetValue(key, out var record))
            {
                _rows[key] = record with { Attempts = record.Attempts + 1, LastError = error };
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<PendingIngressRecord> ListAll(PublicIdentityId ownerId) =>
            _order.Where(k => k.Owner == ownerId.Value).Select(k => _rows[k]).ToList();
    }

    private sealed class StubUnwrapper : ISealedEnvelopeUnwrapper
    {
        public DomainResult<InboundEnvelope> Result { get; set; }
        public int Calls { get; private set; }

        public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope, PublicIdentityId recipientId)
        {
            Calls++;
            return Result;
        }

        public DomainResult<InboundEnvelope> UnwrapDirect(ReadOnlyMemory<byte> payloadBytes, PublicIdentityId recipientId)
            => throw new NotSupportedException();
    }

    private sealed class StubIngressPipeline : IInboundIngressPipeline
    {
        public DomainResult Result { get; set; } = DomainResult.Success();
        public Exception? Fault { get; set; }
        public List<InboundEnvelope> Received { get; } = new();

        public ValueTask<DomainResult> ProcessInboundAsync(InboundEnvelope envelope, CancellationToken ct = default)
        {
            Received.Add(envelope);
            if (Fault != null)
            {
                throw Fault;
            }
            return new ValueTask<DomainResult>(Result);
        }
    }

    private sealed class StubStreamSession : IMailboxStreamSession
    {
        private readonly List<string> _callOrder;

        public StubStreamSession(List<string> callOrder) => _callOrder = callOrder;

        public DomainResult AckResult { get; set; } = DomainResult.Success();
        public List<string> Acks { get; } = new();

        public IAsyncEnumerable<QueuedMailboxEnvelope> Envelopes => AsyncEnumerable.Empty<QueuedMailboxEnvelope>();

        public Task<DomainResult> AcknowledgeAsync(MailboxQueueId queueId, CancellationToken ct = default)
        {
            _callOrder.Add("ack");
            Acks.Add(queueId.Value);
            return Task.FromResult(AckResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPreKeyMaintenance : IPreKeyMaintenanceService
    {
        public List<MailboxDrainTarget> Notified { get; } = new();

        public void NotifyOneTimePreKeyConsumed(MailboxDrainTarget target) => Notified.Add(target);

        public Task EvaluateAsync(MailboxDrainTarget target, bool forced, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
