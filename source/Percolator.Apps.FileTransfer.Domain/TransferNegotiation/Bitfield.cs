using System.Numerics;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public sealed record Bitfield
{
    // Big-endian bit ordering (BitTorrent Standard):
    // Bit 0 is the most significant bit (0x80) of Byte 0.
    // Bit 7 is the least significant bit (0x01) of Byte 0.
    // Bit 8 is the most significant bit (0x80) of Byte 1.
    public ChunkCount TotalChunks { get; }
    public int ByteLength => (TotalChunks.Count + 7) / 8;
    public int PopCount { get; }
    public bool IsEmpty => PopCount == 0;
    public bool IsComplete => PopCount == TotalChunks.Count;

    private readonly byte[] _bytes;

    private Bitfield(byte[] bytes, ChunkCount totalChunks, int popCount)
    {
        _bytes = bytes;
        TotalChunks = totalChunks;
        PopCount = popCount;
    }

    public static Bitfield Empty(ChunkCount totalChunks)
    {
        int len = (totalChunks.Count + 7) / 8;
        return new Bitfield(new byte[len], totalChunks, 0);
    }

    public static Bitfield Full(ChunkCount totalChunks)
    {
        int len = (totalChunks.Count + 7) / 8;
        byte[] bytes = new byte[len];
        for (int i = 0; i < len; i++) bytes[i] = 0xFF;

        // Invariant: Trailing unused bits in the final byte must be strictly zeroed:
        int remainder = totalChunks.Count % 8;
        if (remainder != 0)
        {
            byte mask = (byte)(0xFF << (8 - remainder));
            bytes[len - 1] &= mask;
        }

        return new Bitfield(bytes, totalChunks, totalChunks.Count);
    }

    public static Bitfield FromBytes(ReadOnlySpan<byte> source, ChunkCount totalChunks)
    {
        int expectedLen = (totalChunks.Count + 7) / 8;
        if (source.Length != expectedLen)
            throw new DomainValidationException($"Bitfield byte length ({source.Length}) does not match expected ({expectedLen}) for {totalChunks.Count} chunks.");

        byte[] copy = source.ToArray();

        // Strict Invariant: Trailing unused bits must be 0:
        int remainder = totalChunks.Count % 8;
        if (remainder != 0)
        {
            byte trailingMask = (byte)(0xFF >> remainder);
            if ((copy[expectedLen - 1] & trailingMask) != 0)
                throw new DomainValidationException("Bitfield contains non-zero bits in trailing padding.");
        }

        int count = CountBits(copy);
        return new Bitfield(copy, totalChunks, count);
    }

    public bool HasChunk(ChunkIndex index)
    {
        if (index.Value < 0 || index.Value >= TotalChunks.Count) return false;
        int byteIdx = index.Value / 8;
        int bitIdx = 7 - (index.Value % 8);
        return (_bytes[byteIdx] & (1 << bitIdx)) != 0;
    }

    public Bitfield WithChunk(ChunkIndex index)
    {
        if (index.Value < 0 || index.Value >= TotalChunks.Count)
            throw new DomainValidationException($"Chunk index {index.Value} is out of bounds for {TotalChunks.Count} total chunks.");

        if (HasChunk(index)) return this;
        byte[] next = (byte[])_bytes.Clone();
        int byteIdx = index.Value / 8;
        int bitIdx = 7 - (index.Value % 8);
        next[byteIdx] |= (byte)(1 << bitIdx);
        return new Bitfield(next, TotalChunks, PopCount + 1);
    }

    public Bitfield WithoutChunk(ChunkIndex index)
    {
        if (index.Value < 0 || index.Value >= TotalChunks.Count)
            throw new DomainValidationException($"Chunk index {index.Value} is out of bounds for {TotalChunks.Count} total chunks.");

        if (!HasChunk(index)) return this;
        byte[] next = (byte[])_bytes.Clone();
        int byteIdx = index.Value / 8;
        int bitIdx = 7 - (index.Value % 8);
        next[byteIdx] &= (byte)~(1 << bitIdx);
        return new Bitfield(next, TotalChunks, PopCount - 1);
    }

    public bool HasAllChunks(Bitfield mask)
    {
        if (mask.TotalChunks != TotalChunks) return false;
        for (int i = 0; i < _bytes.Length; i++)
        {
            if ((_bytes[i] & mask._bytes[i]) != mask._bytes[i]) return false;
        }
        return true;
    }

    public Bitfield Intersect(Bitfield other)
    {
        if (other.TotalChunks != TotalChunks)
            throw new DomainValidationException("Cannot intersect bitfields with differing total chunk counts.");
        byte[] result = new byte[_bytes.Length];
        for (int i = 0; i < _bytes.Length; i++) result[i] = (byte)(_bytes[i] & other._bytes[i]);
        return new Bitfield(result, TotalChunks, CountBits(result));
    }

    public Bitfield Difference(Bitfield other)
    {
        if (other.TotalChunks != TotalChunks)
            throw new DomainValidationException("Cannot compute difference of bitfields with differing total chunk counts.");
        byte[] result = new byte[_bytes.Length];
        for (int i = 0; i < _bytes.Length; i++) result[i] = (byte)(_bytes[i] & ~other._bytes[i]);
        return new Bitfield(result, TotalChunks, CountBits(result));
    }

    public byte[] ToByteArray() => (byte[])_bytes.Clone();

    public bool Equals(Bitfield? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return TotalChunks == other.TotalChunks
               && PopCount == other.PopCount
               && _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(TotalChunks);
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    private static int CountBits(byte[] bytes)
    {
        int count = 0;
        foreach (byte b in bytes) count += BitOperations.PopCount(b);
        return count;
    }
}
