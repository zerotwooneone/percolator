namespace Percolator.Infrastructure.Tests.Persistence;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
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

    private static RelayQueuedEnvelopeRecord Envelope(string queueId, byte payloadByte) => new(
        MailboxQueueId.Parse(queueId),
        new byte[] { payloadByte },
        DateTimeOffset.UtcNow);

    [Test]
    public async Task Register_StatePersistsAcrossStoreInstances()
    {
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.GenerateRandom();
        var drainToken = DeliveryToken.GenerateRandom();

        await _store.RegisterVerifiedAsync(routingToken, deliveryToken, drainToken);

        // A new store instance over a fresh DbContext simulates a process restart.
        var restartedStore = new SqliteRelayMailboxCapabilityStore(_testDb.ContextFactory);

        (await restartedStore.VerifyDrainTokenAsync(routingToken, drainToken)).Should().BeTrue();
        (await restartedStore.VerifyDeliveryTokenAsync(routingToken, deliveryToken)).Should().BeTrue();

        var stored = await restartedStore.GetDrainTokenAsync(routingToken);
        stored.Should().NotBeNull();
        stored!.Span.ToArray().Should().Equal(drainToken.Span.ToArray());
    }

    [Test]
    public async Task RegisterVerifiedAsync_WrongDrainTokenOnExistingMailbox_ReturnsOwnershipMismatch()
    {
        var routingToken = BlindedRoutingToken.New();
        var drainToken = DeliveryToken.GenerateRandom();
        await _store.RegisterVerifiedAsync(routingToken, DeliveryToken.GenerateRandom(), drainToken);

        var result = await _store.RegisterVerifiedAsync(routingToken, DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MAILBOX_OWNERSHIP_MISMATCH");

        // Original mailbox is untouched.
        (await _store.VerifyDrainTokenAsync(routingToken, drainToken)).Should().BeTrue();
    }

    [Test]
    public async Task RegisterVerifiedAsync_ExistingDrainToken_RotatesTokens()
    {
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken1 = DeliveryToken.GenerateRandom();
        var drainToken1 = DeliveryToken.GenerateRandom();
        var deliveryToken2 = DeliveryToken.GenerateRandom();
        var drainToken2 = DeliveryToken.GenerateRandom();

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
        var routingToken = BlindedRoutingToken.New();
        var drainToken = DeliveryToken.GenerateRandom();
        var deliveryTokens = Enumerable.Range(0, 12).Select(_ => DeliveryToken.GenerateRandom()).ToArray();

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
    public async Task EnqueueGetPurgeAck_EnvelopeLifecycle_PersistsAcrossInstances()
    {
        var routingToken = BlindedRoutingToken.New();
        await _store.RegisterAsync(routingToken, DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom());

        var first = Envelope("q-1", 0x11);
        var second = Envelope("q-2", 0x22);
        await _store.EnqueueEnvelopeAsync(routingToken, first);
        await _store.EnqueueEnvelopeAsync(routingToken, second);

        var restartedStore = new SqliteRelayMailboxCapabilityStore(_testDb.ContextFactory);
        var held = await restartedStore.GetEnvelopesAsync(routingToken);
        held.Select(e => e.QueueId.Value).Should().Equal("q-1", "q-2");
        held[0].EnvelopePayload.ToArray().Should().Equal(first.EnvelopePayload.ToArray());

        (await restartedStore.AcknowledgeEnvelopeAsync(routingToken, MailboxQueueId.Parse("q-1"))).Should().BeTrue();
        (await restartedStore.GetEnvelopesAsync(routingToken)).Select(e => e.QueueId.Value).Should().Equal("q-2");

        // Purge deletes without materializing payloads and returns the removed count.
        (await restartedStore.PurgeMailboxAsync(routingToken)).Should().Be(1);
        (await restartedStore.GetEnvelopesAsync(routingToken)).Should().BeEmpty();
        (await restartedStore.PurgeMailboxAsync(routingToken)).Should().Be(0);
    }

    [Test]
    public async Task AcknowledgeEnvelopeAsync_UnknownQueueId_ReturnsFalse()
    {
        var routingToken = BlindedRoutingToken.New();
        await _store.RegisterAsync(routingToken, DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom());

        (await _store.AcknowledgeEnvelopeAsync(routingToken, MailboxQueueId.Parse("missing"))).Should().BeFalse();
    }

    [Test]
    public async Task SubscribeMailbox_Enqueue_NotifiesSubscribers()
    {
        var routingToken = BlindedRoutingToken.New();
        await _store.RegisterAsync(routingToken, DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom());

        var channel = Channel.CreateUnbounded<RelayQueuedEnvelopeRecord>();
        using var subscription = _store.SubscribeMailbox(routingToken, channel.Writer);

        var envelope = Envelope("q-live", 0x33);
        await _store.EnqueueEnvelopeAsync(routingToken, envelope);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await channel.Reader.ReadAsync(timeout.Token);
        received.QueueId.Value.Should().Be("q-live");
    }

    [Test]
    public async Task GetDrainTokenAsync_ReturnsPersistedToken()
    {
        var routingToken = BlindedRoutingToken.New();
        var drainToken = DeliveryToken.GenerateRandom();
        await _store.RegisterAsync(routingToken, DeliveryToken.GenerateRandom(), drainToken);

        var restartedStore = new SqliteRelayMailboxCapabilityStore(_testDb.ContextFactory);
        var stored = await restartedStore.GetDrainTokenAsync(routingToken);

        stored.Should().NotBeNull();
        stored!.Span.ToArray().Should().Equal(drainToken.Span.ToArray());
    }
}
