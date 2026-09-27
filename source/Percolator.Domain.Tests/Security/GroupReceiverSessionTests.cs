using System.Security.Cryptography;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public class GroupReceiverSessionTests
{
    private DeterministicCryptoEngine _engine = null!;
    private ConversationId _conversationId;
    private PublicIdentityId _authorId;
    private DeviceId _authorDeviceId;
    private ChainKey _initialChainKey = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new DeterministicCryptoEngine();
        _conversationId = ConversationId.New();
        _authorId = PublicIdentityId.New();
        _authorDeviceId = DeviceId.Primary;

        byte[] rawKey = new byte[32];
        RandomNumberGenerator.Fill(rawKey);
        _initialChainKey = ChainKey.FromSpan(rawKey);
    }

    [TearDown]
    public void TearDown()
    {
        _initialChainKey?.Dispose();
    }

    [Test]
    public void AdvanceTo_SequentialIterations_Succeeds()
    {
        var session = new GroupReceiverSession(
            _conversationId,
            _authorId,
            _authorDeviceId,
            _initialChainKey,
            initialIteration: 0);

        var key0 = session.AdvanceTo(0, _engine);
        key0.IsSuccess.Should().BeTrue();
        key0.Value.Should().NotBeNull();
        session.ReceivingCounter.Should().Be(1);

        var key1 = session.AdvanceTo(1, _engine);
        key1.IsSuccess.Should().BeTrue();
        session.ReceivingCounter.Should().Be(2);
    }

    [Test]
    public void AdvanceTo_WithSkippedIterations_CachesSkippedKeysAndConsumes()
    {
        var session = new GroupReceiverSession(
            _conversationId,
            _authorId,
            _authorDeviceId,
            _initialChainKey,
            initialIteration: 0);

        // Advance directly to iteration 3 (skipping 0, 1, 2)
        var key3 = session.AdvanceTo(3, _engine);
        key3.IsSuccess.Should().BeTrue();
        session.ReceivingCounter.Should().Be(4);

        session.HasSkippedKey(0).Should().BeTrue();
        session.HasSkippedKey(1).Should().BeTrue();
        session.HasSkippedKey(2).Should().BeTrue();
        session.HasSkippedKey(3).Should().BeFalse();

        // Late delivery of message 1: AdvanceTo should consume from cache
        var key1 = session.AdvanceTo(1, _engine);
        key1.IsSuccess.Should().BeTrue();
        session.HasSkippedKey(1).Should().BeFalse();
    }

    [Test]
    public void AdvanceTo_WhenPastAndNotCached_ReturnsCounterAlreadyPassed()
    {
        var session = new GroupReceiverSession(
            _conversationId,
            _authorId,
            _authorDeviceId,
            _initialChainKey,
            initialIteration: 0);

        session.AdvanceTo(0, _engine);

        // Try to request iteration 0 again (already consumed, not in skipped)
        var replay = session.AdvanceTo(0, _engine);
        replay.IsFailure.Should().BeTrue();
        replay.Error.Code.Should().Be("COUNTER_ALREADY_PASSED");
    }

    [Test]
    public void Dispose_ZeroizesActiveSecretsAndSkippedKeys()
    {
        var session = new GroupReceiverSession(
            _conversationId,
            _authorId,
            _authorDeviceId,
            _initialChainKey,
            initialIteration: 0);

        session.AdvanceTo(2, _engine);
        session.HasSkippedKey(0).Should().BeTrue();

        session.Dispose();
        session.IsZeroized.Should().BeTrue();
        session.HasSkippedKey(0).Should().BeFalse();

        var failedAdvance = session.AdvanceTo(3, _engine);
        failedAdvance.IsFailure.Should().BeTrue();
        failedAdvance.Error.Code.Should().Be("INVALID_SESSION_STATE");
    }
}
