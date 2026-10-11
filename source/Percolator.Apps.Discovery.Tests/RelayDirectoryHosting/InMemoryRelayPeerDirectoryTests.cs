using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Tests.RelayDirectoryHosting;

[TestFixture]
public sealed class InMemoryRelayPeerDirectoryTests
{
    private DiscoveryAdmissionGate _admissionGate = null!;
    private InMemoryRelayPeerDirectory _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _admissionGate = new DiscoveryAdmissionGate();
        _directory = new InMemoryRelayPeerDirectory(_admissionGate, RelayDiscoveryPolicy.Open);
    }

    [Test]
    public async Task RegisterAndQuery_WhenDiscoverableAndValid_ReturnsPeer()
    {
        var peerId = PublicIdentityId.New();
        var registration = new RelayPeerRegistration(
            peerId,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true);

        var regResult = await _directory.RegisterPeerAsync(registration);
        regResult.IsSuccess.Should().BeTrue();

        var discoverable = await _directory.GetDiscoverablePeersAsync();
        discoverable.Should().ContainSingle(p => p.PeerId == peerId);
    }

    [Test]
    public async Task GetDiscoverablePeers_WhenRelayPolicyDisabled_ReturnsEmptyList()
    {
        _directory.Policy = RelayDiscoveryPolicy.Disabled;

        var peerId = PublicIdentityId.New();
        var registration = new RelayPeerRegistration(
            peerId,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true);

        await _directory.RegisterPeerAsync(registration);

        var discoverable = await _directory.GetDiscoverablePeersAsync();
        discoverable.Should().BeEmpty();
    }

    [Test]
    public async Task UpdateVisibility_ToHidden_ExcludesFromDiscoverableList()
    {
        var peerId = PublicIdentityId.New();
        var registration = new RelayPeerRegistration(
            peerId,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true);

        await _directory.RegisterPeerAsync(registration);

        var updateResult = await _directory.UpdateVisibilityAsync(peerId, PeerDiscoveryVisibility.Hidden);
        updateResult.IsSuccess.Should().BeTrue();

        var discoverable = await _directory.GetDiscoverablePeersAsync();
        discoverable.Should().NotContain(p => p.PeerId == peerId);
    }

    [Test]
    public async Task GetDiscoverablePeers_WhenMissingSignedPreKey_ExcludesPeer()
    {
        var peerId = PublicIdentityId.New();
        var registration = new RelayPeerRegistration(
            peerId,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: false); // SPK expired or missing

        await _directory.RegisterPeerAsync(registration);

        var discoverable = await _directory.GetDiscoverablePeersAsync();
        discoverable.Should().BeEmpty();
    }

    [Test]
    public async Task UpdateVisibility_ForUnknownPeer_ReturnsFailure()
    {
        var unknownId = PublicIdentityId.New();
        var result = await _directory.UpdateVisibilityAsync(unknownId, PeerDiscoveryVisibility.Discoverable);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("PEER_NOT_REGISTERED");
    }

    [Test]
    public void DiscoveryRegistrationPayload_Create_WithValidId_Succeeds()
    {
        var peerId = PublicIdentityId.New();
        var payloadResult = DiscoveryRegistrationPayload.Create(peerId, PeerDiscoveryVisibility.Discoverable);

        payloadResult.IsSuccess.Should().BeTrue();
        payloadResult.Value.PeerId.Should().Be(peerId);
        payloadResult.Value.Visibility.Should().Be(PeerDiscoveryVisibility.Discoverable);
    }

    [Test]
    public void DiscoveryRegistrationPayload_Create_WithDefaultId_Fails()
    {
        var defaultId = default(PublicIdentityId);
        var payloadResult = DiscoveryRegistrationPayload.Create(defaultId, PeerDiscoveryVisibility.Discoverable);

        payloadResult.IsSuccess.Should().BeFalse();
        payloadResult.Error.Code.Should().Be("INVALID_PEER_ID");
    }
}
