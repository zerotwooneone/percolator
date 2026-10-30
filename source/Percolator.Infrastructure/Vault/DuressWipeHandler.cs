namespace Percolator.Infrastructure.Vault;

using System.IO;
using System.Security.Cryptography;
using Percolator.Application2.Ports;

public sealed class DuressWipeHandler : IDuressWipeHandler
{
    private readonly VaultStorageOptions _options;

    public DuressWipeHandler(VaultStorageOptions? options = null)
    {
        _options = options ?? new VaultStorageOptions();
    }

    public async Task ExecuteWipeAsync(CancellationToken ct = default)
    {
        string headerPath = _options.HeaderFilePath;
        if (File.Exists(headerPath))
        {
            try
            {
                var fileInfo = new FileInfo(headerPath);
                long length = Math.Max(fileInfo.Length, 128);

                // 1. Overwrite header with CSPRNG random bytes and force physical disk controller flush
                await using (var fs = new FileStream(headerPath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    byte[] buffer = new byte[length];
                    RandomNumberGenerator.Fill(buffer);
                    await fs.WriteAsync(buffer, ct).ConfigureAwait(false);
                    fs.Flush(flushToDisk: true);

                    // 2. Truncate to zero bytes
                    fs.SetLength(0);
                    fs.Flush(flushToDisk: true);
                }

                File.Delete(headerPath);
            }
            catch (IOException)
            {
                // Proceed even if locked
            }
        }

        string tempHeaderPath = headerPath + ".tmp";
        if (File.Exists(tempHeaderPath))
        {
            try { File.Delete(tempHeaderPath); } catch (IOException) { }
        }

        // 3. Immediately delete database and WAL files (all data is already unrecoverable ciphertext)
        string dbPath = _options.DatabaseFilePath;
        var dbFiles = new[]
        {
            dbPath,
            dbPath + "-wal",
            dbPath + "-shm"
        };

        foreach (var path in dbFiles)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Even if file deletion is blocked by OS lock, Kdb is already destroyed permanently
            }
        }

        // 4. Purge local cache and staging directories
        string cacheDir = _options.CacheDirectoryPath;
        if (Directory.Exists(cacheDir))
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
