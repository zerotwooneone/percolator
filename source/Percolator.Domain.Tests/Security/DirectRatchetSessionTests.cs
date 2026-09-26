using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public class DirectRatchetSessionTests
{
    private DeterministicCryptoEngine _engine = null!;
    private PublicIdentityId _aliceId;
    private PublicIdentityId _bobId;
    private DeviceId _device1;
    private ChainKey _initialChainKey = null!;
    private ChainKey _rootKey = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new DeterministicCryptoEngine();
        _aliceId = PublicIdentityId.New();
        _bobId = PublicIdentityId.New();
        _device1 = DeviceId.Primary;

        _initialChainKey = ChainKey.FromSpan(new byte[32]);
        _rootKey = ChainKey.FromSpan(new byte[32]);
    }

    [TearDown]
    public void TearDown()
    {
        _initialChainKey?.Dispose();
        _rootKey?.Dispose();
    }

    [Test]
    public void StepSendingChain_AdvancesSendingCounter_AndDerivesKey()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: _initialChainKey,
            receivingChainKey: null);

        var result = session.StepSendingChain(_engine);

        result.IsSuccess.Should().BeTrue();
        result.Value.MessageCounter.Should().Be(0);
        result.Value.Key.Should().NotBeNull();
        session.SendingCounter.Should().Be(1);
    }

    [Test]
    public void StepReceivingChain_WithSkippedCounter_CachesSkippedKeys()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey);

        // Step straight to counter 3 (skipping 0, 1, 2)
        var result = session.StepReceivingChain(_engine, targetCounter: 3);

        result.IsSuccess.Should().BeTrue();
        result.Value.MessageCounter.Should().Be(3);
        session.ReceivingCounter.Should().Be(4);

        session.HasSkippedKey(0).Should().BeTrue();
        session.HasSkippedKey(1).Should().BeTrue();
        session.HasSkippedKey(2).Should().BeTrue();
        session.HasSkippedKey(3).Should().BeFalse();

        // Consume a skipped key
        var consumeResult = session.TryConsumeSkippedKey(1);
        consumeResult.IsSuccess.Should().BeTrue();
        session.HasSkippedKey(1).Should().BeFalse();
    }

    [Test]
    public void StepReceivingChain_WhenSkipThresholdExceeded_ReturnsError()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey);

        var result = session.StepReceivingChain(_engine, targetCounter: DirectRatchetSession.MaxSkipThreshold + 1);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SKIP_THRESHOLD_EXCEEDED");
    }

    [Test]
    public void StepDhRatchet_AdvancesRootKey_AndResetsCounters()
    {
        var localPriv = new byte[32];
        Array.Fill(localPriv, (byte)0xAA);
        var initialRemotePub = IdentityPublicKey.FromSpan(new byte[32]);

        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: _initialChainKey,
            receivingChainKey: null,
            remoteEphemeralPublicKey: initialRemotePub,
            localEphemeralPrivateKey: localPriv);

        session.StepSendingChain(_engine);
        session.SendingCounter.Should().Be(1);

        var newRemotePub = IdentityPublicKey.FromSpan(Enumerable.Repeat((byte)0xBB, 32).ToArray());
        var dhResult = session.StepDhRatchet(newRemotePub, _engine);

        dhResult.IsSuccess.Should().BeTrue();
        session.SendingCounter.Should().Be(0);
        session.ReceivingCounter.Should().Be(0);
        session.PreviousSendingChainLength.Should().Be(1);
        session.RemoteEphemeralPublicKey.Should().Be(newRemotePub);
        session.LocalEphemeralPublicKey.Should().NotBeNull();
    }

    [Test]
    public void Dispose_ZeroizesActiveSecretsAndSkippedKeys()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: _initialChainKey,
            receivingChainKey: _initialChainKey);

        session.StepReceivingChain(_engine, targetCounter: 2);
        session.HasSkippedKey(0).Should().BeTrue();

        session.Dispose();

        session.IsZeroized.Should().BeTrue();
        session.HasSkippedKey(0).Should().BeFalse();

        var stepResult = session.StepSendingChain(_engine);
        stepResult.IsFailure.Should().BeTrue();
        stepResult.Error.Code.Should().Be("INVALID_SESSION_STATE");
    }
}
