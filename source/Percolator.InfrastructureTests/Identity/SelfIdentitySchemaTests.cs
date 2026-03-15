using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Identity;

[TestFixture]
public class SelfIdentitySchemaTests
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
    public async Task SelfIdentity_table_has_non_nullable_LastUsedUtc_column()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info('SelfIdentity');";
        using var reader = await cmd.ExecuteReaderAsync();
        var found = false;
        var notNull = false;
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1); // name
            if (string.Equals(name, "LastUsedUtc", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                notNull = reader.GetInt32(3) == 1; // notnull column
                break;
            }
        }
        Assert.That(found, Is.True, "Expected LastUsedUtc column to exist on SelfIdentity table.");
        Assert.That(notNull, Is.True, "Expected LastUsedUtc to be non-nullable.");
    }

    [Test]
    public async Task Name_is_unique_in_SelfIdentity_table()
    {
        await using (var ctx = new PercolatorDbContext(_options))
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Name = "alice", PeerId = Guid.NewGuid() });
            await ctx.SaveChangesAsync();
        }
        // Attempt to insert duplicate name should fail due to unique index
        Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await using var ctx2 = new PercolatorDbContext(_options);
            ctx2.SelfIdentities.Add(new SelfIdentityDbo { Name = "alice", PeerId = Guid.NewGuid() });
            await ctx2.SaveChangesAsync();
        });
    }
}
