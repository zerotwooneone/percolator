using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application.Network.Handshake;
using Percolator.Infrastructure.Network.Handshake;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;

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

            var db = TestDb.NewContext(options, selfIdentityId);
            db.Database.EnsureCreated();
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
                RecipientPublicKeyHash: new byte[] { 0x01, 0x02 },
                LocalRequestId: Guid.NewGuid(),
                InitiatorEphemeralPrivateKey: new byte[] { 0xAA },
                InitialRootKey: new byte[] { 0x10, 0x20 },
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
                RemoteIdentityKeySpki: new byte[] { 0x11 });

            await store.SaveAsync(rec, CancellationToken.None);

            var results = new List<PreHandshakeRecord>();
            await foreach (var r in store.EnumeratePendingAsync(42, CancellationToken.None))
            {
                results.Add(r);
            }

            Assert.That(results.Count, Is.EqualTo(1));
            var first = results[0];
            Assert.That(first.SelfIdentityId, Is.EqualTo(42));
            Assert.That(first.RecipientPublicKeyHash, Is.EqualTo(rec.RecipientPublicKeyHash));
        }

        [Test]
        public async Task PurgeExpired_RemovesOnlyExpired()
        {
            using var db = CreateInMemoryDb(7);
            var store = new PreHandshakeSessionStore(db);

            var now = DateTimeOffset.UtcNow;
            var active = new PreHandshakeRecord(0, 7, new byte[] { 0x10 }, Guid.NewGuid(), new byte[] { 0x20 }, new byte[] { 0x30 }, now, now.AddMinutes(5), new byte[]{0xA1});
            var expired = new PreHandshakeRecord(0, 7, new byte[] { 0x11 }, Guid.NewGuid(), new byte[] { 0x21 }, new byte[] { 0x31 }, now.AddMinutes(-10), now.AddMinutes(-1), new byte[]{0xB1});

            await store.SaveAsync(active, CancellationToken.None);
            await store.SaveAsync(expired, CancellationToken.None);

            await store.PurgeExpiredAsync(7, CancellationToken.None);

            var ids = new List<long>();
            await foreach (var r in store.EnumeratePendingAsync(7, CancellationToken.None)) ids.Add(r.Id);
            Assert.That(ids.Count, Is.EqualTo(1));
        }
    }
}
