using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Identity;
using Percolator.Identity.Model;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.IdentityTests;

[TestFixture]
public class PersistentIdentityServiceTests
{
    private Mock<IIdentityStore> _mockIdentityStore;
    private Mock<ICredentialService> _mockCredentialService;
    private Mock<ICertificateOperations> _mockCertOps;
    private Mock<IKeyManagementService> _mockKeyManagementService;
    private PersistentIdentityService _service;

    [SetUp]
    public void SetUp()
    {
        _mockIdentityStore = new Mock<IIdentityStore>();
        _mockCredentialService = new Mock<ICredentialService>();
        _mockCertOps = new Mock<ICertificateOperations>();
        _mockKeyManagementService = new Mock<IKeyManagementService>();
        _service = new PersistentIdentityService(
            _mockIdentityStore.Object,
            _mockCredentialService.Object,
            _mockCertOps.Object,
            _mockKeyManagementService.Object,
            Mock.Of<ILogger<PersistentIdentityService>>());
    }

    [Test]
    public async Task CreateIdentityAsync_Should_Create_And_Store_Identity_When_Name_Is_Unique()
    {
        // Arrange
        var identityName = "test-identity";
        var pfxPassword = new Password("password");
        var fakeCert = CreateSelfSignedCertificate(identityName);

        _mockIdentityStore.Setup(s => s.IdentityExistsAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockCredentialService.Setup(s => s.GetOrCreatePfxPassword()).Returns(pfxPassword);
        _mockCertOps.Setup(s => s.CreateTlsCertificate(identityName)).Returns(fakeCert);
        _mockIdentityStore.Setup(s => s.StoreIdentityAsync(It.IsAny<IdentityRecord>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateIdentityAsync(identityName, "nickname");

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be(identityName);
        result.Thumbprint.Should().Be(fakeCert.Thumbprint);
        _mockIdentityStore.Verify(s => s.StoreIdentityAsync(It.Is<IdentityRecord>(i => i.Name == identityName), It.IsAny<CancellationToken>()), Times.Once);
        _mockKeyManagementService.Verify(s => s.GetOrCreateKeysAsync(identityName), Times.Once);
    }

    [Test]
    public async Task CreateIdentityAsync_Should_Throw_When_Identity_Exists()
    {
        // Arrange
        var identityName = "existing-identity";
        _mockIdentityStore.Setup(s => s.IdentityExistsAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act & Assert
        await _service.Invoking(s => s.CreateIdentityAsync(identityName, null)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task LoadIdentityAsync_Should_Return_Certificate_When_Identity_Exists()
    {
        // Arrange
        var identityName = "test-identity";
        var pfxPassword = new Password("password");
        var fakeCert = CreateSelfSignedCertificate(identityName);
        var pfxBytes = fakeCert.Export(X509ContentType.Pfx, pfxPassword.Value);
        var identityRecord = new IdentityRecord(identityName, new PfxCertificate(pfxBytes), fakeCert.Thumbprint);

        _mockIdentityStore.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(identityRecord);
        _mockCredentialService.Setup(s => s.GetOrCreatePfxPassword()).Returns(pfxPassword);

        // Act
        var result = await _service.LoadIdentityAsync(identityName);

        // Assert
        result.Should().NotBeNull();
        result.Value.Thumbprint.Should().Be(fakeCert.Thumbprint);
    }

    [Test]
    public async Task LoadIdentityAsync_Should_Throw_When_Identity_Does_Not_Exist()
    {
        // Arrange
        var identityName = "non-existent-identity";
        _mockIdentityStore.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync((IdentityRecord?)null);

        // Act & Assert
        await _service.Invoking(s => s.LoadIdentityAsync(identityName)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Test]
    public async Task GetIdentityRecordAsync_Should_Return_Record_When_Identity_Exists()
    {
        // Arrange
        var identityName = "test-identity";
        var identityRecord = new IdentityRecord(identityName, new PfxCertificate(Array.Empty<byte>()), "thumbprint");
        _mockIdentityStore.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(identityRecord);

        // Act
        var result = await _service.GetIdentityRecordAsync(identityName);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be(identityName);
    }

    [Test]
    public async Task GetIdentityRecordAsync_Should_Return_Null_When_Identity_Does_Not_Exist()
    {
        // Arrange
        var identityName = "non-existent-identity";
        _mockIdentityStore.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync((IdentityRecord?)null);

        // Act
        var result = await _service.GetIdentityRecordAsync(identityName);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task ListIdentityNamesAsync_Should_Return_All_Identity_Names()
    {
        // Arrange
        var names = new List<string> { "id1", "id2", "id3" };
        _mockIdentityStore.Setup(s => s.ListIdentityNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(names);

        // Act
        var result = await _service.ListIdentityNamesAsync();

        // Assert
        result.Should().BeEquivalentTo(names);
    }

    [Test]
    public async Task LoadIdentityAsync_Should_Throw_CryptographicException_When_PfxIsCorrupt()
    {
        // Arrange
        var identityName = "corrupt-identity";
        var pfxPassword = new Password("password");
        // Create a PFX with valid password but corrupt data
        var corruptPfxBytes = new byte[] { 0x01, 0x02, 0x03 }; 
        var identityRecord = new IdentityRecord(identityName, new PfxCertificate(corruptPfxBytes), "thumbprint");

        _mockIdentityStore.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(identityRecord);
        _mockCredentialService.Setup(s => s.GetOrCreatePfxPassword()).Returns(pfxPassword);

        // Act & Assert
        await _service.Invoking(s => s.LoadIdentityAsync(identityName)).Should().ThrowAsync<System.Security.Cryptography.CryptographicException>();
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string commonName)
    {
        using var ecdsa = ECDsa.Create();
        var req = new CertificateRequest($"cn={commonName}", ecdsa, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
    }
}
