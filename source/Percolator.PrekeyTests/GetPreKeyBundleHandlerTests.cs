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
        var publicIdentityId = new PublicIdentityId(Guid.NewGuid());
        var peerId = new Percolator.Identity.PeerId(1);
        var expected = new PreKeyBundle(
            RatchetIdentityKey.FromBytes(new byte[64]),
            Guid.NewGuid(),
            PreKey.FromBytes(new byte[64]),
            Signature.FromBytes(new byte[64]),
            Guid.NewGuid(),
            OneTimeKey.FromBytes(new byte[64]),
            DateTimeOffset.UtcNow.AddHours(1));

        _publicKeyStore.Setup(s => s.GetPeerIdByPublicIdentityIdAsync(publicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerId);
        _bundleRepository.Setup(r => r.PopBundleAsync(new Percolator.Cryptography.Primitives.PeerId(peerId.Value)))
            .ReturnsAsync(expected);

        var query = new GetPreKeyBundleQuery(publicIdentityId);

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
        var publicIdentityId = new PublicIdentityId(Guid.NewGuid());
        _publicKeyStore.Setup(s => s.GetPeerIdByPublicIdentityIdAsync(publicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.PeerId?)null);
        var query = new GetPreKeyBundleQuery(publicIdentityId);

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
        var publicIdentityId = new PublicIdentityId(Guid.NewGuid());
        var peerId = new Percolator.Identity.PeerId(2);
        _publicKeyStore.Setup(s => s.GetPeerIdByPublicIdentityIdAsync(publicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerId);
        _bundleRepository.Setup(r => r.PopBundleAsync(new Percolator.Cryptography.Primitives.PeerId(peerId.Value)))
            .ReturnsAsync((PreKeyBundle?)null);

        var query = new GetPreKeyBundleQuery(publicIdentityId);

        // Act
        var result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void FromBytes_with_empty_array_throws()
    {
        // Act
        var act = () => IdentityPublicKeyHash.FromBytes(Array.Empty<byte>());

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("Expected length 32*");
    }
}
