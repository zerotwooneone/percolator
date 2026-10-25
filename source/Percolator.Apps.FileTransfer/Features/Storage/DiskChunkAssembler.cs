using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Features.Storage;

public sealed class DiskChunkAssembler
{
    private readonly IDesktopFileStorage _fileStorage;

    public DiskChunkAssembler(IDesktopFileStorage fileStorage)
    {
        _fileStorage = fileStorage;
    }

    public async ValueTask<DomainResult> AssembleChunkAsync(
        TransferSession session,
        RootDirectoryPath rootPath,
        ChunkIndex chunkIndex,
        PlaintextChunkData plaintextData,
        IDateTimeProvider clock,
        CancellationToken ct = default)
    {
        // Verify chunk cryptographic leaf hash and record in domain aggregate BEFORE touching disk
        DomainResult verifyResult = session.VerifyAndRecordChunk(chunkIndex, plaintextData, clock);
        if (verifyResult.IsFailure)
        {
            return verifyResult;
        }

        // Compute file slice boundaries across continuous bundle
        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(
            session.Manifest,
            chunkIndex,
            ChunkByteLength.FromBytes(plaintextData.Length));

        // Write each verified slice to target file on disk and finalize any file that just completed
        foreach (FileChunkSlice slice in slices)
        {
            if (session.SelectedFiles != null && !session.SelectedFiles.Contains(slice.FileIndex))
            {
                continue;
            }

            FileEntry entry = session.Manifest.Files[slice.FileIndex.Value];
            ResolvedFilePath targetFile = ResolvedFilePath.Resolve(rootPath, entry.RelativePath);

            ReadOnlyMemory<byte> sliceMemory = plaintextData.Memory.Slice(slice.BufferOffset, slice.SliceLength.Bytes);
            await _fileStorage.WriteChunkAsync(targetFile, slice.OffsetInFile, PlaintextChunkData.FromMemory(sliceMemory), ct);

            if (session.IsFileComplete(entry))
            {
                await _fileStorage.FinalizeFileAsync(targetFile, entry.LastModifiedUtc, ct);
            }
        }

        return verifyResult;
    }
}
