using Percolator.Application2.Ports;
using Percolator.Application2.Routing;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.Routing;

public sealed class InMemoryPeerReachabilityService : IPeerReachabilityService
{
    private readonly HashSet<PublicIdentityId> _reachablePeers = [];
    private readonly Dictionary<PublicIdentityId, PublicIdentityId> _homeRelays = [];

    public void SetReachable(PublicIdentityId peerId, bool reachable = true)
    {
        if (reachable) _reachablePeers.Add(peerId);
        else _reachablePeers.Remove(peerId);
    }

    public void SetHomeRelay(PublicIdentityId peerId, PublicIdentityId relayId)
    {
        _homeRelays[peerId] = relayId;
    }

    public ValueTask<bool> IsDirectlyReachableAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        return ValueTask.FromResult(_reachablePeers.Contains(peerId));
    }

    public ValueTask<PublicIdentityId?> GetHomeRelayAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        if (_homeRelays.TryGetValue(peerId, out var relayId))
        {
            return ValueTask.FromResult<PublicIdentityId?>(relayId);
        }

        return ValueTask.FromResult<PublicIdentityId?>(null);
    }
}

[TestFixture]
public sealed class RoutingCoordinatorTests
{
    private InMemoryStreamRegistry _streamRegistry = null!;
    private InMemoryPeerReachabilityService _reachabilityService = null!;
    private RoutingCoordinator _coordinator = null!;

    private PublicIdentityId _peerId;
    private PublicIdentityId _relayId;

    [SetUp]
    public void SetUp()
    {
        _streamRegistry = new InMemoryStreamRegistry();
        _reachabilityService = new InMemoryPeerReachabilityService();
        _coordinator = new RoutingCoordinator(_streamRegistry, _reachabilityService);

        _peerId = PublicIdentityId.New();
        _relayId = PublicIdentityId.New();
    }

    [Test]
    public async Task ResolveRoute_WhenPeerHasActiveStream_SelectsDirect()
    {
        // Arrange
        _streamRegistry.SetStreamActive(_peerId, true);

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Mode.Should().Be(DeliveryRoutingMode.DirectPeer);
        result.Value!.TargetPeerId.Should().Be(_peerId);
    }

    [Test]
    public async Task ResolveRoute_WhenPeerOnlineAndReachable_SelectsDirect()
    {
        // Arrange
        _streamRegistry.SetStreamActive(_peerId, false);
        _reachabilityService.SetReachable(_peerId, true);

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Mode.Should().Be(DeliveryRoutingMode.DirectPeer);
    }

    [Test]
    public async Task ResolveRoute_WhenPeerOfflineOrSuspect_SelectsRelay()
    {
        // Arrange
        _streamRegistry.SetStreamActive(_peerId, false);
        _reachabilityService.SetReachable(_peerId, false);
        _reachabilityService.SetHomeRelay(_peerId, _relayId);

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Mode.Should().Be(DeliveryRoutingMode.RelayMailbox);
        result.Value!.TargetRelayId.Should().Be(_relayId);
    }

    [Test]
    public async Task ResolveRoute_WhenNoDirectAndNoRelay_ReturnsFailure()
    {
        // Arrange
        _streamRegistry.SetStreamActive(_peerId, false);
        _reachabilityService.SetReachable(_peerId, false);

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("UNRESOLVABLE_ROUTE");
    }
}
