using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Cryptography.Primitives.PeerId;
using System.Security.Cryptography;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public sealed class SqlitePendingSessionRepositoryTests
{
    private sealed class TestClock : IClock
    {
        private static readonly DateTimeOffset FixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => FixedTime;
    }

    [Test]
    public async Task Add_and_Get_roundtrip_includes_metadata()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = TestDb.NewContextWithSchema(options, 1);

        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo 
            { 
                Id = 1, 
                PublicIdentityId = Guid.NewGuid(), 
                Name = "default", 
                DeviceId = 1, 
                ListeningPort = 5000, 
                LastUsedUtc = DateTimeOffset.UtcNow
            });
            ctx.SaveChanges();
        }

        var repo = new SqlitePendingSessionRepository(ctx, new TestClock());

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
            inviterPublicIdentityId: null,
            callbackEndpointHost: "example.com",
            callbackEndpointPort: 443,
            new TestClock(),
            expiresAtUtc: new TestClock().UtcNow.AddMinutes(5));

        await repo.AddAsync(pending, new CryptoSelfId(1), CancellationToken.None);

        var loaded = await repo.GetAsync(pending.Id, new CryptoSelfId(1), CancellationToken.None);
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
