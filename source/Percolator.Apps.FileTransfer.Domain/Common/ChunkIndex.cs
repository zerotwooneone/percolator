using System.Buffers.Binary;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct ChunkIndex(int Value)
{
    public static ChunkIndex FromInt(int value)
    {
        if (value < 0)
            throw new DomainValidationException("Chunk index cannot be negative.");
        return new ChunkIndex(value);
    }

    public void WriteBigEndian(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, Value);
    }

    public ChunkIndex Next() => new(Value + 1);
}
