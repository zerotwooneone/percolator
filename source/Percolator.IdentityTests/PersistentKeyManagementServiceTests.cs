using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Identity;
using System.IO;
using System.Threading.Tasks;

namespace Percolator.IdentityTests;

[TestFixture]
public class PersistentKeyManagementServiceTests
{
    private Fixture _fixture;
    private Mock<ICredentialService> _mockCredentialService;
    private Mock<ILogger<PersistentKeyManagementService>> _mockLogger;
    private PersistentKeyManagementService _sut;
    private string _testKeysPath;

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _mockCredentialService = new Mock<ICredentialService>();
        _mockLogger = new Mock<ILogger<PersistentKeyManagementService>>();

        // Create a temporary directory for test keys
        _testKeysPath = Path.Combine(Path.GetTempPath(), "PercolatorTests", _fixture.Create<string>());
        Directory.CreateDirectory(_testKeysPath);

        // Setup mock credential service to return unprotected data as is for simplicity
        _mockCredentialService.Setup(s => s.Protect(It.IsAny<byte[]>()))
            .Returns((byte[] data) => data);
        _mockCredentialService.Setup(s => s.Unprotect(It.IsAny<byte[]>()))
            .Returns((byte[] data) => data);

        _sut = new PersistentKeyManagementService(_mockCredentialService.Object, _mockLogger.Object);
        // Override the keys path to use the temporary test directory
        var keysPathField = typeof(PersistentKeyManagementService).GetField("_keysPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        keysPathField!.SetValue(_sut, _testKeysPath);
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(_testKeysPath))
        {
            Directory.Delete(_testKeysPath, true);
        }
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeysDoNotExist_CreatesAndSavesNewKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();

        // Act
        var result = await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        result.Should().NotBeNull();
        result.IdentitySigningKey.Should().NotBeNull();
        result.IdentityAgreementKey.Should().NotBeNull();
        result.SignedPreKey.Should().NotBeNull();
        result.OneTimePreKey.Should().NotBeNull();

        // Verify that keys were saved to a file
        var keyFilePath = Path.Combine(_testKeysPath, $"{identityName}.keys");
        File.Exists(keyFilePath).Should().BeTrue();
        _mockCredentialService.Verify(s => s.Protect(It.IsAny<byte[]>()), Times.Once);
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeysExist_LoadsExistingKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        // First call to create the keys
        await _sut.GetOrCreateKeysAsync(identityName);

        // Reset the mock to verify calls on the second run
        _mockCredentialService.Invocations.Clear();

        // Act
        var result = await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        result.Should().NotBeNull();
        _mockCredentialService.Verify(s => s.Unprotect(It.IsAny<byte[]>()), Times.Once);
        _mockCredentialService.Verify(s => s.Protect(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeyFileIsCorrupt_ThrowsException()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keyFilePath = Path.Combine(_testKeysPath, $"{identityName}.keys");
        await File.WriteAllTextAsync(keyFilePath, "this is not valid json");

        // Act
        Func<Task> act = async () => await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
    }
}
