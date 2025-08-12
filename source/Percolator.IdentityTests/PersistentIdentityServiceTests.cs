using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Identity;
using System.Security.Cryptography;
using Percolator.Identity.Model;

namespace Percolator.IdentityTests;

[TestFixture]
public class PersistentIdentityServiceTests
{
    private Fixture _fixture = null!;
    private Mock<IIdentityStore> _identityStoreMock = null!;
    private Mock<IKeyManagementService> _keyManagementServiceMock = null!;
    private PersistentIdentityService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _identityStoreMock = new Mock<IIdentityStore>();
        _keyManagementServiceMock = new Mock<IKeyManagementService>();
        var loggerMock = new Mock<ILogger<PersistentIdentityService>>();
        _sut = new PersistentIdentityService(_identityStoreMock.Object, _keyManagementServiceMock.Object, loggerMock.Object);
    }

    [Test]
    public async Task GetOrCreateIdentityAsync_WhenIdentityDoesNotExist_CreatesIdentityAndKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keys = new X3dhKeys(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        _identityStoreMock.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdentityRecord?)null);
        _keyManagementServiceMock.Setup(s => s.CreateKeysAsync(identityName))
            .ReturnsAsync(keys);

        // Act
        var (resultIdentity, resultKeys) = await _sut.GetOrCreateIdentityAsync(identityName);

        // Assert
        resultIdentity.Should().NotBeNull();
        resultIdentity.Name.Should().Be(identityName);
        resultKeys.Should().Be(keys);

        _identityStoreMock.Verify(s => s.StoreIdentityAsync(It.Is<IdentityRecord>(r => r.Name == identityName), It.IsAny<CancellationToken>()), Times.Once);
        _keyManagementServiceMock.Verify(s => s.CreateKeysAsync(identityName), Times.Once);
        _keyManagementServiceMock.Verify(s => s.GetKeysAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetOrCreateIdentityAsync_WhenIdentityExists_ReturnsIdentityAndGetsKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var identityRecord = _fixture.Create<IdentityRecord>() with { Name = identityName };
        var keys = new X3dhKeys(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        _identityStoreMock.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(identityRecord);
        _keyManagementServiceMock.Setup(s => s.GetKeysAsync(identityName))
            .ReturnsAsync(keys);

        // Act
        var (resultIdentity, resultKeys) = await _sut.GetOrCreateIdentityAsync(identityName);

        // Assert
        resultIdentity.Should().Be(identityRecord);
        resultKeys.Should().Be(keys);

        _identityStoreMock.Verify(s => s.StoreIdentityAsync(It.IsAny<IdentityRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        _keyManagementServiceMock.Verify(s => s.CreateKeysAsync(It.IsAny<string>()), Times.Never);
        _keyManagementServiceMock.Verify(s => s.GetKeysAsync(identityName), Times.Once);
    }

    [Test]
    public async Task CreateIdentityAsync_WhenIdentityDoesNotExist_CreatesAndStoresIdentity()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var nickname = _fixture.Create<string>();
        _identityStoreMock.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdentityRecord?)null);

        // Act
        var result = await _sut.CreateIdentityAsync(identityName, nickname);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be(identityName);
        result.Nickname.Should().Be(nickname);
        _identityStoreMock.Verify(s => s.StoreIdentityAsync(It.Is<IdentityRecord>(r => r.Name == identityName), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void CreateIdentityAsync_WhenIdentityExists_ThrowsInvalidOperationException()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var identityRecord = _fixture.Create<IdentityRecord>() with { Name = identityName };
        _identityStoreMock.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(identityRecord);

        // Act & Assert
        _sut.Invoking(s => s.CreateIdentityAsync(identityName, "nickname"))
            .Should().ThrowAsync<InvalidOperationException>();
    }
}
