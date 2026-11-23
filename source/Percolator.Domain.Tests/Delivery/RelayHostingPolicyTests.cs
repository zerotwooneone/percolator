using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public sealed class RelayHostingPolicyTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
    }

    [Test]
    public void ToPurgePolicy_DerivesPurgePolicyMatchingEnvelopeTtl()
    {
        var customTtl = TimeSpan.FromDays(21);
        var policy = new RelayHostingPolicy(
            registrationStrategy: RelayRegistrationStrategy.ApprovalRequired,
            autoApproveLocalhost: true,
            enabledFeatures: RelayFeatureFlags.All,
            maxMailboxes: 500,
            maxMailboxBytes: 10 * 1024 * 1024,
            envelopeTtl: customTtl);

        var purgePolicy = policy.ToPurgePolicy();

        purgePolicy.DefaultTtl.Should().Be(customTtl);
        purgePolicy.MaxRetainedEnvelopes.Should().Be(500 * 100);
        purgePolicy.MaxEnvelopeSizeBytes.Should().Be(PurgePolicy.DefaultMaxEnvelopeSizeBytes);
    }

    [Test]
    public void RelayHostingPolicy_RejectsNonPositiveQuotasAndTtl()
    {
        var actZeroMailboxes = () => new RelayHostingPolicy(maxMailboxes: 0);
        actZeroMailboxes.Should().Throw<ArgumentOutOfRangeException>();

        var actNegativeMailboxBytes = () => new RelayHostingPolicy(maxMailboxBytes: -1);
        actNegativeMailboxBytes.Should().Throw<ArgumentOutOfRangeException>();

        var actZeroTtl = () => new RelayHostingPolicy(envelopeTtl: TimeSpan.Zero);
        actZeroTtl.Should().Throw<ArgumentOutOfRangeException>();

        var actNegativeOpks = () => new RelayHostingPolicy(maxOneTimePreKeysPerIdentity: 0);
        actNegativeOpks.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void RelayHostedPreKeyBundle_Create_WhenPreKeyFeatureDisabled_RejectsPublish()
    {
        var policyWithoutPreKeys = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.Profiles | RelayFeatureFlags.Blobs);

        var routingToken = BlindedRoutingToken.New();
        var deviceId = DeviceId.Primary;
        var identityKey = IdentityKey.FromSpan(new byte[32]);
        var signedPreKey = DhPublicKey.FromSpan(new byte[32]);
        var signedPreKeySig = DeviceLinkProof.FromSpan(new byte[64]);
        var signedPreKeyId = SignedPreKeyId.First;
        var opks = new List<(uint KeyId, DhPublicKey Key)>();

        var result = RelayHostedPreKeyBundle.Create(
            routingToken,
            deviceId,
            identityKey,
            signedPreKey,
            signedPreKeySig,
            signedPreKeyId,
            opks,
            policyWithoutPreKeys,
            _cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RELAY_PREKEYS_REJECTED");
    }
}
