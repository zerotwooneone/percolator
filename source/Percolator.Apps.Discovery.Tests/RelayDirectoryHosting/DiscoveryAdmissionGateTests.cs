using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Tests.RelayDirectoryHosting;

[TestFixture]
public sealed class DiscoveryAdmissionGateTests
{
    private readonly PublicIdentityId _validPeerId = PublicIdentityId.New();
    private DiscoveryAdmissionGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _gate = new DiscoveryAdmissionGate();
    }

    [Test]
    public void EvaluateAdmission_WhenAllCriteriaMet_ReturnsSuccess()
    {
        var result = _gate.EvaluateAdmission(
            _validPeerId,
            RelayDiscoveryPolicy.Open,
            PeerDiscoveryVisibility.Discoverable,
            hasAuthenticatedHandshake: true,
            hasActiveSignedPreKey: true);

        result.IsSuccess.Should().BeTrue();
    }

    [Test]
    public void EvaluateAdmission_WhenRelayPolicyDisabled_ReturnsFailure()
    {
        var result = _gate.EvaluateAdmission(
            _validPeerId,
            RelayDiscoveryPolicy.Disabled,
            PeerDiscoveryVisibility.Discoverable,
            hasAuthenticatedHandshake: true,
            hasActiveSignedPreKey: true);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("RELAY_DISCOVERY_DISABLED");
    }

    [Test]
    public void EvaluateAdmission_WhenPeerHidden_ReturnsFailure()
    {
        var result = _gate.EvaluateAdmission(
            _validPeerId,
            RelayDiscoveryPolicy.Open,
            PeerDiscoveryVisibility.Hidden,
            hasAuthenticatedHandshake: true,
            hasActiveSignedPreKey: true);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("PEER_HIDDEN");
    }

    [Test]
    public void EvaluateAdmission_WhenHandshakeMissing_ReturnsFailure()
    {
        var result = _gate.EvaluateAdmission(
            _validPeerId,
            RelayDiscoveryPolicy.Open,
            PeerDiscoveryVisibility.Discoverable,
            hasAuthenticatedHandshake: false,
            hasActiveSignedPreKey: true);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("HANDSHAKE_MISSING");
    }

    [Test]
    public void EvaluateAdmission_WhenSignedPreKeyMissing_ReturnsFailure()
    {
        var result = _gate.EvaluateAdmission(
            _validPeerId,
            RelayDiscoveryPolicy.Open,
            PeerDiscoveryVisibility.Discoverable,
            hasAuthenticatedHandshake: true,
            hasActiveSignedPreKey: false);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("SIGNED_PREKEY_MISSING");
    }

    [Test]
    public void EvaluateAdmission_WhenPeerIdDefault_ReturnsFailure()
    {
        var defaultPeerId = default(PublicIdentityId);

        var result = _gate.EvaluateAdmission(
            defaultPeerId,
            RelayDiscoveryPolicy.Open,
            PeerDiscoveryVisibility.Discoverable,
            hasAuthenticatedHandshake: true,
            hasActiveSignedPreKey: true);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("INVALID_CANDIDATE_ID");
    }
}
