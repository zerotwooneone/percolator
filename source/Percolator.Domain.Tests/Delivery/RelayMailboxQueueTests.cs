using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public class RelayMailboxQueueTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private PublicIdentityId _relayIdentityId;
    private RelayMailboxQueue _queue = null!;
    private BlindedRoutingToken _recipientToken;
    private DeliveryToken _deliveryToken = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        _relayIdentityId = PublicIdentityId.New();
        _queue = new RelayMailboxQueue(_relayIdentityId);
        _recipientToken = BlindedRoutingToken.New();
        _deliveryToken = DeliveryToken.FromSpan(new byte[32]);

        _queue.RegisterRecipient(_recipientToken, _deliveryToken);
    }

    [Test]
    public void Enqueue_WithValidDeliveryToken_StoresEnvelope()
    {
        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            _recipientToken,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddDays(7));

        var result = _queue.Enqueue(envelope, _deliveryToken, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        _queue.TotalCount.Should().Be(1);
    }

    [Test]
    public void Enqueue_WithUnauthorizedDeliveryToken_ReturnsError()
    {
        var invalidToken = DeliveryToken.FromSpan(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            _recipientToken,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddDays(7));

        var result = _queue.Enqueue(envelope, invalidToken, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_DELIVERY_TOKEN");
        _queue.TotalCount.Should().Be(0);
    }

    [Test]
    public void Enqueue_WhenQuotaExceeded_ReturnsQuotaExceededError()
    {
        var tightPolicy = new PurgePolicy(DefaultTtl: TimeSpan.FromDays(1), MaxRetainedEnvelopes: 1);

        var env1 = new MailboxEnvelope(
            EnvelopeId.New(),
            _recipientToken,
            new byte[] { 1 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddDays(1));

        var env2 = new MailboxEnvelope(
            EnvelopeId.New(),
            _recipientToken,
            new byte[] { 2 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddDays(1));

        _queue.Enqueue(env1, _deliveryToken, _timeProvider, tightPolicy).IsSuccess.Should().BeTrue();
        var result = _queue.Enqueue(env2, _deliveryToken, _timeProvider, tightPolicy);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MAILBOX_QUOTA_EXCEEDED");
        _queue.TotalCount.Should().Be(1);
    }

    [Test]
    public void PurgeExpired_RemovesOnlyExpiredEnvelopes()
    {
        var expiredEnv = new MailboxEnvelope(
            EnvelopeId.New(),
            _recipientToken,
            new byte[] { 1 },
            _timeProvider.UtcNow.AddDays(-2),
            _timeProvider.UtcNow.AddDays(-1));

        var validEnv = new MailboxEnvelope(
            EnvelopeId.New(),
            _recipientToken,
            new byte[] { 2 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddDays(5));

        _queue.Enqueue(expiredEnv, _deliveryToken, _timeProvider);
        _queue.Enqueue(validEnv, _deliveryToken, _timeProvider);

        int removed = _queue.PurgeExpired(_timeProvider);

        removed.Should().Be(1);
        _queue.TotalCount.Should().Be(1);
        _queue.Envelopes.Single().Id.Should().Be(validEnv.Id);
    }

    [Test]
    public void DrainForToken_RemovesAndReturnsMatchingEnvelopes()
    {
        var otherToken = BlindedRoutingToken.New();
        var otherDeliveryToken = DeliveryToken.FromSpan(new byte[32]);
        _queue.RegisterRecipient(otherToken, otherDeliveryToken);

        var env1 = new MailboxEnvelope(EnvelopeId.New(), _recipientToken, new byte[] { 1 }, _timeProvider.UtcNow, _timeProvider.UtcNow.AddDays(1));
        var env2 = new MailboxEnvelope(EnvelopeId.New(), otherToken, new byte[] { 2 }, _timeProvider.UtcNow, _timeProvider.UtcNow.AddDays(1));
        var env3 = new MailboxEnvelope(EnvelopeId.New(), _recipientToken, new byte[] { 3 }, _timeProvider.UtcNow, _timeProvider.UtcNow.AddDays(1));

        _queue.Enqueue(env1, _deliveryToken, _timeProvider);
        _queue.Enqueue(env2, otherDeliveryToken, _timeProvider);
        _queue.Enqueue(env3, _deliveryToken, _timeProvider);

        var drained = _queue.DrainForToken(_recipientToken);

        drained.Should().HaveCount(2);
        drained.Select(e => e.Id).Should().Contain([env1.Id, env3.Id]);
        _queue.TotalCount.Should().Be(1);
        _queue.Envelopes.Single().Id.Should().Be(env2.Id);
    }
}
