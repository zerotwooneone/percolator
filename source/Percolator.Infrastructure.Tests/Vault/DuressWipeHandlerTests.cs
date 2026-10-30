namespace Percolator.Infrastructure.Tests.Vault;

using Percolator.Infrastructure.Vault;

[TestFixture]
public class DuressWipeHandlerTests
{
    private string _tempDir = null!;
    private VaultStorageOptions _options = null!;
    private DuressWipeHandler _wipeHandler = null!;
    private ProtobufHeaderStorage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "percolator_wipe_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new VaultStorageOptions { BaseDirectory = _tempDir };
        _wipeHandler = new DuressWipeHandler(_options);
        _storage = new ProtobufHeaderStorage(_options);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task ExecuteWipeAsync_DestroysHeaderAndDeletesDatabaseAndCacheFiles()
    {
        // Arrange
        // 1. Create dummy header
        byte[] dummyHeader = new byte[140];
        Array.Fill(dummyHeader, (byte)0xAB);
        await File.WriteAllBytesAsync(_options.HeaderFilePath, dummyHeader);

        // 2. Create dummy db, wal, shm files
        await File.WriteAllBytesAsync(_options.DatabaseFilePath, [0x01, 0x02]);
        await File.WriteAllBytesAsync(_options.DatabaseFilePath + "-wal", [0x03, 0x04]);
        await File.WriteAllBytesAsync(_options.DatabaseFilePath + "-shm", [0x05, 0x06]);

        // 3. Create dummy cache directory and files
        Directory.CreateDirectory(_options.CacheDirectoryPath);
        await File.WriteAllBytesAsync(Path.Combine(_options.CacheDirectoryPath, "cached_blob.bin"), [0xAA]);

        // Act
        await _wipeHandler.ExecuteWipeAsync();

        // Assert: Observable state changes
        File.Exists(_options.HeaderFilePath).Should().BeFalse();
        File.Exists(_options.DatabaseFilePath).Should().BeFalse();
        File.Exists(_options.DatabaseFilePath + "-wal").Should().BeFalse();
        File.Exists(_options.DatabaseFilePath + "-shm").Should().BeFalse();
        Directory.Exists(_options.CacheDirectoryPath).Should().BeFalse();

        // Subsequent header read returns Failure
        var readResult = await _storage.ReadHeaderAsync();
        readResult.IsFailure.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteWipeAsync_LockedDbFile_ContinuesWithoutThrowing()
    {
        // Arrange
        byte[] dummyHeader = new byte[140];
        Array.Fill(dummyHeader, (byte)0x42);
        await File.WriteAllBytesAsync(_options.HeaderFilePath, dummyHeader);

        await File.WriteAllBytesAsync(_options.DatabaseFilePath, [0x01, 0x02]);

        // Simulate external lock on database file
        await using (var lockStream = new FileStream(_options.DatabaseFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Act: wipe execution should catch IOException on locked db and continue destroying header
            Func<Task> act = async () => await _wipeHandler.ExecuteWipeAsync();

            await act.Should().NotThrowAsync();
        }

        // Header must still be completely destroyed
        File.Exists(_options.HeaderFilePath).Should().BeFalse();
        var readResult = await _storage.ReadHeaderAsync();
        readResult.IsFailure.Should().BeTrue();
    }
}
