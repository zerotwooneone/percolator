using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Common;
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
    public async Task RegisterPeer_WithoutAuthenticatedHandshake_ReturnsFailure()
    {
        var peerId = PublicIdentityId.New();
        var registration = new RelayPeerRegistration(
            peerId,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: false,
            HasActiveSignedPreKey: true);

        var regResult = await _directory.RegisterPeerAsync(registration);
        regResult.IsSuccess.Should().BeFalse();
        regResult.Error.Code.Should().Be("HANDSHAKE_MISSING");
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
    public async Task GetDiscoverablePeers_WhenRelayPolicyBlindedContactsOnly_ReturnsEmptyList()
    {
        _directory.Policy = RelayDiscoveryPolicy.BlindedContactsOnly;

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
    public async Task GetDiscoverablePeers_UsesPreKeyAvailabilityPortAndEvictsExpiredEntries()
    {
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var preKeyPort = new FakePreKeyAvailabilityPort();
        var directory = new InMemoryRelayPeerDirectory(
            _admissionGate,
            RelayDiscoveryPolicy.Open,
            preKeyPort,
            clock,
            TimeSpan.FromHours(1));

        var peerId = PublicIdentityId.New();
        preKeyPort.ActivePreKeyPeers.Add(peerId);

        await directory.RegisterPeerAsync(new RelayPeerRegistration(
            peerId,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true));

        // 1. Initially discoverable because preKeyPort reports active SPK
        (await directory.GetDiscoverablePeersAsync()).Should().ContainSingle(p => p.PeerId == peerId);

        // 2. When SPK expires dynamically on the port, peer is excluded
        preKeyPort.ActivePreKeyPeers.Remove(peerId);
        (await directory.GetDiscoverablePeersAsync()).Should().BeEmpty();

        // 3. When TTL expires, registration is evicted from memory
        preKeyPort.ActivePreKeyPeers.Add(peerId);
        clock.Advance(TimeSpan.FromHours(2));
        (await directory.GetPeerAsync(peerId)).Should().BeNull();
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

    private sealed class FakePreKeyAvailabilityPort : IPreKeyAvailabilityPort
    {
        public HashSet<PublicIdentityId> ActivePreKeyPeers { get; } = new();

        public ValueTask<bool> HasActiveSignedPreKeyAsync(PublicIdentityId peerId, CancellationToken ct = default) =>
            ValueTask.FromResult(ActivePreKeyPeers.Contains(peerId));
    }

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; }

        public FakeClock(DateTimeOffset initialUtc) => UtcNow = initialUtc;

        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }
}
