using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.Infrastructure.Identity;
using System.Threading.Tasks;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Identity;

[TestFixture]
public class SqlitePeerRepositoryTests
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

        using var context = new PercolatorDbContext(_options);
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Close();
    }

    [Test]
    public async Task AddAsync_ShouldAddPeerToDatabase()
    {
        // Arrange
        var peer = new Peer(new PeerId(Guid.NewGuid()), "test-peer");
        await using var context = new PercolatorDbContext(_options);
        var repository = new SqlitePeerRepository(context);

        // Act
        await repository.AddAsync(peer);

        // Assert
        await using var assertContext = new PercolatorDbContext(_options);
        var retrievedPeer = await assertContext.Peers.FindAsync(peer.Id);
        Assert.That(retrievedPeer, Is.Not.Null);
        Assert.That(retrievedPeer.Id, Is.EqualTo(peer.Id));
        Assert.That(retrievedPeer.Name, Is.EqualTo(peer.Name));
    }

    [Test]
    public async Task GetByIdAsync_ShouldReturnCorrectPeer_WhenPeerExists()
    {
        // Arrange
        var peer = new Peer(new PeerId(Guid.NewGuid()), "test-peer-for-get");
        await using (var setupContext = new PercolatorDbContext(_options))
        {
            setupContext.Peers.Add(peer);
            await setupContext.SaveChangesAsync();
        }

        // Act
        await using var actContext = new PercolatorDbContext(_options);
        var repository = new SqlitePeerRepository(actContext);
        var retrievedPeer = await repository.GetByIdAsync(peer.Id);

        // Assert
        Assert.That(retrievedPeer, Is.Not.Null);
        Assert.That(retrievedPeer!.Id, Is.EqualTo(peer.Id));
        Assert.That(retrievedPeer.Name, Is.EqualTo(peer.Name));
    }

    [Test]
    public async Task GetByNameAsync_ShouldReturnCorrectPeer_WhenPeerExists()
    {
        // Arrange
        var peerName = "test-peer-for-get-by-name";
        var peer = new Peer(new PeerId(Guid.NewGuid()), peerName);
        await using (var setupContext = new PercolatorDbContext(_options))
        {
            setupContext.Peers.Add(peer);
            await setupContext.SaveChangesAsync();
        }

        // Act
        await using var actContext = new PercolatorDbContext(_options);
        var repository = new SqlitePeerRepository(actContext);
        var retrievedPeer = await repository.GetByNameAsync(peerName);

        // Assert
        Assert.That(retrievedPeer, Is.Not.Null);
        Assert.That(retrievedPeer!.Id, Is.EqualTo(peer.Id));
        Assert.That(retrievedPeer.Name, Is.EqualTo(peer.Name));
    }

    [Test]
    public async Task RemoveAsync_ShouldRemovePeerFromDatabase()
    {
        // Arrange
        var peerId = new PeerId(Guid.NewGuid());
        var peer = new Peer(peerId, "peer-to-remove");
        await using (var setupContext = new PercolatorDbContext(_options))
        {
            setupContext.Peers.Add(peer);
            await setupContext.SaveChangesAsync();
        }

        // Act
        await using (var actContext = new PercolatorDbContext(_options))
        {
            var repository = new SqlitePeerRepository(actContext);
            await repository.RemoveAsync(peerId);
        }

        // Assert
        await using var assertContext = new PercolatorDbContext(_options);
        var retrievedPeer = await assertContext.Peers.FindAsync(peerId);
        Assert.That(retrievedPeer, Is.Null);
    }
}
