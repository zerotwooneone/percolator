namespace Percolator.Infrastructure.Tests.Plugins.BlobStorage;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.PluginSdk;

[TestFixture]
public sealed class LocalFileChunkStoreTests
{
    private string _tempDirectory = null!;
    private LocalFileChunkStore _chunkStore = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PercolatorTests_Blobs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _chunkStore = new LocalFileChunkStore(_tempDirectory, maxBlobSizeBytes: 1024 * 1024, maxCacheQuotaBytes: 2 * 1024 * 1024);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task AtomicCommit_WritesTempFileAndRenames()
    {
        byte[] payload = new byte[10 * 1024];
        RandomNumberGenerator.Fill(payload);

        var blobId = BlobId.FromSha256(SHA256.HashData(payload));

        using (var ms = new MemoryStream(payload))
        {
            await _chunkStore.SaveBlobAsync(blobId, ms);
        }

        // Verify blob exists and no .tmp files linger
        bool exists = await _chunkStore.HasBlobAsync(blobId);
        exists.Should().BeTrue();

        string targetPath = _chunkStore.GetBlobPath(blobId);
        File.Exists(targetPath).Should().BeTrue();

        var tmpFiles = Directory.GetFiles(_tempDirectory, "*.tmp");
        tmpFiles.Should().BeEmpty();

        // Verify contents
        using var readStream = await _chunkStore.OpenReadAsync(blobId);
        using var readMs = new MemoryStream();
        await readStream.CopyToAsync(readMs);
        readMs.ToArray().Should().BeEquivalentTo(payload);
    }

    [Test]
    public async Task UploadExceedingQuota_ThrowsAndDeletesTempFile()
    {
        // 1.0 MB maxBlobSizeBytes is set in SetUp, attempt to save 1.5 MB
        byte[] oversized = new byte[1536 * 1024];
        var blobId = BlobId.FromSha256(SHA256.HashData(oversized));

        using var ms = new MemoryStream(oversized);
        Func<Task> act = async () => await _chunkStore.SaveBlobAsync(blobId, ms);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceed*");

        // Assert that temporary file was deleted
        var tmpFiles = Directory.GetFiles(_tempDirectory, "*.tmp");
        tmpFiles.Should().BeEmpty();

        bool exists = await _chunkStore.HasBlobAsync(blobId);
        exists.Should().BeFalse();
    }

    [Test]
    public async Task DeleteBlobAsync_RemovesFile()
    {
        byte[] payload = new byte[1024];
        var blobId = BlobId.FromSha256(SHA256.HashData(payload));

        using (var ms = new MemoryStream(payload))
        {
            await _chunkStore.SaveBlobAsync(blobId, ms);
        }

        (await _chunkStore.HasBlobAsync(blobId)).Should().BeTrue();

        await _chunkStore.DeleteBlobAsync(blobId);

        (await _chunkStore.HasBlobAsync(blobId)).Should().BeFalse();
    }
}
