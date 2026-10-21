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
        // Compute file slice boundaries across continuous bundle
        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(
            session.Manifest,
            chunkIndex,
            ChunkByteLength.FromBytes(plaintextData.Length));

        // Write each slice to target file on disk
        foreach (FileChunkSlice slice in slices)
        {
            FileEntry entry = session.Manifest.Files[slice.FileIndex.Value];
            ResolvedFilePath targetFile = ResolvedFilePath.Resolve(rootPath, entry.RelativePath);

            ReadOnlyMemory<byte> sliceMemory = plaintextData.Memory.Slice(slice.BufferOffset, slice.SliceLength.Bytes);
            await _fileStorage.WriteChunkAsync(targetFile, slice.OffsetInFile, PlaintextChunkData.FromMemory(sliceMemory), ct);
        }

        // Record chunk verification in domain aggregate (detects file and session completion)
        return session.VerifyAndRecordChunk(chunkIndex, plaintextData, clock);
    }
}
