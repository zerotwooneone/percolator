using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Identity;
using System.Security.Cryptography;
using System.Text.Json;

namespace Percolator.IdentityTests;

[TestFixture]
public class PersistentKeyManagementServiceTests
{
    private Fixture _fixture = null!;
    private Mock<ICredentialService> _credentialServiceMock = null!;
    private Mock<ILogger<PersistentKeyManagementService>> _loggerMock = null!;
    private PersistentKeyManagementService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _credentialServiceMock = new Mock<ICredentialService>();
        _loggerMock = new Mock<ILogger<PersistentKeyManagementService>>();
        _sut = new PersistentKeyManagementService(_credentialServiceMock.Object, _loggerMock.Object);
    }

    [Test]
    public async Task GetKeysAsync_WhenKeysExist_LoadsExistingKeys()
    {
        var identityName = _fixture.Create<string>();
        CleanupKeys(identityName);
        var keyFilePath = GetKeyFilePath(identityName);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);

        byte[] decryptedBytes;
        string originalKeyJson;
        using (var originalIks = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var originalIka = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        using (var originalSpk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        using (var originalOtps = new DisposableArray<ECDiffieHellman>(Enumerable.Range(0, 10).Select(_ => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)).ToArray()))
        {
            var originalContainer = new PersistentKeyManagementService.KeyContainer(
                originalIks.ExportParameters(true),
                originalIka.ExportParameters(true),
                originalSpk.ExportParameters(true),
                originalOtps.Select(k => k.ExportParameters(true)).ToArray()
            );
            decryptedBytes = JsonSerializer.SerializeToUtf8Bytes(originalContainer, new JsonSerializerOptions { Converters = { new ECParametersJsonConverter() } });
            originalKeyJson = JsonSerializer.Serialize(originalContainer, new JsonSerializerOptions { Converters = { new ECParametersJsonConverter() } });
        }

        var encryptedBytes = _fixture.Create<byte[]>();
        _credentialServiceMock.Setup(s => s.Unprotect(encryptedBytes)).Returns(decryptedBytes);
        await File.WriteAllBytesAsync(keyFilePath, encryptedBytes);

        // Act
        var loadedKeys = await _sut.GetKeysAsync(identityName);

        // Assert
        loadedKeys.Should().NotBeNull();
        var loadedContainer = new PersistentKeyManagementService.KeyContainer(
            loadedKeys.IdentitySigningKey.ExportParameters(true),
            loadedKeys.IdentityAgreementKey.ExportParameters(true),
            loadedKeys.SignedPreKey.ExportParameters(true),
            loadedKeys.OneTimePreKeys.Select(k => k.ExportParameters(true)).ToArray()
        );
        var loadedKeyJson = JsonSerializer.Serialize(loadedContainer, new JsonSerializerOptions { Converters = { new ECParametersJsonConverter() } });

        loadedKeyJson.Should().Be(originalKeyJson);

        CleanupKeys(identityName);
    }

    [Test]
    public void GetKeysAsync_WhenKeysDoNotExist_ThrowsFileNotFoundException()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keyFilePath = GetKeyFilePath(identityName);
        CleanupKeys(identityName);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);

        // Act & Assert
        Assert.ThrowsAsync<FileNotFoundException>(() => _sut.GetKeysAsync(identityName));
    }

    [Test]
    public Task GetKeysAsync_WhenKeyFileIsCorrupt_ThrowsJsonException()
    {
        // Arrange
        var identityName = _fixture.Create<string>();
        var keyFilePath = GetKeyFilePath(identityName);
        CleanupKeys(identityName);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        File.WriteAllText(keyFilePath, "corrupt data");

        // Act & Assert
        Assert.ThrowsAsync<JsonException>(() => _sut.GetKeysAsync(identityName));
        return Task.CompletedTask;
    }

    [Test]
    public async Task CreateKeysAsync_WhenCalled_CreatesAndSavesNewKeys()
    {
        var identityName = _fixture.Create<string>();
        CleanupKeys(identityName);

        var protectedBytes = _fixture.Create<byte[]>();
        _credentialServiceMock.Setup(s => s.Protect(It.IsAny<byte[]>())).Returns(protectedBytes);

        // Act
        var keys = await _sut.CreateKeysAsync(identityName);

        // Assert
        keys.Should().NotBeNull();
        _credentialServiceMock.Verify(s => s.Protect(It.IsAny<byte[]>()), Times.Once);

        var keyFilePath = GetKeyFilePath(identityName);
        File.Exists(keyFilePath).Should().BeTrue();
        var writtenBytes = await File.ReadAllBytesAsync(keyFilePath);
        writtenBytes.Should().BeEquivalentTo(protectedBytes);

        CleanupKeys(identityName);
    }

    private void CleanupKeys(string identityName)
    {
        var path = GetKeyFilePath(identityName);
        var dir = Path.GetDirectoryName(path);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private string GetKeyFilePath(string identityName)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", identityName, "keys.json");
    }

    private class DisposableArray<T> : IDisposable, IEnumerable<T> where T : IDisposable
    {
        private readonly T[] _array;
        public DisposableArray(T[] array) => _array = array;
        public void Dispose() { foreach (var item in _array) item.Dispose(); }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_array).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _array.GetEnumerator();
    }
}
