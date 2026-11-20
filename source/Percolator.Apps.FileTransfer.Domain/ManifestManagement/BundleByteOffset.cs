using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public readonly record struct BundleByteOffset(long Offset)
{
    public static BundleByteOffset Zero => new(0);

    public static BundleByteOffset FromOffset(long offset)
    {
        if (offset < 0)
            throw new DomainValidationException("Bundle offset cannot be negative.");
        return new BundleByteOffset(offset);
    }

    public static BundleByteOffset FromChunk(ChunkIndex chunk, ChunkSizeBytes chunkSize) =>
        new((long)chunk.Value * chunkSize.Bytes);

    public BundleByteOffset Add(FileSizeBytes size) => new(Offset + size.Bytes);
    public BundleByteOffset Add(TransferByteLength length) => new(Offset + length.Bytes);
    public BundleByteOffset Subtract(BundleByteOffset other) => new(Offset - other.Offset);
    public FileByteOffset SubtractToFileOffset(BundleByteOffset other) =>
        FileByteOffset.FromOffset(Offset - other.Offset);
}
