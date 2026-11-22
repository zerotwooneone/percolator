using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Connections;
using Percolator.Application.Invitations;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Application.Tests.TestDoubles;
using Percolator.PluginSdk;

namespace Percolator.Application.Tests.Connections;

[TestFixture]
public sealed class OutboundConnectionWorkflowTests
{
    private FakePeerConnectionService _fakeConnectionService = null!;
    private InMemoryPeerMailboxRoutingRepository _routingRepo = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private OutboundConnectionWorkflow _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeConnectionService = new FakePeerConnectionService();
        _routingRepo = new InMemoryPeerMailboxRoutingRepository();
        _timeProvider = new TestDateTimeProvider();
        _sut = new OutboundConnectionWorkflow(
            _fakeConnectionService,
            _routingRepo,
            _timeProvider,
            NullLogger<OutboundConnectionWorkflow>.Instance);
    }

    [Test]
    public void Constructor_NullConnectionService_ThrowsArgumentNullException()
    {
        var act = () => new OutboundConnectionWorkflow(null!, _routingRepo, _timeProvider, NullLogger<OutboundConnectionWorkflow>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_NullRoutingRepo_ThrowsArgumentNullException()
    {
        var act = () => new OutboundConnectionWorkflow(_fakeConnectionService, null!, _timeProvider, NullLogger<OutboundConnectionWorkflow>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_NullTimeProvider_ThrowsArgumentNullException()
    {
        var act = () => new OutboundConnectionWorkflow(_fakeConnectionService, _routingRepo, null!, NullLogger<OutboundConnectionWorkflow>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new OutboundConnectionWorkflow(_fakeConnectionService, _routingRepo, _timeProvider, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task ConnectAsync_NullPayload_ThrowsArgumentNullException()
    {
        var act = () => _sut.ConnectAsync(null!, TimeSpan.FromSeconds(3));
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task ConnectAsync_WithReachablePeer_ReturnsConnectedStatus()
    {
        using var deliv = DeliveryToken.GenerateRandom();
        var peerId = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var payload = new PeerInvitationPayload(
            peerId,
            route,
            deliv,
            new List<string> { "127.0.0.1" },
            new PortRange(5100, 5100));

        _fakeConnectionService.Result = DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(
            ChannelId.New(),
            DeviceId.Primary));

        var result = await _sut.ConnectAsync(payload, TimeSpan.FromSeconds(3));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OutboundConnectionStatus.Connected);
        result.Value.PeerId.Should().Be(peerId);
        result.Value.ResolvedEndpoint.Should().Be(new Uri("https://127.0.0.1:5100/"));
        _fakeConnectionService.InvokedPeerId.Should().Be(peerId);
    }

    [Test]
    public async Task ConnectAsync_WhenProbeFails_ReturnsPendingOfflineStatus()
    {
        using var deliv = DeliveryToken.GenerateRandom();
        var peerId = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var payload = new PeerInvitationPayload(
            peerId,
            route,
            deliv,
            new List<string> { "192.168.1.100" },
            new PortRange(5200, 5200));

        _fakeConnectionService.Result = DomainResult<PeerConnectionResult>.Failure(
            new DomainError("CONNECTION_REFUSED", "Host unreachable"));

        var result = await _sut.ConnectAsync(payload, TimeSpan.FromSeconds(3));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OutboundConnectionStatus.PendingOffline);
        result.Value.PeerId.Should().Be(peerId);
    }

    [Test]
    public async Task ConnectAsync_WhenProbeTimesOut_ReturnsPendingOfflineStatus()
    {
        using var deliv = DeliveryToken.GenerateRandom();
        var peerId = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var payload = new PeerInvitationPayload(
            peerId,
            route,
            deliv,
            new List<string> { "10.0.0.5" },
            new PortRange(5150, 5150));

        _fakeConnectionService.ThrowOnConnect = new OperationCanceledException();

        var result = await _sut.ConnectAsync(payload, TimeSpan.FromMilliseconds(50));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OutboundConnectionStatus.PendingOffline);
        result.Value.PeerId.Should().Be(peerId);
    }

    [Test]
    public async Task ConnectAsync_WithInvalidHost_ReturnsDomainError()
    {
        using var deliv = DeliveryToken.GenerateRandom();
        var peerId = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var payload = new PeerInvitationPayload(
            peerId,
            route,
            deliv,
            new List<string> { "invalid host name with spaces" },
            new PortRange(5100, 5100));

        var result = await _sut.ConnectAsync(payload, TimeSpan.FromSeconds(3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_ENDPOINT");
    }

    [Test]
    public async Task ConnectAsync_WithRelayEndpoint_PersistsCapabilityAndConnectsViaRelay()
    {
        using var deliv = DeliveryToken.GenerateRandom();
        var peerId = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var relay = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);
        var payload = new PeerInvitationPayload(
            peerId,
            route,
            deliv,
            new List<string> { "127.0.0.1" },
            new PortRange(5100, 5100),
            relay);

        _fakeConnectionService.Result = DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(
            ChannelId.New(),
            DeviceId.Primary));

        var result = await _sut.ConnectAsync(payload, TimeSpan.FromSeconds(3));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OutboundConnectionStatus.Connected);
        result.Value.PeerId.Should().Be(peerId);
        result.Value.ResolvedEndpoint.Should().BeNull();
        _fakeConnectionService.InvokedPeerId.Should().Be(peerId);

        var stored = await _routingRepo.GetByPeerIdAsync(peerId);
        stored.Should().NotBeNull();
        stored!.RelayEndpoint.Should().Be(relay);
        stored.RoutingToken.Should().Be(route);
    }

    private sealed class FakePeerConnectionService : IPeerConnectionService
    {
        public DomainResult<PeerConnectionResult> Result { get; set; } =
            DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(ChannelId.New(), DeviceId.Primary));

        public Exception? ThrowOnConnect { get; set; }
        public PublicIdentityId? InvokedPeerId { get; private set; }

        public Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
            PublicIdentityId targetPeerId,
            DeviceId? targetDeviceId = null,
            RelayEndpoint? relayEndpoint = null,
            CancellationToken ct = default)
        {
            InvokedPeerId = targetPeerId;
            return Task.FromResult(Result);
        }

        public Task<DomainResult<PeerConnectionResult>> ConnectToDirectPeerAsync(
            PublicIdentityId targetPeerId,
            BlindedRoutingToken routingToken,
            DeliveryToken deliveryToken,
            Uri directEndpoint,
            DeviceId? targetDeviceId = null,
            CancellationToken ct = default)
        {
            InvokedPeerId = targetPeerId;
            if (ThrowOnConnect != null)
            {
                throw ThrowOnConnect;
            }

            return Task.FromResult(Result);
        }
    }
}
