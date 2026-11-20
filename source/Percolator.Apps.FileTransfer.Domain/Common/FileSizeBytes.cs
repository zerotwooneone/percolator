using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct FileSizeBytes(long Bytes)
{
    public static FileSizeBytes Zero => new(0);

    public static FileSizeBytes FromBytes(long bytes)
    {
        if (bytes < 0)
            throw new DomainValidationException("File size cannot be negative.");
        return new FileSizeBytes(bytes);
    }

    public TransferByteLength RemainingSlice(FileByteOffset offsetInFile, ChunkByteLength remainingChunkBytes)
    {
        long remainingInFile = Bytes - offsetInFile.Offset;
        int sliceLength = (int)Math.Min(remainingInFile, (long)remainingChunkBytes.Bytes);
        return TransferByteLength.FromBytes(sliceLength);
    }
}
