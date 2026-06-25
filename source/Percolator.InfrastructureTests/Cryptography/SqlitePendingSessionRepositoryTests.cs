using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Cryptography.Primitives.PeerId;
using System.Security.Cryptography;

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
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PublicIdentityId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }

        var repo = new SqlitePendingSessionRepository(ctx, active, new TestClock());

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterKeyBytes = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 1, 2, 3 }),
            requestCorrelationId: new RequestCorrelationId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterKeyBytes),
            callbackEndpointHost: "example.com",
            callbackEndpointPort: 443,
            new TestClock(),
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        await repo.AddAsync(pending, CancellationToken.None);

        var loaded = await repo.GetAsync(pending.Id, CancellationToken.None);
        Assert.That(loaded, Is.Not.Null);

        Assert.That(loaded!.IsRelayed, Is.EqualTo(false));
        Assert.That(loaded.InviterIdentityKey, Is.Not.Null);
        Assert.That(loaded.InviterIdentityKey!.ToArray(), Is.EqualTo(inviterKeyBytes));
        Assert.That(loaded.CallbackEndpointHost, Is.EqualTo("example.com"));
        Assert.That(loaded.CallbackEndpointPort, Is.EqualTo(443));
        Assert.That(
            loaded.RequestCorrelationId,
            Is.EqualTo(new RequestCorrelationId(Guid.Parse("11111111-1111-1111-1111-111111111111"))));
    }
}
