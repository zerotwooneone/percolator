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
        var self = new SelfIdentity(new SelfId(0)); // Id not assigned yet; will be set by DB
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
    public async Task GetMostRecent_returns_highest_LastUsed()
    {
        // Arrange
        await using var ctx = new PercolatorDbContext(_options);
        var repo = new SqliteSelfIdentityDomainRepository(ctx);
        var a = new SelfIdentity(new SelfId(0)); a.SetDisplayName("a"); a.TouchLastUsed(new DateTimeOffset(2025,1,1,0,0,0,TimeSpan.Zero));
        var b = new SelfIdentity(new SelfId(0)); b.SetDisplayName("b"); b.TouchLastUsed(new DateTimeOffset(2025,2,1,0,0,0,TimeSpan.Zero));
        await repo.SaveAsync(a);
        await repo.SaveAsync(b);

        // Act
        var mru = await repo.GetMostRecentAsync();

        // Assert
        Assert.That(mru, Is.Not.Null);
        Assert.That(mru!.DisplayName!.Value, Is.EqualTo("b"));
    }
}
