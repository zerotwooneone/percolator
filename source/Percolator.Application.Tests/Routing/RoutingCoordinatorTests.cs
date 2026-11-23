using Percolator.Application.Routing;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.PluginSdk.Profiles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.Routing;

public sealed class TestPeerMailboxRoutingRepository : IPeerMailboxRoutingRepository
{
    private readonly List<PeerMailboxRoutingCapability> _routes = [];

    public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
    {
        _routes.Add(capability);
        return Task.CompletedTask;
    }

    public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        var found = _routes.Where(r => r.PeerId == peerId).OrderByDescending(r => r.LastSuccessAtUtc ?? r.LastObservedAtUtc).FirstOrDefault();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        IReadOnlyList<PeerMailboxRoutingCapability> list = _routes.Where(r => r.PeerId == peerId).ToList();
        return Task.FromResult(list);
    }

    public Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var best = _routes
            .Where(r => r.PeerId == peerId && r.IsAvailable(nowUtc))
            .OrderByDescending(r => r.LastSuccessAtUtc ?? r.LastObservedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(best);
    }

    public Task RecordBackpressureAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var idx = _routes.FindIndex(r => r.PeerId == peerId && r.RelayEndpoint != null && r.RelayEndpoint.Equals(relayEndpoint));
        if (idx >= 0)
        {
            _routes[idx] = _routes[idx] with { BackpressureCooldownUntilUtc = nowUtc.Add(retryAfter) };
        }
        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var idx = _routes.FindIndex(r => r.PeerId == peerId && r.RelayEndpoint != null && r.RelayEndpoint.Equals(relayEndpoint));
        if (idx >= 0)
        {
            _routes[idx] = _routes[idx] with { LastSuccessAtUtc = nowUtc, BackpressureCooldownUntilUtc = null };
        }
        return Task.CompletedTask;
    }
}

[TestFixture]
public sealed class RoutingCoordinatorTests
{
    private TestPeerMailboxRoutingRepository _routingRepo = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private RoutingCoordinator _coordinator = null!;

    private PublicIdentityId _peerId;
    private BlindedRoutingToken _token1;
    private BlindedRoutingToken _token2;
    private DeliveryToken _deliveryToken1;
    private DeliveryToken _deliveryToken2;

    [SetUp]
    public void SetUp()
    {
        _routingRepo = new TestPeerMailboxRoutingRepository();
        _timeProvider = new TestDateTimeProvider();
        _coordinator = new RoutingCoordinator(_routingRepo, _timeProvider);

        _peerId = PublicIdentityId.New();
        _token1 = BlindedRoutingToken.New();
        _token2 = BlindedRoutingToken.New();
        _deliveryToken1 = DeliveryToken.FromSpan(new byte[32]);
        _deliveryToken2 = DeliveryToken.FromSpan(new byte[32]);
    }

    [TearDown]
    public void TearDown()
    {
        _deliveryToken1.Dispose();
        _deliveryToken2.Dispose();
    }

    [Test]
    public async Task ResolveRoute_WhenRelayRouteExists_SelectsRelay()
    {
        // Arrange
        var route = new PeerMailboxRoutingCapability(
            _peerId,
            _token1,
            _deliveryToken1,
            RelayEndpoint: RelayEndpoint.Parse("relay-1.example.com"),
            LastObservedAtUtc: _timeProvider.UtcNow);
        await _routingRepo.SaveAsync(route);

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Mode.Should().Be(DeliveryRoutingMode.Relayed);
        result.Value!.TargetRelayEndpoint.Should().Be(RelayEndpoint.Parse("relay-1.example.com"));
        result.Value!.TargetRoutingToken.Should().Be(_token1);
        result.Value!.TargetDeliveryToken.Should().Be(_deliveryToken1);
    }

    [Test]
    public async Task ResolveRoute_WhenPrimaryRelayInBackpressureCooldown_SwitchesToNextAvailableRelay()
    {
        // Arrange

        // Route 1 (Most recently observed)
        var primaryRoute = new PeerMailboxRoutingCapability(
            _peerId,
            _token1,
            _deliveryToken1,
            RelayEndpoint: RelayEndpoint.Parse("relay-1.example.com"),
            LastObservedAtUtc: _timeProvider.UtcNow);

        // Route 2 (Older observed route)
        var fallbackRoute = new PeerMailboxRoutingCapability(
            _peerId,
            _token2,
            _deliveryToken2,
            RelayEndpoint: RelayEndpoint.Parse("relay-2.example.com"),
            LastObservedAtUtc: _timeProvider.UtcNow.AddMinutes(-5));

        await _routingRepo.SaveAsync(fallbackRoute);
        await _routingRepo.SaveAsync(primaryRoute);

        // Primary route is placed under 60-second backpressure cooldown
        await _routingRepo.RecordBackpressureAsync(_peerId, RelayEndpoint.Parse("relay-1.example.com"), TimeSpan.FromSeconds(60), _timeProvider.UtcNow);

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert: coordinator bypasses relay-1 and resolves to relay-2
        result.IsSuccess.Should().BeTrue();
        result.Value!.Mode.Should().Be(DeliveryRoutingMode.Relayed);
        result.Value!.TargetRelayEndpoint.Should().Be(RelayEndpoint.Parse("relay-2.example.com"));
        result.Value!.TargetRoutingToken.Should().Be(_token2);
    }

    [Test]
    public async Task ResolveRoute_WhenAllRelaysInBackpressureCooldown_ReturnsFailure()
    {
        // Arrange
        var route = new PeerMailboxRoutingCapability(
            _peerId,
            _token1,
            _deliveryToken1,
            RelayEndpoint: RelayEndpoint.Parse("relay-1.example.com"),
            LastObservedAtUtc: _timeProvider.UtcNow,
            BackpressureCooldownUntilUtc: _timeProvider.UtcNow.AddMinutes(5));
        await _routingRepo.SaveAsync(route);

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("NO_RELAY_ROUTE_AVAILABLE");
    }

    [Test]
    public async Task ResolveRoute_WhenNoDirectAndNoRelay_ReturnsFailure()
    {
        // Arrange

        // Act
        var result = await _coordinator.ResolveRouteAsync(_peerId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("NO_RELAY_ROUTE_AVAILABLE");
    }
}
