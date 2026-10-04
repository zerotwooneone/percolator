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
    public void StepReceivingChain_TargetMatchesCounter_DerivesKeyDirectly()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey);

        var result = session.StepReceivingChain(_engine, targetCounter: 0);

        result.IsSuccess.Should().BeTrue();
        result.Value.MessageCounter.Should().Be(0);
        result.Value.Key.Should().NotBeNull();
        session.ReceivingCounter.Should().Be(1);
    }

    [Test]
    public void StepReceivingChain_TargetGreaterThanCounter_CachesSkippedKeys()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey);

        // Target message 2 received out-of-order -> keys 0 and 1 should be skipped & cached
        var result = session.StepReceivingChain(_engine, targetCounter: 2);

        result.IsSuccess.Should().BeTrue();
        result.Value.MessageCounter.Should().Be(2);
        session.ReceivingCounter.Should().Be(3);

        // Verify keys 0 and 1 were cached and can be retrieved
        var key0 = session.TryGetSkippedKey(0);
        key0.IsSuccess.Should().BeTrue();

        var key1 = session.TryGetSkippedKey(1);
        key1.IsSuccess.Should().BeTrue();

        // Retrieving again should fail (consumed / anti-replay)
        var key0Again = session.TryGetSkippedKey(0);
        key0Again.IsFailure.Should().BeTrue();
    }

    [Test]
    public void StepReceivingChain_TargetBehindCounter_Fails()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: null,
            receivingChainKey: _initialChainKey,
            receivingCounter: 5);

        var result = session.StepReceivingChain(_engine, targetCounter: 3);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("COUNTER_ALREADY_PASSED");
    }

    [Test]
    public void StepReceivingChain_SkipThresholdExceeded_Fails()
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
    public void StoreSkippedKey_WhenGhostEntriesPresent_MaintainsBoundedCount()
    {
        var session = new DirectRatchetSession(
            _aliceId, _device1,
            _bobId, _device1,
            rootKey: _rootKey,
            sendingChainKey: _initialChainKey,
            receivingChainKey: _initialChainKey);

        // Step receiving chain to targetCounter: 1000 -> stores 1000 skipped keys (counters 0..999)
        session.StepReceivingChain(_engine, targetCounter: 1000);
        session.SkippedMessageKeys.Count.Should().Be(1000);

        // Consume key 0 early via TryGetSkippedKey -> leaves a ghost entry in _skippedKeyOrder
        var earlyGet = session.TryGetSkippedKey(0);
        earlyGet.IsSuccess.Should().BeTrue();
        session.SkippedMessageKeys.Count.Should().Be(999);

        // Step receiving chain by 2 more messages (targetCounter: 1002) -> skips 1000, 1001
        session.StepReceivingChain(_engine, targetCounter: 1002);

        // The while loop in StoreSkippedKey must evict past the ghost entry and keep the total strictly bounded by MaxTotalSkippedKeys (1000)
        session.SkippedMessageKeys.Count.Should().BeLessThanOrEqualTo(DirectRatchetSession.MaxTotalSkippedKeys);
    }

    [Test]
    public void StepDhRatchet_AdvancesRootKey_AndResetsCounters()
    {
        Span<byte> privBytes = stackalloc byte[32];
        privBytes.Fill((byte)0xAA);
        using var localPriv = EphemeralPrivateKey.FromSpan(privBytes);
        var initialRemotePub = DhPublicKey.FromSpan(new byte[32]);

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

        var newRemotePub = DhPublicKey.FromSpan(Enumerable.Repeat((byte)0xBB, 32).ToArray());
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
        session.TryGetSkippedKey(0).IsSuccess.Should().BeTrue();

        session.Dispose();

        session.IsZeroized.Should().BeTrue();

        var stepResult = session.StepSendingChain(_engine);
        stepResult.IsFailure.Should().BeTrue();
        stepResult.Error.Code.Should().Be("INVALID_SESSION_STATE");
    }

    [Test]
    public void InitiateOutbound_And_InitiateInbound_DerivesMatchingInitialKeys()
    {
        // 1. Bob prepares his signed pre-key
        var (bobSignedPreKeyPriv, bobSignedPreKeyPub) = _engine.GenerateEphemeralKeyPair();
        var bobIdentityKey = IdentityKey.FromSpan(new byte[32]);
        var bobProof = DeviceLinkProof.FromSpan(new byte[64]);
        var bobBundle = new PreKeyBundle(_bobId, _device1, bobIdentityKey, bobSignedPreKeyPub, bobProof);

        // 2. Alice initiates outbound session
        var aliceResult = DirectRatchetSession.InitiateOutbound(_aliceId, _device1, bobBundle, _engine);
        aliceResult.IsSuccess.Should().BeTrue();
        using var aliceSession = aliceResult.Value;

        // 3. Alice steps her sending chain to derive the first message key
        var aliceStep = aliceSession.StepSendingChain(_engine);
        aliceStep.IsSuccess.Should().BeTrue();

        // 4. Bob receives Alice's initial message with Alice's ephemeral public key
        var bobResult = DirectRatchetSession.InitiateInbound(
            _bobId, _device1,
            _aliceId, _device1,
            bobSignedPreKeyPriv,
            aliceSession.LocalEphemeralPublicKey!,
            _engine);

        bobResult.IsSuccess.Should().BeTrue();
        using var bobSession = bobResult.Value;

        // 5. Bob steps his receiving chain to derive the message key
        var bobStep = bobSession.StepReceivingChain(_engine, targetCounter: 0);
        bobStep.IsSuccess.Should().BeTrue();

        // 6. Assert mutual cryptographic convergence: Alice's message key == Bob's message key!
        aliceStep.Value.Key.Span.ToArray().Should().BeEquivalentTo(bobStep.Value.Key.Span.ToArray());
    }
}
