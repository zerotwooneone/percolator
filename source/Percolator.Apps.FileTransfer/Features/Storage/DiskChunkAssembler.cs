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
        object? sessionSync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);

        if (session.State != TransferSessionState.InFlight)
        {
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot record chunks in state '{session.State}'."));
        }

        if (chunkIndex.Value < 0 || chunkIndex.Value >= session.Manifest.TotalChunks.Count)
        {
            return DomainResult.Failure(new DomainError("Transfer.InvalidChunkIndex", $"Chunk index {chunkIndex.Value} is out of bounds."));
        }

        ChunkByteLength expectedChunkSize = session.Manifest.GetExpectedChunkSize(chunkIndex);
        if (plaintextData.Length != expectedChunkSize.Bytes)
        {
            return DomainResult.Failure(new DomainError("Transfer.MismatchedChunkSize", $"Chunk byte length {plaintextData.Length} does not match expected {expectedChunkSize.Bytes}."));
        }

        object sync = sessionSync ?? new object();
        lock (sync)
        {
            if (session.LocalBitfield.HasChunk(chunkIndex))
            {
                return DomainResult.Success();
            }
        }

        // 1. Off-lock cryptographic leaf verification before touching disk
        if (!session.Manifest.MerkleTree.VerifyLeaf(chunkIndex, plaintextData.Span))
        {
            return DomainResult.Failure(new DomainError("Transfer.CryptographicVerificationFailed", $"Leaf hash verification failed for chunk {chunkIndex.Value}."));
        }

        // 2. Compute file slice boundaries across continuous bundle ONCE
        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(
            session.Manifest,
            chunkIndex,
            expectedChunkSize);

        // 3. Write each verified slice to disk outside session lock
        foreach (FileChunkSlice slice in slices)
        {
            ReadOnlyMemory<byte> sliceMemory = plaintextData.Memory.Slice(slice.BufferOffset, slice.SliceLength.Bytes);
            PlaintextChunkData sliceData = PlaintextChunkData.FromMemory(sliceMemory);

            if (session.SelectedFiles != null && !session.SelectedFiles.Contains(slice.FileIndex))
            {
                await _fileStorage.WriteBoundarySliceAsync(session.Id, chunkIndex, slice.FileIndex, sliceData, ct).ConfigureAwait(false);
                continue;
            }

            FileEntry entry = session.Manifest.Files[slice.FileIndex.Value];
            ResolvedFilePath targetFile = ResolvedFilePath.Resolve(rootPath, entry.RelativePath);

            await _fileStorage.WriteChunkAsync(targetFile, slice.OffsetInFile, sliceData, ct).ConfigureAwait(false);
        }

        // 4. Fast memory commit under sessionSync lock: flips LocalBitfield, re-evaluates interest, collects completed files
        DomainResult<IReadOnlyList<FileEntry>> commitResult;
        lock (sync)
        {
            commitResult = session.RecordPreVerifiedChunk(chunkIndex, slices, clock);
        }

        if (commitResult.IsFailure)
        {
            return DomainResult.Failure(commitResult.Error);
        }

        // 5. Finalize completed files outside session lock
        foreach (FileEntry completedFile in commitResult.Value)
        {
            ResolvedFilePath targetFile = ResolvedFilePath.Resolve(rootPath, completedFile.RelativePath);
            await _fileStorage.FinalizeFileAsync(targetFile, completedFile.LastModifiedUtc, ct).ConfigureAwait(false);
        }

        return DomainResult.Success();
    }
}
