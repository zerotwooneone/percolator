using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Application;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.InfrastructureTests.Application;

[TestFixture]
public sealed class PendingHandshakeQueriesTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    [Test]
    public async Task EnumerateOpenAsync_FiltersExpired_And_ReturnsCorrelationAndFingerprint()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "default") { ListeningPort = new Percolator.Identity.Model.ListeningPort(5000) }, null);

        var clock = new FixedClock { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };

        await using var ctx = new PercolatorDbContext(options);

        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo 
            { 
                Id = 1, 
                PublicIdentityId = Guid.NewGuid(), 
                Name = "default", 
                DeviceId = new Percolator.Identity.DeviceId(1), 
                ListeningPort = 5000, 
                LastUsedUtc = DateTimeOffset.UtcNow
            });
            ctx.SaveChanges();
        }

        var repo = new SqlitePendingSessionRepository(ctx, clock);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterKeyBytes = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var expectedFingerprintHex = Convert.ToHexString(SHA256.HashData(inviterKeyBytes));

        var expired = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 9 }),
            requestCorrelationId: new RequestCorrelationId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterKeyBytes),
            callbackEndpointHost: "example.com",
            callbackEndpointPort: 443,
            clock,
            expiresAtUtc: clock.UtcNow.AddMinutes(-1));

        var open = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 8 }),
            requestCorrelationId: new RequestCorrelationId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterKeyBytes),
            callbackEndpointHost: "example.com",
            callbackEndpointPort: 443,
            clock,
            expiresAtUtc: clock.UtcNow.AddMinutes(10));

        await repo.AddAsync(expired, new CryptoSelfId(1), CancellationToken.None);
        await repo.AddAsync(open, new CryptoSelfId(1), CancellationToken.None);

        var queries = new PendingHandshakeQueries(ctx, clock);

        var results = new List<PendingHandshake>();
        await foreach (var item in queries.EnumerateOpenAsync(CancellationToken.None))
        {
            results.Add(item);
        }

        results.Should().HaveCount(1);
        results[0].Id.Should().Be(open.Id);
        results[0].RequestCorrelationId.Value.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        results[0].InviterFingerprintHex.Should().Be(expectedFingerprintHex);
        results[0].ExpiresAtUtc.Should().Be(open.ExpiresAtUtc);
        results[0].PeerName.Should().NotBeNullOrWhiteSpace();
        results[0].IsRelayed.Should().BeFalse();
        results[0].RelayPeer.Should().BeNull();
        results[0].RelayPeerName.Should().BeNull();
        results[0].RelayEndpoint.Should().BeNull();
    }

    [Test]
    public async Task EnumerateOpenAsync_Relayed_IncludesRelayMetadata()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "default") { ListeningPort = new Percolator.Identity.Model.ListeningPort(5000) }, null);

        var clock = new FixedClock { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };

        await using var ctx = new PercolatorDbContext(options);

        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo 
            { 
                Id = 1, 
                PublicIdentityId = Guid.NewGuid(), 
                Name = "default", 
                DeviceId = new Percolator.Identity.DeviceId(1), 
                ListeningPort = 5000, 
                LastUsedUtc = DateTimeOffset.UtcNow
            });
            ctx.SaveChanges();
        }

        var repo = new SqlitePendingSessionRepository(ctx, clock);

        var remotePeerId = new Percolator.Identity.PeerId(1);
        var relayPeerId = new Percolator.Identity.PeerId(2);
        var relayPeerNetworkId = new Percolator.Network.PeerId(2);

        ctx.PeerIdentities.Add(new PeerIdentityDbo
        {
            PeerId = relayPeerId.Value,
            PublicIdentityId = Guid.NewGuid(),
            Name = "RelayHost",
            Version = 1,
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow
        });

        ctx.PeerRoutingProfiles.Add(new PeerRoutingProfileDbo
        {
            PeerId = remotePeerId.Value,
            ReachabilityStatus = 0,
            ReachabilityLastChangeUtc = clock.UtcNow,
            DirectMessagePublicKey = null
        });
        ctx.PeerRoutingProfiles.Add(new PeerRoutingProfileDbo
        {
            PeerId = relayPeerNetworkId.Value,
            ReachabilityStatus = 0,
            ReachabilityLastChangeUtc = clock.UtcNow,
            DirectMessagePublicKey = null
        });
        ctx.PeerRoutingGrpcEndPoints.Add(new GrpcEndPointRoutingDbo
        {
            PeerId = relayPeerNetworkId.Value,
            Host = "relay.local",
            Port = 5001,
            LastSeenUtc = clock.UtcNow
        });
        ctx.PeerRoutingRelays.Add(new RelayLinkDbo
        {
            PeerId = remotePeerId.Value,
            RelayPeerId = relayPeerNetworkId.Value,
            LastSeenUtc = clock.UtcNow
        });
        ctx.SaveChanges();

        var openRelayed = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new Percolator.Cryptography.Primitives.PeerId(remotePeerId.Value),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 8 }),
            requestCorrelationId: new RequestCorrelationId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            isRelayed: true,
            relayHostPeerId: new Percolator.Cryptography.Primitives.PeerId(relayPeerId.Value),
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddMinutes(10));

        await repo.AddAsync(openRelayed, new CryptoSelfId(1), CancellationToken.None);

        var queries = new PendingHandshakeQueries(ctx, clock);

        var results = new List<PendingHandshake>();
        await foreach (var item in queries.EnumerateOpenAsync(CancellationToken.None))
        {
            results.Add(item);
        }

        results.Should().HaveCount(1);
        results[0].IsRelayed.Should().BeTrue();
        results[0].RelayPeer.Should().Be(relayPeerId);
        results[0].RelayPeerName.Should().Be("RelayHost");
        results[0].RelayEndpoint.Should().Be("relay.local:5001");
    }
}
