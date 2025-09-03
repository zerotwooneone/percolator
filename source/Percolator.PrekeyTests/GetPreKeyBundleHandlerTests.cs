using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Prekey.Handlers;

namespace Percolator.PrekeyTests;

[TestFixture]
public class GetPreKeyBundleHandlerTests
{
    private Mock<IPeerPublicSigningKeyStore> _publicKeyStore = null!;
    private Mock<IPreKeyBundleRepository> _bundleRepository = null!;
    private GetPreKeyBundleHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _publicKeyStore = new Mock<IPeerPublicSigningKeyStore>();
        _bundleRepository = new Mock<IPreKeyBundleRepository>();
        _sut = new GetPreKeyBundleHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GetPreKeyBundleHandler>.Instance,
            _publicKeyStore.Object,
            _bundleRepository.Object);
    }

    [Test]
    public async Task Handle_success_returns_bundle_and_pops()
    {
        // Arrange
        var keyHash = new byte[32];
        new Random().NextBytes(keyHash);
        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var expected = new PreKeyBundle(
            new RatchetIdentityKey(new byte[] { 1 }),
            Guid.NewGuid(),
            new PreKey(new byte[] { 2 }),
            new Signature(new byte[] { 3 }),
            Guid.NewGuid(),
            new OneTimeKey(new byte[] { 4 }),
            DateTimeOffset.UtcNow.AddHours(1));

        _publicKeyStore.Setup(s => s.GetPeerIdByPublicKeyHashAsync(keyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerId);
        _bundleRepository.Setup(r => r.PopBundleAsync(new Percolator.Cryptography.Primitives.PeerId(peerId.Value)))
            .ReturnsAsync(expected);

        var query = new GetPreKeyBundleQuery { TargetPublicSigningKeyHash = keyHash };

        // Act
        var result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().Be(expected);
        _bundleRepository.Verify(r => r.PopBundleAsync(new Percolator.Cryptography.Primitives.PeerId(peerId.Value)), Times.Once);
    }

    [Test]
    public async Task Handle_unknown_key_hash_returns_null()
    {
        // Arrange
        var keyHash = new byte[32];
        _publicKeyStore.Setup(s => s.GetPeerIdByPublicKeyHashAsync(keyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.PeerId?)null);
        var query = new GetPreKeyBundleQuery { TargetPublicSigningKeyHash = keyHash };

        // Act
        var result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        _bundleRepository.Verify(r => r.PopBundleAsync(It.IsAny<Percolator.Cryptography.Primitives.PeerId>()), Times.Never);
    }

    [Test]
    public async Task Handle_no_bundle_returns_null()
    {
        // Arrange
        var keyHash = new byte[32];
        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        _publicKeyStore.Setup(s => s.GetPeerIdByPublicKeyHashAsync(keyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerId);
        _bundleRepository.Setup(r => r.PopBundleAsync(new Percolator.Cryptography.Primitives.PeerId(peerId.Value)))
            .ReturnsAsync((PreKeyBundle?)null);

        var query = new GetPreKeyBundleQuery { TargetPublicSigningKeyHash = keyHash };

        // Act
        var result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void Handle_missing_hash_throws()
    {
        // Arrange
        var query = new GetPreKeyBundleQuery { TargetPublicSigningKeyHash = Array.Empty<byte>() };

        // Act
        Func<Task> act = async () => await _sut.Handle(query, CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<InvalidOperationException>();
    }
}
