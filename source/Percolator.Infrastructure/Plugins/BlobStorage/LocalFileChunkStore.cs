namespace Percolator.Infrastructure.Plugins.BlobStorage;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using Percolator.PluginSdk;

public sealed class LocalFileChunkStore : ILocalChunkStore
{
    private readonly string _storageDirectory;
    private readonly long _maxBlobSizeBytes;
    private readonly long _maxCacheQuotaBytes;
    private long _currentCacheSizeBytes;

    public LocalFileChunkStore(
        string? storageDirectory = null,
        long maxBlobSizeBytes = 100L * 1024 * 1024, // 100 MB ceiling
        long maxCacheQuotaBytes = 5L * 1024 * 1024 * 1024) // 5 GB quota ceiling
    {
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Percolator",
            "Blobs");

        _maxBlobSizeBytes = maxBlobSizeBytes;
        _maxCacheQuotaBytes = maxCacheQuotaBytes;

        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }
        else
        {
            long total = 0;
            var dirInfo = new DirectoryInfo(_storageDirectory);
            foreach (var file in dirInfo.EnumerateFiles("*.blob"))
            {
                total += file.Length;
            }
            _currentCacheSizeBytes = total;
        }
    }

    public long CurrentCacheSizeBytes => Interlocked.Read(ref _currentCacheSizeBytes);

    public ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default)
    {
        string filePath = GetBlobPath(blobId);
        return ValueTask.FromResult(File.Exists(filePath));
    }

    public ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default)
    {
        string filePath = GetBlobPath(blobId);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Blob {blobId} not found in local chunk store.", filePath);
        }

        // Return a FileStream with Read sharing
        Stream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        return ValueTask.FromResult(stream);
    }

    public async ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contentStream);

        string targetFilePath = GetBlobPath(blobId);
        string tempFilePath = Path.Combine(_storageDirectory, $"{GetBlobHex(blobId)}_{Guid.NewGuid():N}.tmp");

        // Pre-flight quota check if stream provides length
        if (contentStream.CanSeek && contentStream.Length > _maxBlobSizeBytes)
        {
            throw new InvalidOperationException($"Blob exceeds maximum allowed size of {_maxBlobSizeBytes} bytes.");
        }

        long currentTotalCache = GetCurrentCacheSizeBytes();
        if (contentStream.CanSeek && (currentTotalCache + contentStream.Length > _maxCacheQuotaBytes))
        {
            throw new InvalidOperationException($"Local cache quota of {_maxCacheQuotaBytes} bytes exceeded.");
        }

        long bytesWritten = 0;
        byte[] buffer = new byte[64 * 1024];

        try
        {
            using (var fileStream = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                int read;
                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    bytesWritten += read;
                    if (bytesWritten > _maxBlobSizeBytes)
                    {
                        throw new InvalidOperationException($"Blob stream exceeded {_maxBlobSizeBytes} bytes during write.");
                    }

                    if (currentTotalCache + bytesWritten > _maxCacheQuotaBytes)
                    {
                        throw new InvalidOperationException($"Writing blob exceeded cache quota of {_maxCacheQuotaBytes} bytes.");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                await fileStream.FlushAsync(ct).ConfigureAwait(false);
            }

            long existingFileLength = File.Exists(targetFilePath) ? new FileInfo(targetFilePath).Length : 0;

            // Atomic move to destination
            File.Move(tempFilePath, targetFilePath, overwrite: true);
            Interlocked.Add(ref _currentCacheSizeBytes, bytesWritten - existingFileLength);
        }
        catch
        {
            // Clean up temporary file on any error
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* best effort */ }
            }
            throw;
        }
    }

    public ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default)
    {
        string filePath = GetBlobPath(blobId);
        if (File.Exists(filePath))
        {
            long len = new FileInfo(filePath).Length;
            File.Delete(filePath);
            Interlocked.Add(ref _currentCacheSizeBytes, -len);
        }
        return ValueTask.CompletedTask;
    }

    public string GetBlobPath(BlobId blobId)
    {
        return Path.Combine(_storageDirectory, $"{GetBlobHex(blobId)}.blob");
    }

    private static string GetBlobHex(BlobId blobId)
    {
        return string.IsNullOrEmpty(blobId.HexDigest) ? string.Empty : blobId.HexDigest.ToLowerInvariant();
    }

    private long GetCurrentCacheSizeBytes()
    {
        return Interlocked.Read(ref _currentCacheSizeBytes);
    }
}
