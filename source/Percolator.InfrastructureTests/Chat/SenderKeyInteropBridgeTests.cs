using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Chat;

[TestFixture]
[Category("InfrastructureTests")]
public class SenderKeyInteropBridgeTests
{
    [Test]
    public void SenderKeyInteropBridge_TryLoadSenderKey_ReturnsFalse_WhenRecordIsMissing()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var senderId = new PeerId(1);
        var deviceId = new DeviceId(1);
        
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        using var db = new PercolatorDbContext(options);
        var dbFactory = new DbContextFactoryWrapper(db);
        var bridge = new SenderKeyInteropBridge(dbFactory);

        // Act
        var result = bridge.TryLoadSenderKey(conversationId, senderId, deviceId, out var recordBytes);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(recordBytes, Is.Empty);
    }

    [Test]
    public void SenderKeyStatePersistenceRoundTrip_StoreThenLoad_MatchesOriginalBytes()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var senderId = new PeerId(2);
        var deviceId = new DeviceId(1);
        var originalBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        using var db = new PercolatorDbContext(options);
        var dbFactory = new DbContextFactoryWrapper(db);
        var bridge = new SenderKeyInteropBridge(dbFactory);

        // Act
        bridge.StoreSenderKey(conversationId, senderId, deviceId, originalBytes);
        var loaded = bridge.TryLoadSenderKey(conversationId, senderId, deviceId, out var loadedBytes);

        // Assert
        Assert.That(loaded, Is.True);
        Assert.That(loadedBytes, Is.EqualTo(originalBytes));
    }

    private class DbContextFactoryWrapper : IDbContextFactory<PercolatorDbContext>
    {
        private readonly PercolatorDbContext _context;

        public DbContextFactoryWrapper(PercolatorDbContext context)
        {
            _context = context;
        }

        public PercolatorDbContext CreateDbContext()
        {
            return _context;
        }
    }
}
