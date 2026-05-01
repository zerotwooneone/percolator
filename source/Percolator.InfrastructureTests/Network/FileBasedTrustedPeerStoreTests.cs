using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Network;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Network;

public class FileBasedTrustedPeerStoreTests
{
    private string _storagePath = null!;
    private IOptions<StorageOptions> _storageOptions = null!;

    [SetUp]
    public void SetUp()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_storagePath);
        _storageOptions = Options.Create(new StorageOptions { Path = _storagePath });
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_storagePath, true);
    }

    [Test]
    public async Task AddAsync_WhenCalled_MarksPeerAsTrusted()
    {
        // Arrange
        var trustedStore = new FileBasedTrustedPeerStore(_storageOptions);
        var publicKeyHash = PublicKeyHash.FromBytes(RandomNumberGenerator.GetBytes(32));

        // Act
        await trustedStore.AddAsync(publicKeyHash);

        // Assert
        Assert.That(trustedStore.IsTrusted(publicKeyHash), Is.True);
    }

    [Test]
    public async Task IsTrusted_WhenHashIsPersisted_ReturnsTrueAsync()
    {
        // Arrange
        var publicKeyHash = PublicKeyHash.FromBytes(RandomNumberGenerator.GetBytes(32));
        var initialStore = new FileBasedTrustedPeerStore(_storageOptions);
        await initialStore.AddAsync(publicKeyHash);

        // Act
        var newStore = new FileBasedTrustedPeerStore(_storageOptions);
        var isTrusted = newStore.IsTrusted(publicKeyHash);

        // Assert
        Assert.That(isTrusted, Is.True);
    }
}
