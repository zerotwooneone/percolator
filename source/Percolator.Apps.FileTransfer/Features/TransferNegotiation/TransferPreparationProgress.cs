namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public readonly record struct TransferPreparationProgress(
    int FilesScanned,
    int TotalFiles,
    int ChunksHashed,
    int TotalChunks);
