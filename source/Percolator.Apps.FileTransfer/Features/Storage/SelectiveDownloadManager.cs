using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

namespace Percolator.Apps.FileTransfer.Features.Storage;

public static class SelectiveDownloadManager
{
    public static TotalBundleBytes CalculateRequiredDiskSpace(TransferManifest manifest, SelectedFileIndices selectedFiles)
    {
        long required = 0;
        foreach (FileIndex idx in selectedFiles.Indices)
        {
            required += manifest.Files[idx.Value].FileSize.Bytes;
        }
        return TotalBundleBytes.FromBytes(required);
    }

    public static async ValueTask FinalizeZeroByteFilesAsync(
        TransferManifest manifest,
        SelectedFileIndices selectedFiles,
        RootDirectoryPath rootPath,
        IDesktopFileStorage fileStorage,
        CancellationToken ct = default)
    {
        foreach (FileIndex idx in selectedFiles.Indices)
        {
            FileEntry file = manifest.Files[idx.Value];
            if (file.FileSize.Bytes == 0)
            {
                ResolvedFilePath path = ResolvedFilePath.Resolve(rootPath, file.RelativePath);
                await fileStorage.FinalizeFileAsync(path, file.LastModifiedUtc, ct);
            }
        }
    }
}
