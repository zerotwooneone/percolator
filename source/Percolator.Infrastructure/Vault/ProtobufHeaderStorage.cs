namespace Percolator.Infrastructure.Vault;

using System.IO;
using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Domain.Common;

internal sealed class ProtobufHeaderStorage : IHeaderStorage
{
    private readonly string _headerPath;

    public ProtobufHeaderStorage(VaultStorageOptions options)
        : this(options.HeaderFilePath)
    {
    }

    public ProtobufHeaderStorage(string headerPath)
    {
        _headerPath = headerPath ?? throw new ArgumentNullException(nameof(headerPath));
    }

    public async Task<DomainResult<DatabaseHeader>> ReadHeaderAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_headerPath))
            {
                return DomainResult<DatabaseHeader>.Failure(new DomainError("HEADER_NOT_FOUND", "Database header file was not found."));
            }

            var fileInfo = new FileInfo(_headerPath);
            if (fileInfo.Length == 0)
            {
                return DomainResult<DatabaseHeader>.Failure(new DomainError("HEADER_EMPTY", "Database header file is empty."));
            }

            byte[] bytes = await File.ReadAllBytesAsync(_headerPath, ct).ConfigureAwait(false);
            var header = DatabaseHeader.Parser.ParseFrom(bytes);
            return DomainResult<DatabaseHeader>.Success(header);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return DomainResult<DatabaseHeader>.Failure(new DomainError("HEADER_CORRUPTED", $"Database header protobuf parsing failed: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return DomainResult<DatabaseHeader>.Failure(new DomainError("HEADER_READ_FAILED", ex.Message));
        }
    }

    public async Task<DomainResult> WriteHeaderAsync(DatabaseHeader header, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);

        try
        {
            string directory = Path.GetDirectoryName(_headerPath)!;
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = _headerPath + ".tmp";

            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                header.WriteTo(fs);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _headerPath, overwrite: true);
            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("HEADER_WRITE_FAILED", ex.Message));
        }
    }

    public async Task<DomainResult> OverwriteAndWipeAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_headerPath))
            {
                var fileInfo = new FileInfo(_headerPath);
                long length = Math.Max(fileInfo.Length, 128);

                await using (var fs = new FileStream(_headerPath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    byte[] buffer = new byte[length];
                    RandomNumberGenerator.Fill(buffer);
                    await fs.WriteAsync(buffer, ct).ConfigureAwait(false);
                    fs.Flush(flushToDisk: true);

                    fs.SetLength(0);
                    fs.Flush(flushToDisk: true);
                }

                File.Delete(_headerPath);
            }

            string tempPath = _headerPath + ".tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("HEADER_WIPE_FAILED", ex.Message));
        }
    }
}
