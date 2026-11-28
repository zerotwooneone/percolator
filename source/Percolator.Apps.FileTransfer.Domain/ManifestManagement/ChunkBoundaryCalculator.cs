using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public static class ChunkBoundaryCalculator
{
    public static IReadOnlyList<FileChunkSlice> CalculateSlices(
        TransferManifest manifest,
        ChunkIndex chunkIndex,
        ChunkByteLength actualChunkSize)
    {
        return CalculateSlicesFromEntries(manifest.Files, manifest.ChunkSize, chunkIndex, actualChunkSize);
    }

    public static IReadOnlyList<FileChunkSlice> CalculateSlicesFromEntries(
        IReadOnlyList<FileEntry> files,
        ChunkSizeBytes chunkSize,
        ChunkIndex chunkIndex,
        ChunkByteLength actualChunkSize)
    {
        List<FileChunkSlice> slices = [];
        BundleByteOffset chunkStart = BundleByteOffset.FromChunk(chunkIndex, chunkSize);
        ChunkByteLength remainingChunkBytes = actualChunkSize;
        BundleByteOffset currentOffset = chunkStart;
        int bufferOffset = 0;

        int fileIdx = FindFirstFileIndexForOffset(files, chunkStart);

        while (remainingChunkBytes.Bytes > 0 && fileIdx < files.Count)
        {
            FileEntry file = files[fileIdx];
            if (file.FileSize.Bytes == 0)
            {
                fileIdx++;
                continue;
            }

            FileByteOffset offsetInFile = currentOffset.SubtractToFileOffset(file.StartOffset);
            TransferByteLength bytesForThisFile = file.FileSize.RemainingSlice(offsetInFile, remainingChunkBytes);

            slices.Add(new FileChunkSlice(
                file.Index,
                file.RelativePath,
                offsetInFile,
                bytesForThisFile,
                bufferOffset));

            bufferOffset += bytesForThisFile.Bytes;
            currentOffset = currentOffset.Add(bytesForThisFile);
            remainingChunkBytes = remainingChunkBytes.Subtract(bytesForThisFile);

            if (currentOffset.Offset >= file.EndOffset.Offset)
            {
                fileIdx++;
            }
        }

        return slices;
    }

    private static int FindFirstFileIndexForOffset(IReadOnlyList<FileEntry> files, BundleByteOffset offset)
    {
        int low = 0;
        int high = files.Count - 1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            FileEntry candidate = files[mid];

            if (candidate.FileSize.Bytes == 0)
            {
                if (offset.Offset < candidate.StartOffset.Offset)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
                continue;
            }

            if (offset.Offset < candidate.StartOffset.Offset)
            {
                high = mid - 1;
            }
            else if (offset.Offset >= candidate.EndOffset.Offset)
            {
                low = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        throw new DomainValidationException($"Offset {offset.Offset} falls outside bundle layout.");
    }
}
