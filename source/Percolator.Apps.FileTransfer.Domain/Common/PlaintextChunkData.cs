using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public sealed record PlaintextChunkData
{
    public ReadOnlyMemory<byte> Memory { get; }
    public ReadOnlySpan<byte> Span => Memory.Span;
    public int Length => Memory.Length;

    private PlaintextChunkData(ReadOnlyMemory<byte> memory)
    {
        Memory = memory;
    }

    public static PlaintextChunkData FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new DomainValidationException("Plaintext chunk data cannot be empty.");
        return new PlaintextChunkData(bytes);
    }

    public static PlaintextChunkData FromMemory(ReadOnlyMemory<byte> memory)
    {
        if (memory.Length == 0)
            throw new DomainValidationException("Plaintext chunk data cannot be empty.");
        return new PlaintextChunkData(memory);
    }
}
