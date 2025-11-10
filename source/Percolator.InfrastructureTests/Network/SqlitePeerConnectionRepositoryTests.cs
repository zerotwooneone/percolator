using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Identity;
using Percolator.Network;
using NUnit.Framework;

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
    public async Task Save_and_Get_roundtrip_stores_endpoints_and_certs()
    {
        using var ctx = CreateDbContext(out var conn);
        var repo = new SqlitePeerConnectionRepository(ctx);

        // Arrange: ensure PeerIdentity exists to satisfy FK
        var peerId = Percolator.Network.PeerId.NewId();
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerId.Value, Name = "peer-name", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var pc = new PeerConnection(
            peerId,
            new DirectMessagePublicKey(RandomNumberGenerator.GetBytes(32)),
            new[]
            {
                new GrpcEndPoint(new DnsEndPoint("127.0.0.1", 5001), now),
                new GrpcEndPoint(new DnsEndPoint("localhost", 5002), now.AddMinutes(-5))
            },
            new[]
            {
                new TlsCertificate(RandomNumberGenerator.GetBytes(64)),
                new TlsCertificate(RandomNumberGenerator.GetBytes(64))
            },
            now
        );

        // Act
        await repo.SaveAsync(pc);
        var loaded = await repo.GetByIdAsync(peerId);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(pc.Id);
        loaded.IdentitySigningKey!.Value.Should().BeEquivalentTo(pc.IdentitySigningKey!.Value);
        loaded.GrpcEndPoints.Should().HaveCount(2);
        loaded.TlsCertificates.Should().HaveCount(2);
    }

    [Test]
    public async Task Lookup_by_direct_message_and_tls_certificate_returns_expected()
    {
        using var ctx = CreateDbContext(out var conn);
        var repo = new SqlitePeerConnectionRepository(ctx);

        // Arrange peer identities
        var peerA = Percolator.Network.PeerId.NewId();
        var peerB = Percolator.Network.PeerId.NewId();
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerA.Value, Name = "A", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerB.Value, Name = "B", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();

        var dmA = new DirectMessagePublicKey(RandomNumberGenerator.GetBytes(32));
        var certB = new TlsCertificate(RandomNumberGenerator.GetBytes(64));
        var now = DateTimeOffset.UtcNow;

        await repo.SaveAsync(new PeerConnection(peerA, dmA, Array.Empty<GrpcEndPoint>(), Array.Empty<TlsCertificate>(), now));
        await repo.SaveAsync(new PeerConnection(peerB, null, Array.Empty<GrpcEndPoint>(), new[] { certB }, now));

        // Act
        var byDm = await repo.GetByPublicKey(dmA);
        var byCert = await repo.GetByTlsCertificateAsync(certB);

        // Assert
        byDm.Should().NotBeNull();
        byDm!.Id.Should().Be(peerA);
        byCert.Should().NotBeNull();
        byCert!.Id.Should().Be(peerB);
    }
}
