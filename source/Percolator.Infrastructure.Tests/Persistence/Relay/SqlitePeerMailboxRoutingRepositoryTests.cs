namespace Percolator.Infrastructure.Tests.Persistence.Relay;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using Percolator.PluginSdk;

[TestFixture]
public class SqlitePeerMailboxRoutingRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqlitePeerMailboxRoutingRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqlitePeerMailboxRoutingRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task UpdateRouting_PersistsEndpoint()
    {
        // Arrange
        var peerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.FromSpan(new byte[32]);
        var relayEndpoint = RelayEndpoint.Parse("relay://relay.example.com:8443");
        var now = DateTimeOffset.UtcNow;

        var capability = new PeerMailboxRoutingCapability(
            peerId,
            routingToken,
            deliveryToken,
            relayEndpoint,
            LastObservedAtUtc: now);

        // Act - Save capability
        await _repository.SaveAsync(capability);

        // Assert - Get best route
        var route = await _repository.GetBestRouteAsync(peerId, now);
        route.Should().NotBeNull();
        route!.PeerId.Should().Be(peerId);
        route.RelayEndpoint.Should().Be(relayEndpoint);
        route.RoutingToken.Should().Be(routingToken);

        // Act - Record backpressure
        await _repository.RecordBackpressureAsync(peerId, relayEndpoint, TimeSpan.FromMinutes(10), now);

        // Assert - Backpressured route is not returned as best route during cooldown
        var bestDuringCooldown = await _repository.GetBestRouteAsync(peerId, now);
        bestDuringCooldown.Should().BeNull();

        // But is available after cooldown
        var bestAfterCooldown = await _repository.GetBestRouteAsync(peerId, now.AddMinutes(11));
        bestAfterCooldown.Should().NotBeNull();

        // Record success clears backpressure
        await _repository.RecordSuccessAsync(peerId, relayEndpoint, now.AddMinutes(12));
        var bestAfterSuccess = await _repository.GetBestRouteAsync(peerId, now.AddMinutes(12));
        bestAfterSuccess.Should().NotBeNull();
        bestAfterSuccess!.BackpressureCooldownUntilUtc.Should().BeNull();
    }
}
