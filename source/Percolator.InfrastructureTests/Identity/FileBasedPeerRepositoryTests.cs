using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Identity;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Identity;

public class FileBasedPeerRepositoryTests
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
    public void Add_WhenCalled_MarksPeerAsTrusted()
    {
        // Arrange
        var repository = new FileBasedPeerRepository(_storageOptions);
        var trustedStore = (ITrustedPeerStore)repository;
        var publicKeyHash = new PublicKeyHash(RandomNumberGenerator.GetBytes(32));

        // Act
        trustedStore.Add(publicKeyHash);

        // Assert
        Assert.That(trustedStore.IsTrusted(publicKeyHash), Is.True);
    }

    [Test]
    public void IsTrusted_WhenHashIsPersisted_ReturnsTrue()
    {
        // Arrange
        var publicKeyHash = new PublicKeyHash(RandomNumberGenerator.GetBytes(32));
        var initialRepository = new FileBasedPeerRepository(_storageOptions);
        ((ITrustedPeerStore)initialRepository).Add(publicKeyHash);

        // Act
        var newRepository = new FileBasedPeerRepository(_storageOptions);
        var newTrustedStore = (ITrustedPeerStore)newRepository;

        // Assert
        Assert.That(newTrustedStore.IsTrusted(publicKeyHash), Is.True);
    }
}
