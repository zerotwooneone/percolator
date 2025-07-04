using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Identity;

namespace Percolator.IdentityTests;

[TestFixture]
public class PersistentKeyManagementServiceTests
{
    private Fixture _fixture;
    private Mock<ICredentialService> _credentialServiceMock;
    private Mock<ILogger<PersistentKeyManagementService>> _loggerMock;
    private PersistentKeyManagementService _sut;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new ECParametersJsonConverter() }
    };

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _credentialServiceMock = new Mock<ICredentialService>();
        _loggerMock = new Mock<ILogger<PersistentKeyManagementService>>();
        _sut = new PersistentKeyManagementService(_credentialServiceMock.Object, _loggerMock.Object);

        var identityName = _fixture.Create<string>();
        var basePath = IdentityPathHelper.GetBasePath(identityName);
        var keysPath = Path.Combine(basePath, "keys");
        if (Directory.Exists(keysPath))
        {
            Directory.Delete(keysPath, true);
        }
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeysDoNotExist_CreatesAndSavesNewKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keyFilePath = Path.Combine(IdentityPathHelper.GetBasePath(identityName), "keys", $"{identityName}.json");

        // Act
        var createdKeys = await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        createdKeys.Should().NotBeNull();
        createdKeys.IdentitySigningKey.Should().NotBeNull();
        createdKeys.IdentityAgreementKey.Should().NotBeNull();
        createdKeys.SignedPreKey.Should().NotBeNull();
        createdKeys.OneTimePreKeys.Should().HaveCount(100);
        _credentialServiceMock.Verify(s => s.Protect(It.IsAny<byte[]>()), Times.Once);
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeysExist_LoadsExistingKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keyFilePath = Path.Combine(IdentityPathHelper.GetBasePath(identityName), "keys", $"{identityName}.json");

        var iks = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ika = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var otps = Enumerable.Range(0, 10).Select(_ => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)).ToArray();
        var existingKeys = new X3dhKeys(iks, ika, spk, otps);

        var container = new { // Anonymous type to match KeyContainer structure
            IdentitySigningKey = iks.ExportParameters(true),
            IdentityAgreementKey = ika.ExportParameters(true),
            SignedPreKey = spk.ExportParameters(true),
            OneTimePreKeys = otps.Select(k => k.ExportParameters(true)).ToArray()
        };
        var decryptedBytes = JsonSerializer.SerializeToUtf8Bytes(container, s_jsonOptions);
        var encryptedBytes = _fixture.Create<byte[]>();

        _credentialServiceMock.Setup(s => s.Unprotect(encryptedBytes)).Returns(decryptedBytes);

        // Mock file system behavior if not using a real file system
        var directory = Path.GetDirectoryName(keyFilePath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllBytesAsync(keyFilePath, encryptedBytes);

        // Act
        var loadedKeys = await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        loadedKeys.Should().NotBeNull();
        // Compare public key parts to verify correctness
        loadedKeys.IdentitySigningKey.ExportParameters(false).Q.X.Should().BeEquivalentTo(existingKeys.IdentitySigningKey.ExportParameters(false).Q.X);
        loadedKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo().Should().BeEquivalentTo(existingKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        loadedKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo().Should().BeEquivalentTo(existingKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());

        // Clean up
        File.Delete(keyFilePath);
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeyFileIsCorrupt_ThrowsException()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keyFilePath = Path.Combine(IdentityPathHelper.GetBasePath(identityName), "keys", $"{identityName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        await File.WriteAllTextAsync(keyFilePath, "this is not valid json");

        // Act & Assert
        await _sut.Invoking(s => s.GetOrCreateKeysAsync(identityName)).Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeyFileIsProtectedCorrupt_ThrowsCryptographicException()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keyFilePath = Path.Combine(IdentityPathHelper.GetBasePath(identityName), "keys", $"{identityName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        // Simulate a file that was protected but is now corrupt (e.g., tampered with)
        await File.WriteAllBytesAsync(keyFilePath, new byte[] { 0x01, 0x02, 0x03 });

        // Setup mock credential service to throw CryptographicException on Unprotect
        _credentialServiceMock.Setup(s => s.Unprotect(It.IsAny<byte[]>()))
            .Throws(new System.Security.Cryptography.CryptographicException("Corrupt data"));

        // Act & Assert
        await _sut.Invoking(s => s.GetOrCreateKeysAsync(identityName)).Should().ThrowAsync<System.Security.Cryptography.CryptographicException>();
    }
}
