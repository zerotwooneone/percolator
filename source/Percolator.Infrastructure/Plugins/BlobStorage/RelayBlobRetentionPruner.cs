namespace Percolator.Infrastructure.Plugins.BlobStorage;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

public sealed class RelayBlobRetentionPruner : BackgroundService
{
    private readonly string _storageDirectory;
    private readonly long _maxCacheQuotaBytes;
    private readonly TimeSpan _blobTtl;
    private readonly TimeSpan _checkInterval;

    public RelayBlobRetentionPruner(
        string? storageDirectory = null,
        long maxCacheQuotaBytes = 5L * 1024 * 1024 * 1024, // 5 GB
        TimeSpan? blobTtl = null,
        TimeSpan? checkInterval = null)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Percolator",
            "Blobs");

        _maxCacheQuotaBytes = maxCacheQuotaBytes;
        _blobTtl = blobTtl ?? TimeSpan.FromDays(30);
        _checkInterval = checkInterval ?? TimeSpan.FromHours(1);
    }

    public void PruneOnce()
    {
        if (!Directory.Exists(_storageDirectory))
            return;

        var dirInfo = new DirectoryInfo(_storageDirectory);
        var files = dirInfo.GetFiles("*.blob");

        var now = DateTime.UtcNow;

        // 1. Prune expired TTL blobs
        foreach (var file in files)
        {
            if (now - file.LastAccessTimeUtc > _blobTtl || now - file.LastWriteTimeUtc > _blobTtl)
            {
                try { file.Delete(); } catch { /* best effort */ }
            }
        }

        // 2. Prune LRU if still exceeding quota watermark
        files = dirInfo.GetFiles("*.blob");
        long totalSize = files.Sum(f => f.Length);

        if (totalSize > _maxCacheQuotaBytes)
        {
            var orderedByLru = files.OrderBy(f => f.LastAccessTimeUtc).ToList();
            foreach (var file in orderedByLru)
            {
                try
                {
                    totalSize -= file.Length;
                    file.Delete();
                }
                catch { /* best effort */ }

                if (totalSize <= _maxCacheQuotaBytes)
                    break;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PruneOnce();
            }
            catch
            {
                // Background worker resilience
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
