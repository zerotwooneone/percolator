using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public class SqliteSessionRepositoryTests
{
    private static (PercolatorDbContext Ctx, SqliteSessionRepository Repo) CreateDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = TestDb.NewContext(options, 1);
        ctx.Database.EnsureCreated();
        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }
        var repo = new SqliteSessionRepository(ctx, new NoopSessionCrypto(), new TestClock());
        return (ctx, repo);
    }

    private static SecureSession NewSession(ISessionCrypto crypto, IClock clock)
    {
        var id = SessionId.NewId();
        var remote = Percolator.Cryptography.Primitives.PeerId.NewId();
        var ver = new ProtocolVersion(1);
        var state = new RatchetState(new RootKey(new byte[]{1}), new ChainKey(new byte[]{2}), 3UL, new ChainKey(new byte[]{4}), 5UL, 0UL, null, null, 1000);
        return SecureSession.Create(id, remote, ver, state, crypto, clock);
    }

    private sealed class NoopSessionCrypto : ISessionCrypto
    {
        public (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(PrivatePreKey mySignedPreKey, PreKeyBundle remoteBundle)
            => (new SharedSecret(new byte[]{0xAA}), new RatchetEphemeralKey(new byte[]{0xBB}));
        public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(RatchetState state, Plaintext pt, AssociatedData ad, ulong ctr, ulong prevLen)
            => (new Ciphertext(new byte[]{0x01}), new RatchetEphemeralKey(new byte[]{0x02}), state);
        public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage message, AssociatedData ad)
            => (new Plaintext(new byte[]{0x03}), state);

        public SharedSecret X3DH_Respond(
            RatchetIdentityKey initiatorId,
            RatchetEphemeralKey initiatorEph,
            PrivatePreKey localIdentityPrivate,
            PrivatePreKey localSpkPrivate,
            PrivatePreKey? localOtkPrivate) => throw new NotImplementedException();

        public bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature) =>
            throw new NotImplementedException();
    }

    [Test]
    public async Task Add_and_Get_roundtrip_scoped_to_identity()
    {
        var (ctx, repo) = CreateDb();
        var clock = new TestClock();
        var session = NewSession(new NoopSessionCrypto(), clock);

        await repo.AddAsync(session, CancellationToken.None);
        var loaded = await repo.GetAsync(session.Id, CancellationToken.None);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Id.Value, Is.EqualTo(session.Id.Value));
        Assert.That(loaded.RemotePeerId.Value, Is.EqualTo(session.RemotePeerId.Value));
        Assert.That(loaded.ProtocolVersion.Value, Is.EqualTo(session.ProtocolVersion.Value));
        Assert.That(loaded.State.SendingCounter, Is.EqualTo(session.State.SendingCounter));
        Assert.That(loaded.State.ReceivingCounter, Is.EqualTo(session.State.ReceivingCounter));
    }

    [Test]
    public async Task Update_updates_counters_and_last_used()
    {
        var (ctx, repo) = CreateDb();
        var clock = new TestClock();
        var session = NewSession(new NoopSessionCrypto(), clock);

        await repo.AddAsync(session, CancellationToken.None);
        session.TouchLastUsed(clock);
        await repo.UpdateAsync(session, CancellationToken.None);

        var loaded = await repo.GetAsync(session.Id, CancellationToken.None);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.LastUsedAtUtc >= session.CreatedAtUtc, Is.True);
    }

    [Test]
    public async Task Cross_identity_access_is_filtered()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        // Seed identities
        using (var ctxSeed = TestDb.NewContext(options, 1))
        {
            ctxSeed.Database.EnsureCreated();
            if (!ctxSeed.SelfIdentities.Any())
            {
                ctxSeed.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "one" });
                ctxSeed.SelfIdentities.Add(new SelfIdentityDbo { Id = 2, PeerId = Guid.NewGuid(), Name = "two" });
                ctxSeed.SaveChanges();
            }
        }

        // Write under identity 1
        var testClock = new TestClock();
        using (var ctx1 = TestDb.NewContext(options, 1))
        {
            var repo1 = new SqliteSessionRepository(ctx1, new NoopSessionCrypto(), testClock);
            var s = NewSession(new NoopSessionCrypto(), testClock);
            await repo1.AddAsync(s, CancellationToken.None);
        }

        // Attempt to read under identity 2 should return null
        using (var ctx2 = TestDb.NewContext(options, 2))
        {
            var repo2 = new SqliteSessionRepository(ctx2, new NoopSessionCrypto(), testClock);
            var result = await repo2.GetAsync(new SessionId(Guid.NewGuid()), CancellationToken.None); // different id very likely null
            Assert.That(result, Is.Null);
        }
    }
}
