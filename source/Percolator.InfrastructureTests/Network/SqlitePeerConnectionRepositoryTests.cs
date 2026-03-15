using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Identity;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Network;

[TestFixture]
public class SqlitePeerConnectionRepositoryTests
{
    private static PercolatorDbContext CreateDbContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .Options;

        var ctx = new PercolatorDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Test]
    public async Task Upsert_and_Get_roundtrip_stores_endpoints_and_key()
    {
        using var ctx = CreateDbContext(out var conn);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);

        // Arrange: ensure PeerIdentity exists to satisfy FK
        var peerId = Percolator.Network.PeerId.NewId();
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerId.Value, Name = "peer-name", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(peerId);
        profile.SetIdentityPublicKey(new Percolator.Network.ValueObjects.IdentityPublicKey(RandomNumberGenerator.GetBytes(32)));
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("127.0.0.1", 5001), now), now);
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("localhost", 5002), now.AddMinutes(-5)), now.AddMinutes(-5));

        // Act
        await repo.UpsertAsync(profile);
        var loaded = await repo.GetByIdAsync(peerId);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(profile.Id);
        loaded.IdentityPublicKey!.Value.Should().BeEquivalentTo(profile.IdentityPublicKey!.Value);
        loaded.Endpoints.Should().HaveCount(2);
    }

    [Test]
    public async Task GetById_returns_expected_profile()
    {
        using var ctx = CreateDbContext(out var conn);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);

        // Arrange peer identities
        var peerA = Percolator.Network.PeerId.NewId();
        var peerB = Percolator.Network.PeerId.NewId();
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerA.Value, Name = "A", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerB.Value, Name = "B", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var profileA = new PeerRoutingProfile(); profileA.BindIdentity(peerA); profileA.SetIdentityPublicKey(new Percolator.Network.ValueObjects.IdentityPublicKey(RandomNumberGenerator.GetBytes(32))); profileA.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("localhost", 5001), now), now);
        var profileB = new PeerRoutingProfile(); profileB.BindIdentity(peerB); profileB.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("localhost", 5002), now), now);
        await repo.UpsertAsync(profileA);
        await repo.UpsertAsync(profileB);

        // Act
        var byIdA = await repo.GetByIdAsync(peerA);
        var byIdB = await repo.GetByIdAsync(peerB);

        // Assert
        byIdA.Should().NotBeNull();
        byIdA!.Id.Should().Be(peerA);
        byIdB.Should().NotBeNull();
        byIdB!.Id.Should().Be(peerB);
    }
}
