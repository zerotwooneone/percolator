using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
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
        active.SetActiveIdentity(new IdentityRecord(Guid.NewGuid(), "default") { SelfIdentityId = new SelfId(1) }, null);

        var clock = new FixedClock { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };

        await using var ctx = new PercolatorDbContext(options, active);
        ctx.Database.EnsureCreated();

        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }

        var repo = new SqlitePendingSessionRepository(ctx, active, clock);

        var inviterKeyBytes = new byte[] { 1, 2, 3 };
        var expectedFingerprintHex = Convert.ToHexString(SHA256.HashData(inviterKeyBytes));

        var expired = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 9 }),
            requestCorrelationId: new RequestCorrelationId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            isRelayed: false,
            inviterIdentityKey: new RatchetIdentityKey(inviterKeyBytes),
            callbackEndpointHost: "example.com",
            callbackEndpointPort: 443,
            clock,
            expiresAtUtc: clock.UtcNow.AddMinutes(-1));

        var open = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 8 }),
            requestCorrelationId: new RequestCorrelationId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            isRelayed: false,
            inviterIdentityKey: new RatchetIdentityKey(inviterKeyBytes),
            callbackEndpointHost: "example.com",
            callbackEndpointPort: 443,
            clock,
            expiresAtUtc: clock.UtcNow.AddMinutes(10));

        await repo.AddAsync(expired, CancellationToken.None);
        await repo.AddAsync(open, CancellationToken.None);

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
    }
}
