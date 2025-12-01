using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Identity.Model;
using Percolator.Identity;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Identity;

[TestFixture]
public class ActiveIdentityFilteringTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<PercolatorDbContext> _options = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new PercolatorDbContext(_options);
        ctx.Database.EnsureCreated();

        // Seed: two identities (1 and 2)
        ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, Name = "alice", PeerId = Guid.NewGuid() });
        ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 2, Name = "bob", PeerId = Guid.NewGuid() });
        ctx.SelfIdentityKnownPeers.Add(new SelfIdentityKnownPeerDbo { SelfIdentityId = 1, PeerId = Guid.NewGuid() });
        ctx.SelfIdentityKnownPeers.Add(new SelfIdentityKnownPeerDbo { SelfIdentityId = 2, PeerId = Guid.NewGuid() });
        ctx.PendingSessions.Add(new PendingSessionDbo { Id = Guid.NewGuid(), SelfIdentityId = 1, RemotePeerId = Guid.NewGuid(), ProtocolVersion = 1, Invitation = new byte[]{1}, State = 0, CreatedAtUtc = DateTimeOffset.UtcNow });
        ctx.PendingSessions.Add(new PendingSessionDbo { Id = Guid.NewGuid(), SelfIdentityId = 2, RemotePeerId = Guid.NewGuid(), ProtocolVersion = 1, Invitation = new byte[]{2}, State = 0, CreatedAtUtc = DateTimeOffset.UtcNow });
        ctx.DirectSessions.Add(new DirectSessionDbo { SelfIdentityId = 1, RemotePeerId = Guid.NewGuid(), SessionId = Guid.NewGuid() });
        ctx.DirectSessions.Add(new DirectSessionDbo { SelfIdentityId = 2, RemotePeerId = Guid.NewGuid(), SessionId = Guid.NewGuid() });
        ctx.SaveChanges();
    }

    [TearDown]
    public void Teardown()
    {
        _connection.Close();
    }

    [Test]
    public async Task When_ActiveIdentity_Unset_identity_scoped_sets_return_zero_rows()
    {
        // Use an ActiveIdentityContext with a non-existent SelfIdentityId to simulate 'unset' behavior
        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(Guid.NewGuid(), "none") { SelfIdentityId = new SelfId(999999) }, null);
        await using var ctx = new PercolatorDbContext(_options, active);
        var countKnownPeers = await ctx.SelfIdentityKnownPeers.CountAsync();
        var countPending = await ctx.PendingSessions.CountAsync();
        var countDirect = await ctx.DirectSessions.CountAsync();
        Assert.That(countKnownPeers, Is.EqualTo(0));
        Assert.That(countPending, Is.EqualTo(0));
        Assert.That(countDirect, Is.EqualTo(0));
    }

    [Test]
    public async Task When_ActiveIdentity_Set_filters_include_only_that_identity()
    {
        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord( Guid.NewGuid(), "alice") { SelfIdentityId = new SelfId(1) }, null);
        await using var ctx = new PercolatorDbContext(_options, active);

        var countKnownPeers = await ctx.SelfIdentityKnownPeers.CountAsync();
        var countPending = await ctx.PendingSessions.CountAsync();
        var countDirect = await ctx.DirectSessions.CountAsync();
        Assert.That(countKnownPeers, Is.EqualTo(1));
        Assert.That(countPending, Is.EqualTo(1));
        Assert.That(countDirect, Is.EqualTo(1));
    }

    [Test]
    public async Task Switching_ActiveIdentity_reflects_in_subsequent_context_instances()
    {
        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(Guid.NewGuid(), "alice") { SelfIdentityId = new SelfId(1) }, null);
        await using (var ctx1 = new PercolatorDbContext(_options, active))
        {
            Assert.That(await ctx1.SelfIdentityKnownPeers.CountAsync(), Is.EqualTo(1));
        }
        active.SetActiveIdentity(new IdentityRecord( Guid.NewGuid(), "bob") { SelfIdentityId = new SelfId(2) }, null);
        await using (var ctx2 = new PercolatorDbContext(_options, active))
        {
            Assert.That(await ctx2.SelfIdentityKnownPeers.CountAsync(), Is.EqualTo(1));
        }
    }
}
