using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

public interface IDesktopFileStorage
{
    ValueTask PreAllocateFilesAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices selectedIndices, CancellationToken ct = default);
    ValueTask WriteChunkAsync(ResolvedFilePath targetPath, FileByteOffset fileOffset, PlaintextChunkData data, CancellationToken ct = default);
    ValueTask<PlaintextChunkData> ReadChunkAsync(ResolvedFilePath sourcePath, FileByteOffset fileOffset, TransferByteLength length, CancellationToken ct = default);

    ValueTask<int> ReadChunkIntoAsync(ResolvedFilePath sourcePath, FileByteOffset fileOffset, Memory<byte> destination, CancellationToken ct = default)
    {
        return ReadChunkIntoFallbackAsync(sourcePath, fileOffset, destination, ct);

        async ValueTask<int> ReadChunkIntoFallbackAsync(ResolvedFilePath path, FileByteOffset offset, Memory<byte> dest, CancellationToken token)
        {
            PlaintextChunkData data = await ReadChunkAsync(path, offset, TransferByteLength.FromBytes(dest.Length), token).ConfigureAwait(false);
            data.Span.CopyTo(dest.Span);
            return data.Length;
        }
    }
    ValueTask WriteBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, PlaintextChunkData data, CancellationToken ct = default);
    ValueTask<PlaintextChunkData?> ReadBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, TransferByteLength length, CancellationToken ct = default);
    ValueTask DeleteBoundaryPartsAsync(TransferSessionId sessionId, CancellationToken ct = default) => ValueTask.CompletedTask;
    ValueTask<Bitfield> ScanAndComputeBitfieldAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices? selectedIndices = null, CancellationToken ct = default);
    ValueTask FinalizeFileAsync(ResolvedFilePath targetPath, FileLastModifiedUtc lastModifiedUtc, CancellationToken ct = default);
    ValueTask<DiskSpacePreflightResult> CheckDiskSpaceAsync(RootDirectoryPath rootPath, TotalBundleBytes requiredBytes, CancellationToken ct = default);
    ValueTask<bool> HasAnyExistingTransferFilesAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices selectedIndices, CancellationToken ct = default) => ValueTask.FromResult(true);

    ValueTask<FileMetadataResult> GetFileMetadataAsync(ResolvedFilePath sourcePath, CancellationToken ct = default)
    {
        FileInfo info = new(sourcePath.AbsolutePath);
        if (!info.Exists)
            return ValueTask.FromResult(FileMetadataResult.NotFound());

        return ValueTask.FromResult(FileMetadataResult.Found(
            FileSizeBytes.FromBytes(info.Length),
            FileLastModifiedUtc.FromDateTimeOffset(info.LastWriteTimeUtc)));
    }
}
