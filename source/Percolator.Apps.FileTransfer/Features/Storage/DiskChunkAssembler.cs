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
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
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

        // Write each verified slice to target file on disk (or boundary part storage in the file storage adapter for unselected files)
        foreach (FileChunkSlice slice in slices)
        {
            ReadOnlyMemory<byte> sliceMemory = plaintextData.Memory.Slice(slice.BufferOffset, slice.SliceLength.Bytes);
            PlaintextChunkData sliceData = PlaintextChunkData.FromMemory(sliceMemory);

            if (session.SelectedFiles != null && !session.SelectedFiles.Contains(slice.FileIndex))
            {
                // Boundary slice for unselected file: delegate caching to storage adapter to enable future chunk seeding without polluting user folders
                await _fileStorage.WriteBoundarySliceAsync(session.Id, chunkIndex, slice.FileIndex, sliceData, ct);
                continue;
            }

            FileEntry entry = session.Manifest.Files[slice.FileIndex.Value];
            ResolvedFilePath targetFile = ResolvedFilePath.Resolve(rootPath, entry.RelativePath);

            await _fileStorage.WriteChunkAsync(targetFile, slice.OffsetInFile, sliceData, ct);

            if (session.IsFileComplete(entry))
            {
                await _fileStorage.FinalizeFileAsync(targetFile, entry.LastModifiedUtc, ct);
            }
        }

        return verifyResult;
    }
}
