using System.Security.Cryptography;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public class GroupReceiverSessionTests
{
    private DeterministicCryptoEngine _engine = null!;
    private ChannelId _channelId;
    private PublicIdentityId _authorId;
    private DeviceId _authorDeviceId;
    private ChainKey _initialChainKey = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new DeterministicCryptoEngine();
        _channelId = ChannelId.New();
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
        using var session = new GroupReceiverSession(
            _channelId,
            _authorId,
            _authorDeviceId,
            _initialChainKey);

        var result0 = session.TryAdvanceToIteration(0, _engine);
        result0.IsSuccess.Should().BeTrue();
        result0.Value.Dispose();

        var result1 = session.TryAdvanceToIteration(1, _engine);
        result1.IsSuccess.Should().BeTrue();
        result1.Value.Dispose();

        session.ReceivingCounter.Should().Be(2);
    }

    [Test]
    public void AdvanceTo_OutOrder_CachesSkippedKeysAndRetrievesThem()
    {
        using var session = new GroupReceiverSession(
            _channelId,
            _authorId,
            _authorDeviceId,
            _initialChainKey);

        var result3 = session.TryAdvanceToIteration(3, _engine);
        result3.IsSuccess.Should().BeTrue();
        result3.Value.Dispose();
        session.ReceivingCounter.Should().Be(4);

        var result1 = session.TryAdvanceToIteration(1, _engine);
        result1.IsSuccess.Should().BeTrue();
        result1.Value.Dispose();

        var result1Again = session.TryAdvanceToIteration(1, _engine);
        result1Again.IsFailure.Should().BeTrue();
        result1Again.Error.Code.Should().Be("EXPIRED_OR_DUPLICATE_MESSAGE");
    }

    [Test]
    public void AdvanceTo_ExceedsMaxSkipThreshold_ReturnsError()
    {
        using var session = new GroupReceiverSession(
            _channelId,
            _authorId,
            _authorDeviceId,
            _initialChainKey);

        var result = session.TryAdvanceToIteration(GroupReceiverSession.MaxSkipThreshold + 1, _engine);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MAX_SKIP_THRESHOLD_EXCEEDED");
    }

    [Test]
    public void Zeroize_ClearsAllKeysAndPreventsFurtherAdvances()
    {
        var session = new GroupReceiverSession(
            _channelId,
            _authorId,
            _authorDeviceId,
            _initialChainKey);

        session.TryAdvanceToIteration(2, _engine);
        session.Zeroize();

        session.IsZeroized.Should().BeTrue();
        var result = session.TryAdvanceToIteration(3, _engine);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_SESSION_STATE");
    }
}
