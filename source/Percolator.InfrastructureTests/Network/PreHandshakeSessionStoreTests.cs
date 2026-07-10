using Microsoft.EntityFrameworkCore;
using Percolator.Application.Network.Handshake;
using Percolator.Infrastructure.Network.Handshake;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Network
{
    [TestFixture]
    public class PreHandshakeSessionStoreTests
    {
        private static PercolatorDbContext CreateInMemoryDb(int? selfIdentityId = 1)
        {
            var options = new DbContextOptionsBuilder<PercolatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = TestDb.NewContextWithSchema(options, selfIdentityId);
            return db;
        }

        [Test]
        public async Task Save_Then_EnumeratePending_ReturnsRecord()
        {
            using var db = CreateInMemoryDb(42);
            var store = new PreHandshakeSessionStore(db);

            var rec = new PreHandshakeRecord(
                Id: 0,
                SelfIdentityId: 42,
                RecipientPublicIdentityId: new Percolator.Identity.PublicIdentityId(Guid.NewGuid()),
                LocalRequestId: Guid.NewGuid(),
                InitiatorEphemeralPrivateKey: new byte[] { 0xAA },
                InitialRootKey: new byte[] { 0x10, 0x20 },
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
                RemoteIdentityKeySpki: new byte[] { 0x11 });

            await store.SaveAsync(rec, CancellationToken.None);

            var results = new List<PreHandshakeRecord>();
            await foreach (var r in store.EnumeratePendingAsync(new NetworkSelfId(42), CancellationToken.None))
            {
                results.Add(r);
            }

            Assert.That(results.Count, Is.EqualTo(1));
            var first = results[0];
            Assert.That(first.SelfIdentityId, Is.EqualTo(42));
            Assert.That(first.RecipientPublicIdentityId, Is.EqualTo(rec.RecipientPublicIdentityId));
        }

        [Test]
        public async Task PurgeExpired_RemovesOnlyExpired()
        {
            using var db = CreateInMemoryDb(7);
            var store = new PreHandshakeSessionStore(db);

            var now = DateTimeOffset.UtcNow;
            var active = new PreHandshakeRecord(0, 7, new Percolator.Identity.PublicIdentityId(Guid.NewGuid()), Guid.NewGuid(), new byte[] { 0x20 }, new byte[] { 0x30 }, now, now.AddMinutes(5), new byte[]{0xA1});
            var expired = new PreHandshakeRecord(0, 7, new Percolator.Identity.PublicIdentityId(Guid.NewGuid()), Guid.NewGuid(), new byte[] { 0x21 }, new byte[] { 0x31 }, now.AddMinutes(-10), now.AddMinutes(-1), new byte[]{0xB1});

            await store.SaveAsync(active, CancellationToken.None);
            await store.SaveAsync(expired, CancellationToken.None);

            await store.PurgeExpiredAsync(new NetworkSelfId(7), CancellationToken.None);

            var ids = new List<long>();
            await foreach (var r in store.EnumeratePendingAsync(new NetworkSelfId(7), CancellationToken.None)) ids.Add(r.Id);
            Assert.That(ids.Count, Is.EqualTo(1));
        }
    }
}
