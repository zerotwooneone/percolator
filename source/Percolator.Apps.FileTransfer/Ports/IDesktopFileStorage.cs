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
    ValueTask<Bitfield> ScanAndComputeBitfieldAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices? selectedIndices = null, CancellationToken ct = default);
    ValueTask FinalizeFileAsync(ResolvedFilePath targetPath, FileLastModifiedUtc lastModifiedUtc, CancellationToken ct = default);
    ValueTask<DiskSpacePreflightResult> CheckDiskSpaceAsync(RootDirectoryPath rootPath, TotalBundleBytes requiredBytes, CancellationToken ct = default);

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
