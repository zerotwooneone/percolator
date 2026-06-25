using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Identity;

[TestFixture]
public class SelfIdentityDomainRepositoryTests
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
    }

    [TearDown]
    public void Teardown()
    {
        _connection.Close();
    }

    [Test]
    public async Task Save_and_GetById_round_trips_LastUsed_and_Name()
    {
        // Arrange
        await using var ctx = new PercolatorDbContext(_options);
        var repo = new SqliteSelfIdentityDomainRepository(ctx);
        var self = new SelfIdentity(new SelfId(0), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), DateTimeOffset.UtcNow); // Id not assigned yet; will be set by DB
        self.SetDisplayName("alice");
        var used = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        self.TouchLastUsed(used);

        // Act
        await repo.SaveAsync(self);
        var listed = await repo.ListAsync();
        var saved = listed.Single(x => x.DisplayName!.Value == "alice");
        var byId = await repo.GetByIdAsync(saved.Id);

        // Assert
        Assert.That(byId, Is.Not.Null);
        Assert.That(byId!.DisplayName!.Value, Is.EqualTo("alice"));
        Assert.That(byId.LastUsedUtc, Is.EqualTo(used));
    }

    [Test]
    public async Task Save_and_GetById_round_trips_ActiveIdentityKeySpki()
    {
        // Arrange
        await using var ctx = new PercolatorDbContext(_options);
        var repo = new SqliteSelfIdentityDomainRepository(ctx);
        var self = new SelfIdentity(new SelfId(0), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), DateTimeOffset.UtcNow);
        
        var spki = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        // Key is active immediately
        self.AddKey(spki, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.UtcNow);

        // Act
        await repo.SaveAsync(self);
        var listed = await repo.ListAsync();
        var saved = listed.Single(x => x.PublicIdentityId == self.PublicIdentityId);
        var byId = await repo.GetByIdAsync(saved.Id);

        // Assert
        Assert.That(byId, Is.Not.Null);
        var activeKey = byId!.GetActiveKey(DateTimeOffset.UtcNow);
        Assert.That(activeKey, Is.Not.Null, "The key should be successfully rehydrated and active.");
        Assert.That(activeKey!.Spki, Is.EqualTo(spki));
    }

    [Test]
    public async Task GetMostRecent_returns_highest_LastUsed()
    {
        // Arrange
        await using var ctx = new PercolatorDbContext(_options);
        var repo = new SqliteSelfIdentityDomainRepository(ctx);
        var a = new SelfIdentity(new SelfId(0), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), new DateTimeOffset(2025,1,1,0,0,0,TimeSpan.Zero)); a.SetDisplayName("a");
        var b = new SelfIdentity(new SelfId(0), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), new DateTimeOffset(2025,2,1,0,0,0,TimeSpan.Zero)); b.SetDisplayName("b");
        await repo.SaveAsync(a);
        await repo.SaveAsync(b);

        // Act
        var mru = await repo.GetMostRecentAsync();

        // Assert
        Assert.That(mru, Is.Not.Null);
        Assert.That(mru!.DisplayName!.Value, Is.EqualTo("b"));
    }

    [Test]
    public async Task RelayMode_PersistsAndRehydrates()
    {
        // Arrange
        await using var ctx = new PercolatorDbContext(_options);
        var repo = new SqliteSelfIdentityDomainRepository(ctx);
        var self = new SelfIdentity(new SelfId(0), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), DateTimeOffset.UtcNow);
        self.SetDisplayName("relay-test");

        var relayRootKey = RelayRootKeyBytes.FromBytesOwned(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        self.EnableRelayMode(relayRootKey);

        // Act
        await repo.SaveAsync(self);
        var listed = await repo.ListAsync();
        var saved = listed.Single(x => x.DisplayName!.Value == "relay-test");
        var byId = await repo.GetByIdAsync(saved.Id);

        // Assert
        Assert.That(byId, Is.Not.Null);
        Assert.That(byId!.RelayDeliveryRootKey, Is.Not.Null, "The relay root key should be successfully rehydrated.");
        Assert.That(byId.RelayDeliveryRootKey!.Span.ToArray(), Is.EqualTo(relayRootKey.Span.ToArray()));
    }
}
