namespace Percolator.Infrastructure.Tests.Persistence;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqliteRelayMailboxCapabilityStoreTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteRelayMailboxCapabilityStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _store = new SqliteRelayMailboxCapabilityStore(_testDb.ContextFactory);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static QueuedSealedEnvelopeProto Envelope(string queueId, byte payloadByte) => new()
    {
        QueueId = queueId,
        EnvelopePayload = ByteString.CopyFrom(payloadByte, 0, 1),
        EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
    };

    [Test]
    public async Task Register_StatePersistsAcrossStoreInstances()
    {
        var routingToken = RandomBytes(16);
        var deliveryToken = RandomBytes(32);
        var drainToken = RandomBytes(32);

        await _store.RegisterVerifiedAsync(routingToken, deliveryToken, drainToken);

        // A new store instance over a fresh DbContext simulates a process restart.
        var restartedStore = new SqliteRelayMailboxCapabilityStore(_testDb.ContextFactory);

        (await restartedStore.VerifyDrainTokenAsync(routingToken, drainToken)).Should().BeTrue();
        (await restartedStore.VerifyDeliveryTokenAsync(routingToken, deliveryToken)).Should().BeTrue();

        var stored = await restartedStore.GetDrainTokenAsync(routingToken);
        stored.Should().NotBeNull();
        stored!.Value.ToArray().Should().Equal(drainToken);
    }

    [Test]
    public async Task RegisterVerifiedAsync_WrongDrainTokenOnExistingMailbox_ReturnsOwnershipMismatch()
    {
        var routingToken = RandomBytes(16);
        var drainToken = RandomBytes(32);
        await _store.RegisterVerifiedAsync(routingToken, RandomBytes(32), drainToken);

        var result = await _store.RegisterVerifiedAsync(routingToken, RandomBytes(32), RandomBytes(32));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MAILBOX_OWNERSHIP_MISMATCH");

        // Original mailbox is untouched.
        (await _store.VerifyDrainTokenAsync(routingToken, drainToken)).Should().BeTrue();
    }

    [Test]
    public async Task RegisterVerifiedAsync_ExistingDrainToken_RotatesTokens()
    {
        var routingToken = RandomBytes(16);
        var deliveryToken1 = RandomBytes(32);
        var drainToken1 = RandomBytes(32);
        var deliveryToken2 = RandomBytes(32);
        var drainToken2 = RandomBytes(32);

        await _store.RegisterVerifiedAsync(routingToken, deliveryToken1, drainToken1);

        var rotated = await _store.RegisterVerifiedAsync(
            routingToken, deliveryToken2, drainToken2, existingDrainToken: drainToken1);

        rotated.IsSuccess.Should().BeTrue();
        (await _store.VerifyDrainTokenAsync(routingToken, drainToken2)).Should().BeTrue();
        (await _store.VerifyDrainTokenAsync(routingToken, drainToken1)).Should().BeFalse();
        (await _store.VerifyDeliveryTokenAsync(routingToken, deliveryToken2)).Should().BeTrue();
    }

    [Test]
    public async Task VerifyDeliveryTokenAsync_HonoursAuthorizedTokenRing_WithCapTen()
    {
        var routingToken = RandomBytes(16);
        var drainToken = RandomBytes(32);
        var deliveryTokens = Enumerable.Range(0, 12).Select(_ => RandomBytes(32)).ToArray();

        foreach (var token in deliveryTokens)
        {
            await _store.RegisterVerifiedAsync(routingToken, token, drainToken);
        }

        // The two oldest authorized tokens fell out of the ring; the newest ten remain valid.
        (await _store.VerifyDeliveryTokenAsync(routingToken, deliveryTokens[0])).Should().BeFalse();
        (await _store.VerifyDeliveryTokenAsync(routingToken, deliveryTokens[1])).Should().BeFalse();
        for (int i = 2; i < deliveryTokens.Length; i++)
        {
            (await _store.VerifyDeliveryTokenAsync(routingToken, deliveryTokens[i])).Should().BeTrue($"token {i} should remain authorized");
        }
    }

    [Test]
    public async Task EnqueueGetDrainAck_EnvelopeLifecycle_PersistsAcrossInstances()
    {
        var routingToken = RandomBytes(16);
        await _store.RegisterAsync(routingToken, RandomBytes(32), RandomBytes(32));

        var first = Envelope("q-1", 0x11);
        var second = Envelope("q-2", 0x22);
        await _store.EnqueueEnvelopeAsync(routingToken, first);
        await _store.EnqueueEnvelopeAsync(routingToken, second);

        var restartedStore = new SqliteRelayMailboxCapabilityStore(_testDb.ContextFactory);
        var held = await restartedStore.GetEnvelopesAsync(routingToken);
        held.Select(e => e.QueueId).Should().Equal("q-1", "q-2");
        held[0].EnvelopePayload.ToByteArray().Should().Equal(first.EnvelopePayload.ToByteArray());

        (await restartedStore.AcknowledgeEnvelopeAsync(routingToken, "q-1")).Should().BeTrue();
        (await restartedStore.GetEnvelopesAsync(routingToken)).Select(e => e.QueueId).Should().Equal("q-2");

        var drained = await restartedStore.DrainEnvelopesAsync(routingToken);
        drained.Select(e => e.QueueId).Should().Equal("q-2");
        (await restartedStore.GetEnvelopesAsync(routingToken)).Should().BeEmpty();
    }

    [Test]
    public async Task AcknowledgeEnvelopeAsync_UnknownQueueId_ReturnsFalse()
    {
        var routingToken = RandomBytes(16);
        await _store.RegisterAsync(routingToken, RandomBytes(32), RandomBytes(32));

        (await _store.AcknowledgeEnvelopeAsync(routingToken, "missing")).Should().BeFalse();
    }

    [Test]
    public async Task SubscribeMailbox_Enqueue_NotifiesSubscribers()
    {
        var routingToken = RandomBytes(16);
        await _store.RegisterAsync(routingToken, RandomBytes(32), RandomBytes(32));

        var channel = Channel.CreateUnbounded<QueuedSealedEnvelopeProto>();
        using var subscription = _store.SubscribeMailbox(routingToken, channel.Writer);

        var envelope = Envelope("q-live", 0x33);
        await _store.EnqueueEnvelopeAsync(routingToken, envelope);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await channel.Reader.ReadAsync(timeout.Token);
        received.QueueId.Should().Be("q-live");
    }

    [Test]
    public async Task GetDrainTokenAsync_ReturnsPersistedToken()
    {
        var routingToken = RandomBytes(16);
        var drainToken = RandomBytes(32);
        await _store.RegisterAsync(routingToken, RandomBytes(32), drainToken);

        var restartedStore = new SqliteRelayMailboxCapabilityStore(_testDb.ContextFactory);
        var stored = await restartedStore.GetDrainTokenAsync(routingToken);

        stored.Should().NotBeNull();
        stored!.Value.ToArray().Should().Equal(drainToken);
    }
}
