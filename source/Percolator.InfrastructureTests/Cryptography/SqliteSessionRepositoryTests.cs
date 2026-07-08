using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Identity;
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
        var ctx = TestDb.NewContextWithSchema(options, 1);
        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo 
            { 
                Id = 1, 
                PublicIdentityId = Guid.NewGuid(), 
                Name = "default", 
                DeviceId = new DeviceId(1), 
                ListeningPort = 5000, 
                LastUsedUtc = DateTimeOffset.UtcNow
            });
            ctx.SaveChanges();
        }
        var repo = new SqliteSessionRepository(ctx, new NoopSessionCrypto(), new TestClock());
        return (ctx, repo);
    }

    private static SecureSession NewSession(ISessionCrypto crypto, IClock clock)
    {
        var id = SessionId.NewId();
        var remote = new Percolator.Cryptography.Primitives.PeerId(1);
        var ver = new ProtocolVersion(1);
        var state = new RatchetState(
            RootKey.FromBytes(new byte[32]),
            ChainKey.FromBytes(new byte[32]),
            3UL,
            ChainKey.FromBytes(new byte[32]),
            5UL,
            0UL,
            null,
            null,
            1000);
        return SecureSession.Create(id, remote, ver, state, crypto, clock);
    }

    private sealed class TestClock : IClock
    {
        private static readonly DateTimeOffset FixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => FixedTime;
    }

    private sealed class NoopSessionCrypto : ISessionCrypto
    {
        public (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(PrivatePreKey mySignedPreKey, PreKeyBundle remoteBundle)
            => (SharedSecret.FromBytes(new byte[32]), RatchetEphemeralKey.FromBytes(new byte[64]));
        public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(RatchetState state, Plaintext pt, AssociatedData ad, ulong ctr, ulong prevLen)
            => (Ciphertext.FromBytes(new byte[]{0x01}), RatchetEphemeralKey.FromBytes(new byte[64]), state);
        public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage message, AssociatedData ad)
            => (Plaintext.FromBytes(new byte[]{0x03}), state);

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

        await repo.AddAsync(session, new CryptoSelfId(1), CancellationToken.None);
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

        await repo.AddAsync(session, new CryptoSelfId(1), CancellationToken.None);
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
        using (var ctxSeed = TestDb.NewContextWithSchema(options, 1))
        {
            if (!ctxSeed.SelfIdentities.Any())
            {
                ctxSeed.SelfIdentities.Add(new SelfIdentityDbo 
                { 
                    Id = 1, 
                    PublicIdentityId = Guid.NewGuid(), 
                    Name = "one", 
                    DeviceId = new DeviceId(1), 
                    ListeningPort = 5000, 
                    LastUsedUtc = DateTimeOffset.UtcNow
                });
                ctxSeed.SelfIdentities.Add(new SelfIdentityDbo 
                { 
                    Id = 2, 
                    PublicIdentityId = Guid.NewGuid(), 
                    Name = "two", 
                    DeviceId = new DeviceId(1), 
                    ListeningPort = 5000, 
                    LastUsedUtc = DateTimeOffset.UtcNow
                });
                ctxSeed.SaveChanges();
            }
        }

        // Write under identity 1
        var testClock = new TestClock();
        using (var ctx1 = TestDb.NewContext(options, 1))
        {
            var repo1 = new SqliteSessionRepository(ctx1, new NoopSessionCrypto(), testClock);
            var s = NewSession(new NoopSessionCrypto(), testClock);
            await repo1.AddAsync(s, new CryptoSelfId(1), CancellationToken.None);
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
