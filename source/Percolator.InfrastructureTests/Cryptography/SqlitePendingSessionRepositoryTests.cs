using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public sealed class SqlitePendingSessionRepositoryTests
{
    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    [Test]
    public async Task Add_and_Get_roundtrip_includes_metadata()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(Guid.NewGuid(), "default") { SelfIdentityId = new SelfId(1) }, null);

        await using var ctx = new PercolatorDbContext(options, active);
        ctx.Database.EnsureCreated();

        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }

        var repo = new SqlitePendingSessionRepository(ctx, active, new TestClock());

        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 1, 2, 3 }),
            isRelayed: false,
            inviterIdentityKey: new RatchetIdentityKey(new byte[] { 9, 9, 9 }),
            callbackEndpointHost: "example.com",
            callbackEndpointPort: 443,
            new TestClock(),
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        await repo.AddAsync(pending, CancellationToken.None);

        var loaded = await repo.GetAsync(pending.Id, CancellationToken.None);
        Assert.That(loaded, Is.Not.Null);

        Assert.That(loaded!.IsRelayed, Is.EqualTo(false));
        Assert.That(loaded.InviterIdentityKey, Is.Not.Null);
        Assert.That(loaded.InviterIdentityKey!.Value, Is.EqualTo(new byte[] { 9, 9, 9 }));
        Assert.That(loaded.CallbackEndpointHost, Is.EqualTo("example.com"));
        Assert.That(loaded.CallbackEndpointPort, Is.EqualTo(443));
    }
}
