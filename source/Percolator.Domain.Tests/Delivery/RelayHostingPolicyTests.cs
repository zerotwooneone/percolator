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

    [Test]
    public void DefaultAndHostManagedPolicies_DisableSwarmTrackerAndTransferPipeByDefault()
    {
        RelayHostingPolicy.Default.IsFeatureEnabled(RelayFeatureFlags.PreKeys).Should().BeTrue();
        RelayHostingPolicy.Default.IsFeatureEnabled(RelayFeatureFlags.Profiles).Should().BeTrue();
        RelayHostingPolicy.Default.IsFeatureEnabled(RelayFeatureFlags.Blobs).Should().BeTrue();
        RelayHostingPolicy.Default.IsFeatureEnabled(RelayFeatureFlags.Groups).Should().BeTrue();
        RelayHostingPolicy.Default.IsFeatureEnabled(RelayFeatureFlags.SwarmTracker).Should().BeFalse();
        RelayHostingPolicy.Default.IsFeatureEnabled(RelayFeatureFlags.TransferPipe).Should().BeFalse();

        RelayHostingPolicy.HostManaged.IsFeatureEnabled(RelayFeatureFlags.SwarmTracker).Should().BeFalse();
        RelayHostingPolicy.HostManaged.IsFeatureEnabled(RelayFeatureFlags.TransferPipe).Should().BeFalse();

        var optedIn = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker | RelayFeatureFlags.TransferPipe);
        optedIn.IsFeatureEnabled(RelayFeatureFlags.SwarmTracker).Should().BeTrue();
        optedIn.IsFeatureEnabled(RelayFeatureFlags.TransferPipe).Should().BeTrue();
    }

    [Test]
    public void RelayHostingPolicy_RejectsNonPositiveSwarmAndPipeQuotas()
    {
        var actSwarms = () => new RelayHostingPolicy(maxActiveSwarms: 0);
        actSwarms.Should().Throw<ArgumentOutOfRangeException>();

        var actPeers = () => new RelayHostingPolicy(maxPeersPerSwarm: 0);
        actPeers.Should().Throw<ArgumentOutOfRangeException>();

        var actSwarmTtl = () => new RelayHostingPolicy(swarmPeerEntryTtl: TimeSpan.Zero);
        actSwarmTtl.Should().Throw<ArgumentOutOfRangeException>();

        var actPipes = () => new RelayHostingPolicy(maxConcurrentPipes: 0);
        actPipes.Should().Throw<ArgumentOutOfRangeException>();

        var actRate = () => new RelayHostingPolicy(maxBytesPerSecondPerPipe: 0);
        actRate.Should().Throw<ArgumentOutOfRangeException>();

        var actSessionBytes = () => new RelayHostingPolicy(maxBytesPerSession: -1);
        actSessionBytes.Should().Throw<ArgumentOutOfRangeException>();

        var actIdle = () => new RelayHostingPolicy(pipeIdleTimeout: TimeSpan.Zero);
        actIdle.Should().Throw<ArgumentOutOfRangeException>();
    }
}
