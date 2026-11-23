using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public sealed class RelayHostedPreKeyBundleTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
    }

    [Test]
    public void Create_And_ConsumeBundle_DispensesOneTimeKeys_AndFallsBackWhenExhausted()
    {
        // Arrange
        var routingToken = BlindedRoutingToken.New();
        var deviceId = DeviceId.Primary;
        var identityKey = IdentityKey.FromSpan(new byte[32]);
        var signedPreKey = DhPublicKey.FromSpan(new byte[32]);
        var signature = DeviceLinkProof.FromSpan(new byte[64]);

        var otpk1 = DhPublicKey.FromSpan(new byte[32]);
        var otpk2 = DhPublicKey.FromSpan(new byte[32]);
        var oneTimeKeys = new (uint, DhPublicKey)[]
        {
            (1u, otpk1),
            (2u, otpk2)
        };

        // Act: Create hosted bundle
        var createResult = RelayHostedPreKeyBundle.Create(
            routingToken,
            deviceId,
            identityKey,
            signedPreKey,
            signature,
            SignedPreKeyId.First,
            oneTimeKeys,
            RelayHostingPolicy.Default,
            _cryptoEngine);

        Assert.That(createResult.IsSuccess, Is.True);
        var bundle = createResult.Value;
        Assert.That(bundle.AvailableOneTimePreKeyCount, Is.EqualTo(2));

        // Act: Consume first OTPK
        var consume1 = bundle.ConsumeBundle();
        Assert.That(consume1.IsSuccess, Is.True);
        Assert.That(consume1.Value.OneTimePreKey, Is.Not.Null);
        Assert.That(consume1.Value.OneTimePreKeyId, Is.EqualTo(1u));
        Assert.That(bundle.AvailableOneTimePreKeyCount, Is.EqualTo(1));

        // Act: Consume second OTPK
        var consume2 = bundle.ConsumeBundle();
        Assert.That(consume2.IsSuccess, Is.True);
        Assert.That(consume2.Value.OneTimePreKey, Is.Not.Null);
        Assert.That(consume2.Value.OneTimePreKeyId, Is.EqualTo(2u));
        Assert.That(bundle.AvailableOneTimePreKeyCount, Is.EqualTo(0));

        // Act: Consume when exhausted -> fallback mode (no OTPK)
        var consumeFallback = bundle.ConsumeBundle();
        Assert.That(consumeFallback.IsSuccess, Is.True);
        Assert.That(consumeFallback.Value.OneTimePreKey, Is.Null);
        Assert.That(consumeFallback.Value.OneTimePreKeyId, Is.EqualTo(0u));
    }

    [Test]
    public void Create_WhenAcceptingPreKeysIsDisabled_ReturnsError()
    {
        // Arrange
        var policy = new RelayHostingPolicy(enabledFeatures: RelayFeatureFlags.Profiles | RelayFeatureFlags.Blobs);
        var routingToken = BlindedRoutingToken.New();

        // Act
        var result = RelayHostedPreKeyBundle.Create(
            routingToken,
            DeviceId.Primary,
            IdentityKey.FromSpan(new byte[32]),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]),
            SignedPreKeyId.First,
            [],
            policy,
            _cryptoEngine);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error.Code, Is.EqualTo("RELAY_PREKEYS_REJECTED"));
    }

    [Test]
    public void Create_WhenQuotaExceeded_ReturnsQuotaExceededError()
    {
        // Arrange: Policy with limit of 2 OTPKs
        var policy = new RelayHostingPolicy(maxOneTimePreKeysPerIdentity: 2);
        var routingToken = BlindedRoutingToken.New();

        var oneTimeKeys = new (uint, DhPublicKey)[]
        {
            (1u, DhPublicKey.FromSpan(new byte[32])),
            (2u, DhPublicKey.FromSpan(new byte[32])),
            (3u, DhPublicKey.FromSpan(new byte[32]))
        };

        // Act
        var result = RelayHostedPreKeyBundle.Create(
            routingToken,
            DeviceId.Primary,
            IdentityKey.FromSpan(new byte[32]),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]),
            SignedPreKeyId.First,
            oneTimeKeys,
            policy,
            _cryptoEngine);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error.Code, Is.EqualTo("PREKEY_QUOTA_EXCEEDED"));
    }

    [Test]
    public void ReplenishOneTimePreKeys_OverCapacity_EvictsOldestWithinCap()
    {
        // Arrange: Policy with limit of 2 OTPKs
        var policy = new RelayHostingPolicy(maxOneTimePreKeysPerIdentity: 2);
        var routingToken = BlindedRoutingToken.New();

        var bundle = RelayHostedPreKeyBundle.Create(
            routingToken,
            DeviceId.Primary,
            IdentityKey.FromSpan(new byte[32]),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]),
            SignedPreKeyId.First,
            [(1u, DhPublicKey.FromSpan(new byte[32]))],
            policy,
            _cryptoEngine).Value;

        // Act: Replenish 2 more (total 3, exceeds limit of 2) — the oldest
        // unconsumed key (id 1) evicts so the newest keys are hosted.
        var result = bundle.ReplenishOneTimePreKeys(
            [(2u, DhPublicKey.FromSpan(new byte[32])), (3u, DhPublicKey.FromSpan(new byte[32]))],
            policy);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(bundle.AvailableOneTimePreKeyCount, Is.EqualTo(2));
        Assert.That(bundle.OneTimePreKeys.Select(k => k.KeyId), Is.EqualTo(new[] { 2u, 3u }));
    }

    [Test]
    public void ReplenishOneTimePreKeys_BatchLargerThanCap_ReturnsQuotaExceededError()
    {
        // Arrange: Policy with limit of 2 OTPKs
        var policy = new RelayHostingPolicy(maxOneTimePreKeysPerIdentity: 2);
        var routingToken = BlindedRoutingToken.New();

        var bundle = RelayHostedPreKeyBundle.Create(
            routingToken,
            DeviceId.Primary,
            IdentityKey.FromSpan(new byte[32]),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]),
            SignedPreKeyId.First,
            [(1u, DhPublicKey.FromSpan(new byte[32]))],
            policy,
            _cryptoEngine).Value;

        // Act: a single incoming batch that alone exceeds the cap still rejects.
        var result = bundle.ReplenishOneTimePreKeys(
            [(2u, DhPublicKey.FromSpan(new byte[32])),
             (3u, DhPublicKey.FromSpan(new byte[32])),
             (4u, DhPublicKey.FromSpan(new byte[32]))],
            policy);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error.Code, Is.EqualTo("PREKEY_QUOTA_EXCEEDED"));
        Assert.That(bundle.AvailableOneTimePreKeyCount, Is.EqualTo(1));
    }
}
