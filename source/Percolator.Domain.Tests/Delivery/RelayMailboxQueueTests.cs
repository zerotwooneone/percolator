using Percolator.Domain.Delivery.Events;
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
    private BlindedRoutingToken _recipientToken;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        _relayIdentityId = PublicIdentityId.New();
        _recipientToken = BlindedRoutingToken.New();
    }

    [Test]
    public void Enqueue_StoresEnvelope_AndEmitsEnvelopeBufferedEvent()
    {
        var queue = new RelayMailboxQueue(_relayIdentityId);
        var envelope = new MailboxEnvelope(
            Guid.NewGuid(),
            _recipientToken,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddDays(7));

        var result = queue.Enqueue(envelope, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        queue.TotalCount.Should().Be(1);
        queue.DomainEvents.Should().ContainSingle(e => e is EnvelopeBufferedEvent);
    }

    [Test]
    public void PurgeExpired_RemovesEnvelopesPastExpiresAtUtc()
    {
        var queue = new RelayMailboxQueue(_relayIdentityId);
        var expiredEnvelope = new MailboxEnvelope(
            Guid.NewGuid(),
            _recipientToken,
            new byte[] { 1 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddMinutes(10));

        var validEnvelope = new MailboxEnvelope(
            Guid.NewGuid(),
            _recipientToken,
            new byte[] { 2 },
            _timeProvider.UtcNow,
            _timeProvider.UtcNow.AddDays(1));

        queue.Enqueue(expiredEnvelope, _timeProvider);
        queue.Enqueue(validEnvelope, _timeProvider);

        _timeProvider.Advance(TimeSpan.FromMinutes(15));
        int purged = queue.PurgeExpired(_timeProvider);

        purged.Should().Be(1);
        queue.TotalCount.Should().Be(1);
    }

    [Test]
    public void DrainForToken_ReturnsMatchingEnvelopes_AndRemovesThemFromQueue()
    {
        var queue = new RelayMailboxQueue(_relayIdentityId);
        var otherToken = BlindedRoutingToken.New();

        queue.Enqueue(new MailboxEnvelope(Guid.NewGuid(), _recipientToken, new byte[] { 1 }, _timeProvider.UtcNow, _timeProvider.UtcNow.AddDays(1)), _timeProvider);
        queue.Enqueue(new MailboxEnvelope(Guid.NewGuid(), otherToken, new byte[] { 2 }, _timeProvider.UtcNow, _timeProvider.UtcNow.AddDays(1)), _timeProvider);

        var drained = queue.DrainForToken(_recipientToken);

        drained.Should().HaveCount(1);
        drained[0].RecipientToken.Should().Be(_recipientToken);
        queue.TotalCount.Should().Be(1);
    }

    [Test]
    public void PruneDiscretionary_DropsOldestEnvelopesWhenLimitExceeded()
    {
        var queue = new RelayMailboxQueue(_relayIdentityId);

        for (int i = 0; i < 5; i++)
        {
            _timeProvider.Advance(TimeSpan.FromMinutes(1));
            queue.Enqueue(new MailboxEnvelope(Guid.NewGuid(), _recipientToken, new byte[] { (byte)i }, _timeProvider.UtcNow, _timeProvider.UtcNow.AddDays(1)), _timeProvider);
        }

        // Limit to 3 items -> oldest 2 must be pruned
        int pruned = queue.PruneDiscretionary(maxRetainedCount: 3);

        pruned.Should().Be(2);
        queue.TotalCount.Should().Be(3);
    }
}
