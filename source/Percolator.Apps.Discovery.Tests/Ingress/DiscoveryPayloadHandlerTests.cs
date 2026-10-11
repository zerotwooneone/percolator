using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Tests.Ingress;

[TestFixture]
public sealed class DiscoveryPayloadHandlerTests
{
    private DiscoveryAdmissionGate _admissionGate = null!;
    private InMemoryRelayPeerDirectory _directory = null!;
    private PrivateRendezvousService _rendezvousService = null!;
    private DiscoveryPayloadHandler _handler = null!;

    private readonly ChannelId _channelId = ChannelId.New();
    private readonly PublicIdentityId _aliceId = PublicIdentityId.New();
    private readonly PublicIdentityId _bobId = PublicIdentityId.New();
    private readonly DeviceId _deviceId = new(1);

    [SetUp]
    public void SetUp()
    {
        _admissionGate = new DiscoveryAdmissionGate();
        _directory = new InMemoryRelayPeerDirectory(_admissionGate, RelayDiscoveryPolicy.Open);
        _rendezvousService = new PrivateRendezvousService();

        _handler = new DiscoveryPayloadHandler(_directory, _rendezvousService);
    }

    [Test]
    public async Task HandleRegistrationAsync_WhenSenderMatchesPeerId_Succeeds()
    {
        // First register Bob in directory
        await _directory.RegisterPeerAsync(new RelayPeerRegistration(
            _bobId,
            PeerDiscoveryVisibility.Hidden,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true));

        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Discovery,
            new byte[] { 1 },
            DateTimeOffset.UtcNow);

        var payload = new DiscoveryRegistrationPayload(_bobId, PeerDiscoveryVisibility.Discoverable);
        var result = await _handler.HandleRegistrationAsync(context, payload);

        result.IsSuccess.Should().BeTrue();

        var updated = await _directory.GetPeerAsync(_bobId);
        updated.Should().NotBeNull();
        updated!.Visibility.Should().Be(PeerDiscoveryVisibility.Discoverable);
    }

    [Test]
    public async Task HandleRegistrationAsync_WhenSenderDoesNotMatchPeerId_RejectsWithUnauthorized()
    {
        // Alice attempts to alter Bob's visibility
        var context = new InboundPayloadContext(
            _channelId,
            _aliceId, // Authenticated session sender is Alice
            _deviceId,
            AppId.Discovery,
            new byte[] { 1 },
            DateTimeOffset.UtcNow);

        var payload = new DiscoveryRegistrationPayload(_bobId, PeerDiscoveryVisibility.Hidden);
        var result = await _handler.HandleRegistrationAsync(context, payload);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("UNAUTHORIZED_REGISTRATION");
    }

    [Test]
    public async Task HandleNearbyQueryAsync_ReturnsCandidatesExcludingRequester()
    {
        var peerCharlie = PublicIdentityId.New();

        // Register Bob and Charlie (both discoverable)
        await _directory.RegisterPeerAsync(new RelayPeerRegistration(_bobId, PeerDiscoveryVisibility.Discoverable, true, true));
        await _directory.RegisterPeerAsync(new RelayPeerRegistration(peerCharlie, PeerDiscoveryVisibility.Discoverable, true, true));

        // Bob queries for nearby peers
        var context = new InboundPayloadContext(
            _channelId,
            _bobId, // Requester is Bob
            _deviceId,
            AppId.Discovery,
            new byte[] { 1 },
            DateTimeOffset.UtcNow);

        var query = new NearbyDiscoveryQueryPayload(_bobId, 50);
        var responseResult = await _handler.HandleNearbyQueryAsync(context, query);

        responseResult.IsSuccess.Should().BeTrue();
        // Response should contain Charlie, but NOT Bob (the requester)
        responseResult.Value.Descriptors.Should().ContainSingle(d => d.PeerId == peerCharlie);
        responseResult.Value.Descriptors.Should().NotContain(d => d.PeerId == _bobId);
    }

    [Test]
    public async Task HandleNearbyQueryAsync_WhenRelayDisabled_ReturnsFailure()
    {
        _directory.Policy = RelayDiscoveryPolicy.Disabled;

        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Discovery,
            new byte[] { 1 },
            DateTimeOffset.UtcNow);

        var query = new NearbyDiscoveryQueryPayload(_bobId, 50);
        var responseResult = await _handler.HandleNearbyQueryAsync(context, query);

        responseResult.IsSuccess.Should().BeFalse();
        responseResult.Error.Code.Should().Be("RELAY_DISCOVERY_DISABLED");
    }

    [Test]
    public async Task HandleInboundAsync_AppIdMismatch_ReturnsFailure()
    {
        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Chat, // Mismatched AppId
            new byte[] { 1 },
            DateTimeOffset.UtcNow);

        var result = await _handler.HandleInboundAsync(context);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("APP_ID_MISMATCH");
    }
}
