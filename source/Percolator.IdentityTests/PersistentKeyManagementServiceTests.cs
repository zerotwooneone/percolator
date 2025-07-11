using System.Security.Cryptography;
using System.Text.Json;
using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
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
    }

    private void CleanupKeys(string identityName)
    {
        var basePath = IdentityPathHelper.GetBasePath(identityName);
        if (Directory.Exists(basePath))
        {
            Directory.Delete(basePath, true);
        }
    }

    private async Task<X3dhKeys> CreateAndSaveKeys(string identityName)
    {
        var keyFilePath = Path.Combine(IdentityPathHelper.GetBasePath(identityName), "keys", $"{identityName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);

        var iks = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ika = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var otps = Enumerable.Range(0, 10).Select(_ => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)).ToArray();

        var container = new
        {
            IdentitySigningKey = iks.ExportParameters(false),
            IdentityAgreementKey = ika.ExportParameters(false),
            SignedPreKey = spk.ExportParameters(false),
            OneTimePreKeys = otps.Select(k => k.ExportParameters(false)).ToArray()
        };

        var decryptedBytes = JsonSerializer.SerializeToUtf8Bytes(container, s_jsonOptions);
        var encryptedBytes = _fixture.Create<byte[]>();
        _credentialServiceMock.Setup(s => s.Unprotect(encryptedBytes)).Returns(decryptedBytes);

        await File.WriteAllBytesAsync(keyFilePath, encryptedBytes);

        return new X3dhKeys(iks, ika, spk, otps);
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeysDoNotExist_CreatesAndSavesNewKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        CleanupKeys(identityName);
        _credentialServiceMock.Setup(s => s.Protect(It.IsAny<byte[]>())).Returns((byte[] b) => b); // Pass-through for test

        // Act
        using var keys = await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        keys.Should().NotBeNull();

        keys.IdentitySigningKey.ExportParameters(false).Q.X.Should().NotBeNull();
        keys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo().Should().NotBeNull();
        keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo().Should().NotBeNull();
        keys.OneTimePreKeys.Should().HaveCount(10);

        _credentialServiceMock.Verify(s => s.Protect(It.IsAny<byte[]>()), Times.Once);
        CleanupKeys(identityName);
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeysExist_LoadsExistingKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        CleanupKeys(identityName);
        using var originalKeys = await CreateAndSaveKeys(identityName);
        var iksPublicX = originalKeys.IdentitySigningKey.ExportParameters(false).Q.X;
        var ikaPublic = originalKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo();
        var spkPublic = originalKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();

        // Act
        using var loadedKeys = await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        loadedKeys.Should().NotBeNull();

        loadedKeys.IdentitySigningKey.ExportParameters(false).Q.X.Should().BeEquivalentTo(iksPublicX);
        loadedKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo().Should().BeEquivalentTo(ikaPublic);
        loadedKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo().Should().BeEquivalentTo(spkPublic);

        CleanupKeys(identityName);
    }

    [Test]
    public async Task GetOrCreateKeysAsync_WhenKeyFileIsCorrupt_RecoversByCreatingNewKeys()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        CleanupKeys(identityName);
        var keyFilePath = Path.Combine(IdentityPathHelper.GetBasePath(identityName), "keys", $"{identityName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);

        var corruptBytes = "this is not valid json"u8.ToArray();
        await File.WriteAllBytesAsync(keyFilePath, corruptBytes);

        _credentialServiceMock.Setup(s => s.Unprotect(corruptBytes)).Returns(corruptBytes);
        _credentialServiceMock.Setup(s => s.Protect(It.IsAny<byte[]>())).Returns((byte[] b) => b); // For new key creation

        // Act
        using var keys = await _sut.GetOrCreateKeysAsync(identityName);

        // Assert
        keys.Should().NotBeNull();

        // Verify the error was logged
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to load or decrypt existing keys")),
                It.IsAny<JsonException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()!
            ), Times.Once);

        // Verify that new keys were indeed saved
        _credentialServiceMock.Verify(s => s.Protect(It.IsAny<byte[]>()), Times.Once);
        CleanupKeys(identityName);
    }
}
