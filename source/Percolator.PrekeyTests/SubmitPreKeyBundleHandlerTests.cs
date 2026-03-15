using FluentAssertions;
using Moq;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Prekey.Handlers;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.PrekeyTests;

[TestFixture]
public class SubmitPreKeyBundleHandlerTests
{
    private Mock<Percolator.Cryptography.ISigningService> _signingService = null!;
    private Mock<IPreKeyBundleRepository> _bundleRepository = null!;
    private Mock<IPeerPublicSigningKeyStore> _publicKeyStore = null!;

    private SubmitPreKeyBundleHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _signingService = new Mock<Percolator.Cryptography.ISigningService>();
        _bundleRepository = new Mock<IPreKeyBundleRepository>();
        _publicKeyStore = new Mock<IPeerPublicSigningKeyStore>();

        _sut = new SubmitPreKeyBundleHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubmitPreKeyBundleHandler>.Instance,
            _signingService.Object,
            _bundleRepository.Object,
            _publicKeyStore.Object);
    }

    [Test]
    public async Task Handle_success_stores_bundles_and_activates_key()
    {
        // Arrange
        var remotePeerId = new CryptoPeerId(Guid.NewGuid());
        var publicSigningKey = new byte[] { 1, 2, 3 };
        var signedPreKeyId = Guid.NewGuid();
        var signedPreKey = new byte[] { 9, 9, 9 };
        var signature = new byte[] { 5, 5 };
        var otk1 = new SubmitPreKeyBundleCommand.OneTimePreKey(Guid.NewGuid(), new byte[] { 10 });
        var otk2 = new SubmitPreKeyBundleCommand.OneTimePreKey(Guid.NewGuid(), new byte[] { 11 });
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        var cmd = new SubmitPreKeyBundleCommand
        {
            RemotePeerId = new NetworkPeerId(remotePeerId.Value),
            PublicSigningKey = publicSigningKey,
            SignedPreKeyId = signedPreKeyId,
            SignedPreKey = signedPreKey,
            PreKeySignature = signature,
            OneTimePreKeys = new[] { otk1, otk2 },
            Expires = expires
        };

        _signingService
            .Setup(s => s.Verify(
                It.Is<byte[]>(b => b.SequenceEqual(signedPreKey)),
                It.Is<Percolator.Cryptography.Signature>(sig => sig.Value.SequenceEqual(signature)),
                It.Is<Percolator.Cryptography.PublicKey>(pk => pk.Value.SequenceEqual(publicSigningKey))))
            .Returns(true);

        // Act
        await _sut.Handle(cmd, CancellationToken.None);

        // Assert
        _publicKeyStore.Verify(s => s.ActivateIfChangedAsync(
            It.Is<Percolator.Identity.PeerId>(p => p.Value == remotePeerId.Value),
            It.Is<byte[]>(pk => pk.SequenceEqual(publicSigningKey)),
            It.IsAny<byte[]>(), // hash
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _bundleRepository.Verify(r => r.StoreBundlesAsync(
            It.Is<Percolator.Cryptography.Primitives.PeerId>(p => p.Value == remotePeerId.Value),
            It.Is<IReadOnlyCollection<Percolator.Cryptography.PreKeyBundle>>(bundles => bundles.Count == 2)
        ), Times.Once);
    }

    [Test]
    public async Task Handle_invalid_signature_throws()
    {
        // Arrange
        var cmd = new SubmitPreKeyBundleCommand
        {
            RemotePeerId = new NetworkPeerId(Guid.NewGuid()),
            PublicSigningKey = new byte[] { 1 },
            SignedPreKeyId = Guid.NewGuid(),
            SignedPreKey = new byte[] { 2 },
            PreKeySignature = new byte[] { 3 },
            OneTimePreKeys = new[] { new SubmitPreKeyBundleCommand.OneTimePreKey(Guid.NewGuid(), new byte[] { 4 }) },
            Expires = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        _signingService.Setup(s => s.Verify(It.IsAny<byte[]>(), It.IsAny<Percolator.Cryptography.Signature>(), It.IsAny<Percolator.Cryptography.PublicKey>()))
            .Returns(false);

        // Act
        Func<Task> act = async () => await _sut.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid signed pre-key signature.");

        _bundleRepository.Verify(r => r.StoreBundlesAsync(It.IsAny<Percolator.Cryptography.Primitives.PeerId>(), It.IsAny<IReadOnlyCollection<Percolator.Cryptography.PreKeyBundle>>()), Times.Never);
    }

    [Test]
    public async Task  Handle_no_bundles_throws()
    {
        // Arrange
        var cmd = new SubmitPreKeyBundleCommand
        {
            RemotePeerId = new NetworkPeerId(Guid.NewGuid()),
            PublicSigningKey = new byte[] { 1 },
            SignedPreKeyId = Guid.NewGuid(),
            SignedPreKey = new byte[] { 2 },
            PreKeySignature = new byte[] { 3 },
            OneTimePreKeys = Array.Empty<SubmitPreKeyBundleCommand.OneTimePreKey>(),
            Expires = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        _signingService.Setup(s => s.Verify(It.IsAny<byte[]>(), It.IsAny<Percolator.Cryptography.Signature>(), It.IsAny<Percolator.Cryptography.PublicKey>()))
            .Returns(true);

        // Act
        Func<Task> act = async () => await _sut.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No valid pre-key bundles provided.");

        _bundleRepository.Verify(r => r.StoreBundlesAsync(It.IsAny<Percolator.Cryptography.Primitives.PeerId>(), It.IsAny<IReadOnlyCollection<Percolator.Cryptography.PreKeyBundle>>()), Times.Never);
    }
}
