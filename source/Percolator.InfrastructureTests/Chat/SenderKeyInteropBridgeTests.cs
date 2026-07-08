using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Identity;
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
        var senderPublicIdentityId = new CryptoPublicIdentity(Guid.NewGuid());
        var deviceId = new DeviceId(1);

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var dbFactory = new DbContextFactoryWrapper(options);
        var bridge = new SenderKeyInteropBridge(dbFactory);

        // Act
        var result = bridge.TryLoadSenderKey(conversationId, senderPublicIdentityId, deviceId, out var recordBytes);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(recordBytes, Is.Empty);
    }

    [Test]
    public void SenderKeyStatePersistenceRoundTrip_StoreThenLoad_MatchesOriginalBytes()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var senderPublicIdentityId = new CryptoPublicIdentity(Guid.NewGuid());
        var deviceId = new DeviceId(1);
        var originalBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        var dbFactory = new DbContextFactoryWrapper(options);
        var bridge = new SenderKeyInteropBridge(dbFactory);

        // Add a PeerIdentityDbo to enable the lookup
        using (var db = dbFactory.CreateDbContext())
        {
            db.PeerIdentities.Add(new PeerIdentityDbo
            {
                PeerId = 1,
                PublicIdentityId = senderPublicIdentityId.Value,
                Name = "Test",
                Version = 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        // Act
        bridge.StoreSenderKey(conversationId, senderPublicIdentityId, deviceId, originalBytes);
        var loaded = bridge.TryLoadSenderKey(conversationId, senderPublicIdentityId, deviceId, out var loadedBytes);

        // Assert
        Assert.That(loaded, Is.True);
        Assert.That(loadedBytes, Is.EqualTo(originalBytes));
    }

    private class DbContextFactoryWrapper : IDbContextFactory<PercolatorDbContext>
    {
        private readonly DbContextOptions<PercolatorDbContext> _options;

        public DbContextFactoryWrapper(DbContextOptions<PercolatorDbContext> options)
        {
            _options = options;
        }

        public PercolatorDbContext CreateDbContext()
        {
            return new PercolatorDbContext(_options);
        }
    }
}
