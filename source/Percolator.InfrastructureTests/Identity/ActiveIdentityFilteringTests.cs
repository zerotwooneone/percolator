using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        // Create schema without active identity first
        using var schemaCtx = new PercolatorDbContext(_options);
        schemaCtx.Database.EnsureCreated();

        // Seed: two identities (1 and 2)
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        schemaCtx.SelfIdentities.Add(new SelfIdentityDbo { Id = new SelfId(1), Name = "alice", PublicIdentityId = new PublicIdentityId(Guid.NewGuid()), DeviceId = new DeviceId(1), ListeningPort = new Percolator.Identity.Model.ListeningPort(5000), LastUsedUtc = DateTimeOffset.UtcNow });
        schemaCtx.SelfIdentities.Add(new SelfIdentityDbo { Id = new SelfId(2), Name = "bob", PublicIdentityId = new PublicIdentityId(Guid.NewGuid()), DeviceId = new DeviceId(1), ListeningPort = new Percolator.Identity.Model.ListeningPort(5000), LastUsedUtc = DateTimeOffset.UtcNow });
        schemaCtx.SelfIdentityKnownPeers.Add(new SelfIdentityKnownPeerDbo { SelfIdentityId = new SelfId(1), PeerId = new Percolator.Identity.PeerId(12345) });
        schemaCtx.SelfIdentityKnownPeers.Add(new SelfIdentityKnownPeerDbo { SelfIdentityId = new SelfId(2), PeerId = new Percolator.Identity.PeerId(67890) });
        schemaCtx.PendingSessions.Add(new PendingSessionDbo { Id = Guid.NewGuid(), SelfIdentityId = new SelfId(1), RemotePeerId = new Percolator.Identity.PeerId(23456), ProtocolVersion = 1, Invitation = new byte[]{1}, State = 0, CreatedAtUtc = fixedTime });
        schemaCtx.PendingSessions.Add(new PendingSessionDbo { Id = Guid.NewGuid(), SelfIdentityId = new SelfId(2), RemotePeerId = new Percolator.Identity.PeerId(34567), ProtocolVersion = 1, Invitation = new byte[]{2}, State = 0, CreatedAtUtc = fixedTime });
        schemaCtx.DirectSessions.Add(new DirectSessionDbo { SelfIdentityId = new SelfId(1), RemotePeerId = new Percolator.Identity.PeerId(45678), SessionId = Guid.NewGuid() });
        schemaCtx.DirectSessions.Add(new DirectSessionDbo { SelfIdentityId = new SelfId(2), RemotePeerId = new Percolator.Identity.PeerId(56789), SessionId = Guid.NewGuid() });
        schemaCtx.SaveChanges();
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
        active.SetActiveIdentity(new IdentityRecord(new SelfId(999999), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "none") { ListeningPort = new Percolator.Identity.Model.ListeningPort(5000) }, null);
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
        active.SetActiveIdentity(new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "alice") { ListeningPort = new Percolator.Identity.Model.ListeningPort(5000) }, null);
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
        active.SetActiveIdentity(new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "alice") { ListeningPort = new Percolator.Identity.Model.ListeningPort(5000) }, null);
        await using (var ctx1 = new PercolatorDbContext(_options, active))
        {
            Assert.That(await ctx1.SelfIdentityKnownPeers.CountAsync(), Is.EqualTo(1));
        }
        active.SetActiveIdentity(new IdentityRecord(new SelfId(2), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "bob") { ListeningPort = new Percolator.Identity.Model.ListeningPort(5000) }, null);
        await using (var ctx2 = new PercolatorDbContext(_options, active))
        {
            Assert.That(await ctx2.SelfIdentityKnownPeers.CountAsync(), Is.EqualTo(1));
        }
    }
}
