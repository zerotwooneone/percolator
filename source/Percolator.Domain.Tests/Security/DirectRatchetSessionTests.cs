using System.Security.Cryptography;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public class DirectRatchetSessionTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private PublicIdentityId _ownerId;
    private DeviceId _ownerDeviceId;
    private PublicIdentityId _peerId;
    private DeviceId _peerDeviceId;
    private ChainKey _initialChainKey;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
        _ownerId = PublicIdentityId.New();
        _ownerDeviceId = DeviceId.Primary;
        _peerId = PublicIdentityId.New();
        _peerDeviceId = DeviceId.Primary;

        byte[] rawChainKey = new byte[32];
        RandomNumberGenerator.Fill(rawChainKey);
        _initialChainKey = ChainKey.FromSpan(rawChainKey);
    }

    [Test]
    public void StepSendingChain_AdvancesCounter_AndDerivesUniqueMessageKeys()
    {
        var session = new DirectRatchetSession(
            _ownerId,
            _ownerDeviceId,
            _peerId,
            _peerDeviceId,
            sendingChainKey: _initialChainKey,
            receivingChainKey: null);

        var result1 = session.StepSendingChain(_cryptoEngine);
        var result2 = session.StepSendingChain(_cryptoEngine);

        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result1.Value.MessageCounter.Should().Be(0);
        result2.Value.MessageCounter.Should().Be(1);
        result1.Value.Key.Should().NotBe(result2.Value.Key);
        session.SendingCounter.Should().Be(2);
    }

    [Test]
    public void StepReceivingChain_InOrder_AdvancesReceivingCounter()
    {
        var session = new DirectRatchetSession(
            _ownerId,
            _ownerDeviceId,
            _peerId,
            _peerDeviceId,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey);

        var result = session.StepReceivingChain(_cryptoEngine, targetCounter: 0);

        result.IsSuccess.Should().BeTrue();
        session.ReceivingCounter.Should().Be(1);
    }

    [Test]
    public void StepReceivingChain_WithSkippedCounter_CachesSkippedKeys()
    {
        var session = new DirectRatchetSession(
            _ownerId,
            _ownerDeviceId,
            _peerId,
            _peerDeviceId,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey);

        // Message 2 arrives first (skipping 0 and 1)
        var result = session.StepReceivingChain(_cryptoEngine, targetCounter: 2);

        result.IsSuccess.Should().BeTrue();
        session.ReceivingCounter.Should().Be(3);
        session.HasSkippedKey(0).Should().BeTrue();
        session.HasSkippedKey(1).Should().BeTrue();

        // Late message 0 arrives and retrieves cached key
        var lateResult = session.TryConsumeSkippedKey(0);
        lateResult.IsSuccess.Should().BeTrue();
        session.HasSkippedKey(0).Should().BeFalse();
    }

    [Test]
    public void StepReceivingChain_WhenSkipExceedsThreshold_ReturnsError()
    {
        var session = new DirectRatchetSession(
            _ownerId,
            _ownerDeviceId,
            _peerId,
            _peerDeviceId,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey);

        // Attempting to skip 2001 messages
        var result = session.StepReceivingChain(_cryptoEngine, targetCounter: 2001);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SKIP_THRESHOLD_EXCEEDED");
    }

    [Test]
    public void Dispose_ZeroizesActiveChainKeys()
    {
        var session = new DirectRatchetSession(
            _ownerId,
            _ownerDeviceId,
            _peerId,
            _peerDeviceId,
            sendingChainKey: _initialChainKey,
            receivingChainKey: null);

        session.Dispose();

        session.IsZeroized.Should().BeTrue();
    }
}
