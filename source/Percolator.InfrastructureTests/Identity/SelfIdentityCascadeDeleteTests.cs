using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Identity;

[TestFixture]
public class SelfIdentityCascadeDeleteTests
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
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Close();
    }

    [Test]
    public async Task Deleting_SelfIdentity_Should_Cascade_Delete_SelfIdentityKeys()
    {
        // Arrange: create identity and keys
        await using (var setup = new PercolatorDbContext(_options))
        {
            var self = new SelfIdentityDbo { Name = "alice", PublicIdentityId = Guid.NewGuid() };
            setup.SelfIdentities.Add(self);
            await setup.SaveChangesAsync();

            var keys = new SelfIdentityKeysDbo
            {
                SelfIdentityId = self.Id,
                IdentitySigningKey = new byte[] { 1, 2, 3 },
                SignedPreKey = new byte[] { 7, 8, 9 }
            };
            setup.SelfIdentityKeys.Add(keys);
            await setup.SaveChangesAsync();
        }

        // Act: delete identity
        await using (var act = new PercolatorDbContext(_options))
        {
            var self = await act.SelfIdentities.FirstAsync();
            act.SelfIdentities.Remove(self);
            await act.SaveChangesAsync();
        }

        // Assert: keys are gone
        await using (var assertCtx = new PercolatorDbContext(_options))
        {
            var remainingKeys = await assertCtx.SelfIdentityKeys.ToListAsync();
            Assert.That(remainingKeys, Is.Empty);
        }
    }
}
