using Percolator.Application2.Handshake;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.Handshake;

[TestFixture]
public sealed class MemoryHandshakeReplayFilterTests
{
    [Test]
    public void TryRecordAndValidate_FirstEncounter_ReturnsTrue()
    {
        var filter = new MemoryHandshakeReplayFilter();
        var key = DhPublicKey.FromSpan(new byte[32]);
        var now = DateTimeOffset.UtcNow;

        var result = filter.TryRecordAndValidate(key, now);

        result.Should().BeTrue();
    }

    [Test]
    public void TryRecordAndValidate_DuplicateKey_ReturnsFalse()
    {
        var filter = new MemoryHandshakeReplayFilter();
        var key = DhPublicKey.FromSpan(new byte[32]);
        var now = DateTimeOffset.UtcNow;

        filter.TryRecordAndValidate(key, now);
        var replayResult = filter.TryRecordAndValidate(key, now.AddSeconds(1));

        replayResult.Should().BeFalse();
    }

    [Test]
    public void TryRecordAndValidate_ExpiredKey_PrunedAndAllowsReinsertion()
    {
        var ttl = TimeSpan.FromMinutes(5);
        var filter = new MemoryHandshakeReplayFilter(capacity: 100, ttl: ttl);
        var key = DhPublicKey.FromSpan(new byte[32]);
        var t0 = DateTimeOffset.UtcNow;

        filter.TryRecordAndValidate(key, t0);

        // Advance beyond TTL
        var tFuture = t0.AddMinutes(6);
        var result = filter.TryRecordAndValidate(key, tFuture);

        result.Should().BeTrue();
    }

    [Test]
    public void TryRecordAndValidate_ExceedsCapacity_EvictsOldest()
    {
        var filter = new MemoryHandshakeReplayFilter(capacity: 2, ttl: TimeSpan.FromHours(1));
        var key1Bytes = new byte[32]; key1Bytes[0] = 1;
        var key2Bytes = new byte[32]; key2Bytes[0] = 2;
        var key3Bytes = new byte[32]; key3Bytes[0] = 3;

        var key1 = DhPublicKey.FromSpan(key1Bytes);
        var key2 = DhPublicKey.FromSpan(key2Bytes);
        var key3 = DhPublicKey.FromSpan(key3Bytes);

        var now = DateTimeOffset.UtcNow;

        filter.TryRecordAndValidate(key1, now.AddSeconds(1)).Should().BeTrue();
        filter.TryRecordAndValidate(key2, now.AddSeconds(2)).Should().BeTrue();

        // Adding key3 should trigger capacity check and allow key3
        filter.TryRecordAndValidate(key3, now.AddSeconds(3)).Should().BeTrue();

        // Key1 (oldest) was evicted, so recording it again should succeed
        filter.TryRecordAndValidate(key1, now.AddSeconds(4)).Should().BeTrue();
    }
}
