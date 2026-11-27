namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;

public readonly struct PooledEncodedFrame : IDisposable
{
    private readonly byte[] _rentedBuffer;

    public int Length { get; }
    public ReadOnlySpan<byte> Span => _rentedBuffer.AsSpan(0, Length);
    public ReadOnlyMemory<byte> Memory => _rentedBuffer.AsMemory(0, Length);

    internal PooledEncodedFrame(byte[] rentedBuffer, int length)
    {
        _rentedBuffer = rentedBuffer;
        Length = length;
    }

    public void Dispose()
    {
        if (_rentedBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer, clearArray: false);
        }
    }
}

public static class SwarmFrameCodec
{
    public const int FrameHeaderSizeBytes = 5;
    public const int MaxPayloadSizeBytes = ChunkSizeBytes.MaxChunkSize + 64; // 4 MB max chunk + ChunkIndex/frame metadata headroom
    public const int MaxWireFrameSizeBytes = FrameHeaderSizeBytes + MaxPayloadSizeBytes;
    public const int HandshakePayloadSizeBytes = 68;

    public static PooledEncodedFrame EncodePooled(IStreamFramePayload frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        byte opcode = (byte)frame.FrameType;
        int payloadLen = ComputePayloadLength(frame);
        if (payloadLen > MaxPayloadSizeBytes)
        {
            throw new InvalidOperationException($"Encoded frame payload {payloadLen} exceeds {MaxPayloadSizeBytes}-byte limit.");
        }

        int totalLen = FrameHeaderSizeBytes + payloadLen;
        byte[] rented = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            rented[0] = opcode;
            BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(1, 4), (uint)payloadLen);
            WritePayload(frame, rented.AsSpan(FrameHeaderSizeBytes, payloadLen));
            return new PooledEncodedFrame(rented, totalLen);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            throw;
        }
    }

    public static IStreamFramePayload DecodeFramed(ReadOnlySpan<byte> wireFrame)
    {
        if (wireFrame.Length < FrameHeaderSizeBytes || wireFrame.Length > MaxWireFrameSizeBytes)
        {
            throw new InvalidDataException($"Framed payload size {wireFrame.Length} is outside valid bounds [{FrameHeaderSizeBytes}..{MaxWireFrameSizeBytes}].");
        }

        byte opcode = wireFrame[0];
        uint declaredLen = BinaryPrimitives.ReadUInt32BigEndian(wireFrame.Slice(1, 4));
        if (declaredLen > MaxPayloadSizeBytes)
        {
            throw new InvalidDataException($"Frame payload length {declaredLen} exceeds maximum allowed limit ({MaxPayloadSizeBytes} bytes).");
        }

        if (wireFrame.Length != FrameHeaderSizeBytes + (int)declaredLen)
        {
            throw new InvalidDataException($"Frame length mismatch: declared {declaredLen} bytes, received {wireFrame.Length - FrameHeaderSizeBytes}.");
        }

        return Decode(opcode, wireFrame.Slice(FrameHeaderSizeBytes, (int)declaredLen));
    }

    public static async ValueTask<IStreamFramePayload> ReadFramePooledAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = ArrayPool<byte>.Shared.Rent(FrameHeaderSizeBytes);
        byte opcode;
        int payloadLen;
        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(0, FrameHeaderSizeBytes), ct).ConfigureAwait(false);
            opcode = header[0];
            uint rawLen = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
            if (rawLen > MaxPayloadSizeBytes)
            {
                throw new InvalidDataException($"Frame payload length {rawLen} exceeds maximum allowed limit ({MaxPayloadSizeBytes} bytes).");
            }
            payloadLen = (int)rawLen;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header, clearArray: false);
        }

        if (payloadLen == 0)
        {
            return Decode(opcode, ReadOnlySpan<byte>.Empty);
        }

        byte[] payloadBuf = ArrayPool<byte>.Shared.Rent(payloadLen);
        try
        {
            await stream.ReadExactlyAsync(payloadBuf.AsMemory(0, payloadLen), ct).ConfigureAwait(false);
            return Decode(opcode, payloadBuf.AsSpan(0, payloadLen));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payloadBuf, clearArray: false);
        }
    }

    private static int ComputePayloadLength(IStreamFramePayload frame) => frame switch
    {
        HandshakeFrame => HandshakePayloadSizeBytes,
        BitfieldFrame bitfield => 4 + ((bitfield.Bitfield.TotalChunks.Count + 7) / 8),
        HaveFrame => 4,
        RequestChunkFrame => 4,
        ChunkDataFrame chunkData => 4 + chunkData.Data.Length,
        CancelChunkFrame => 4,
        ChokeFrame => 0,
        UnchokeFrame => 0,
        InterestedFrame => 0,
        NotInterestedFrame => 0,
        KeepAliveFrame => 0,
        PexPeersFrame pex => ComputePexPeersLength(pex),
        _ => throw new NotSupportedException($"Unsupported frame type: {frame.GetType().Name}")
    };

    private static int ComputePexPeersLength(PexPeersFrame frame)
    {
        int total = 2;
        foreach (DirectEndpointCandidate c in frame.Candidates)
        {
            total += 2 + Encoding.UTF8.GetByteCount(c.Host.Value) + 2 + 1;
        }
        return total;
    }

    private static void WritePayload(IStreamFramePayload frame, Span<byte> destination)
    {
        switch (frame)
        {
            case HandshakeFrame handshake:
                handshake.AuthToken.Span.CopyTo(destination.Slice(0, 32));
                handshake.SessionId.TryWriteBytes(destination.Slice(32, 16));
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(48, 4), handshake.StreamIndex.Value);
                handshake.SenderPeerId.TryWriteBytes(destination.Slice(52, 16));
                break;
            case BitfieldFrame bitfield:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(0, 4), bitfield.Bitfield.TotalChunks.Count);
                bitfield.Bitfield.ToByteArray().CopyTo(destination.Slice(4));
                break;
            case HaveFrame have:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(0, 4), have.ChunkIndex.Value);
                break;
            case RequestChunkFrame req:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(0, 4), req.ChunkIndex.Value);
                break;
            case ChunkDataFrame chunkData:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(0, 4), chunkData.ChunkIndex.Value);
                chunkData.Data.Span.CopyTo(destination.Slice(4));
                break;
            case CancelChunkFrame cancel:
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(0, 4), cancel.ChunkIndex.Value);
                break;
            case ChokeFrame:
            case UnchokeFrame:
            case InterestedFrame:
            case NotInterestedFrame:
            case KeepAliveFrame:
                break;
            case PexPeersFrame pex:
                EncodePexPeers(pex).CopyTo(destination);
                break;
            default:
                throw new NotSupportedException($"Unsupported frame type: {frame.GetType().Name}");
        }
    }

    public static (byte Opcode, byte[] Payload) Encode(IStreamFramePayload frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        byte opcode = (byte)frame.FrameType;
        byte[] payload = frame switch
        {
            HandshakeFrame handshake => EncodeHandshake(handshake),
            BitfieldFrame bitfield => EncodeBitfield(bitfield),
            HaveFrame have => EncodeChunkIndex(have.ChunkIndex),
            RequestChunkFrame request => EncodeChunkIndex(request.ChunkIndex),
            ChunkDataFrame chunkData => EncodeChunkData(chunkData),
            CancelChunkFrame cancel => EncodeChunkIndex(cancel.ChunkIndex),
            ChokeFrame => Array.Empty<byte>(),
            UnchokeFrame => Array.Empty<byte>(),
            InterestedFrame => Array.Empty<byte>(),
            NotInterestedFrame => Array.Empty<byte>(),
            KeepAliveFrame => Array.Empty<byte>(),
            PexPeersFrame pex => EncodePexPeers(pex),
            _ => throw new NotSupportedException($"Unsupported frame type: {frame.GetType().Name}")
        };

        if (payload.Length > MaxPayloadSizeBytes)
        {
            throw new InvalidOperationException($"Encoded frame payload {payload.Length} exceeds limit ({MaxPayloadSizeBytes} bytes).");
        }

        return (opcode, payload);
    }

    public static IStreamFramePayload Decode(byte opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSizeBytes)
        {
            throw new InvalidDataException($"Frame payload length {payload.Length} exceeds maximum allowed limit ({MaxPayloadSizeBytes} bytes).");
        }

        var frameType = (StreamFrameType)opcode;
        return frameType switch
        {
            StreamFrameType.Handshake => DecodeHandshake(payload),
            StreamFrameType.Bitfield => DecodeBitfield(payload),
            StreamFrameType.Have => new HaveFrame(DecodeChunkIndex(payload)),
            StreamFrameType.RequestChunk => new RequestChunkFrame(DecodeChunkIndex(payload)),
            StreamFrameType.ChunkData => DecodeChunkData(payload),
            StreamFrameType.CancelChunk => new CancelChunkFrame(DecodeChunkIndex(payload)),
            StreamFrameType.Choke => new ChokeFrame(),
            StreamFrameType.Unchoke => new UnchokeFrame(),
            StreamFrameType.Interested => new InterestedFrame(),
            StreamFrameType.NotInterested => new NotInterestedFrame(),
            StreamFrameType.KeepAlive => new KeepAliveFrame(),
            StreamFrameType.PexPeers => DecodePexPeers(payload),
            _ => throw new InvalidDataException($"Unknown stream frame opcode: 0x{opcode:X2}")
        };
    }

    private static byte[] EncodeHandshake(HandshakeFrame frame)
    {
        byte[] payload = new byte[HandshakePayloadSizeBytes];
        frame.AuthToken.Span.CopyTo(payload.AsSpan(0, 32));
        frame.SessionId.TryWriteBytes(payload.AsSpan(32, 16));
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(48, 4), frame.StreamIndex.Value);
        frame.SenderPeerId.TryWriteBytes(payload.AsSpan(52, 16));
        return payload;
    }

    private static HandshakeFrame DecodeHandshake(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HandshakePayloadSizeBytes)
        {
            throw new InvalidDataException($"Handshake frame payload must be exactly {HandshakePayloadSizeBytes} bytes, received {payload.Length}.");
        }

        var authToken = StreamAuthToken.FromSpan(payload.Slice(0, 32));
        var sessionId = TransferSessionId.FromBytes(payload.Slice(32, 16));
        var streamIndex = StreamIndex.FromInt(BinaryPrimitives.ReadInt32BigEndian(payload.Slice(48, 4)));
        var senderPeerId = Domain.Identities.PublicIdentityId.FromBytes(payload.Slice(52, 16));

        return new HandshakeFrame(authToken, sessionId, streamIndex, senderPeerId);
    }

    private static byte[] EncodeBitfield(BitfieldFrame frame)
    {
        byte[] bitfieldBytes = frame.Bitfield.ToByteArray();
        byte[] payload = new byte[4 + bitfieldBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), frame.Bitfield.TotalChunks.Count);
        bitfieldBytes.CopyTo(payload.AsSpan(4));
        return payload;
    }

    private static BitfieldFrame DecodeBitfield(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException($"Bitfield payload too short ({payload.Length} bytes).");
        }

        int totalChunks = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(0, 4));
        int expectedByteLen = (totalChunks + 7) / 8;
        if (payload.Length - 4 == expectedByteLen)
        {
            var bitfield = Bitfield.FromBytes(payload.Slice(4), ChunkCount.FromInt(totalChunks));
            return new BitfieldFrame(bitfield);
        }

        // Fallback if raw bitfield without chunk count prefix
        int fallbackTotal = payload.Length * 8;
        var rawBitfield = Bitfield.FromBytes(payload, ChunkCount.FromInt(fallbackTotal));
        return new BitfieldFrame(rawBitfield);
    }

    private static byte[] EncodeChunkIndex(ChunkIndex index)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, index.Value);
        return payload;
    }

    private static ChunkIndex DecodeChunkIndex(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4)
        {
            throw new InvalidDataException($"Chunk index payload must be exactly 4 bytes, received {payload.Length}.");
        }

        return ChunkIndex.FromInt(BinaryPrimitives.ReadInt32BigEndian(payload));
    }

    private static byte[] EncodeChunkData(ChunkDataFrame frame)
    {
        byte[] payload = new byte[4 + frame.Data.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), frame.ChunkIndex.Value);
        frame.Data.Span.CopyTo(payload.AsSpan(4));
        return payload;
    }

    private static ChunkDataFrame DecodeChunkData(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 4)
        {
            throw new InvalidDataException($"ChunkData payload must contain a 4-byte index and at least 1 data byte, received {payload.Length}.");
        }

        int chunkIndex = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(0, 4));
        var plaintextData = PlaintextChunkData.FromBytes(payload.Slice(4).ToArray());
        return new ChunkDataFrame(ChunkIndex.FromInt(chunkIndex), plaintextData);
    }

    private static byte[] EncodePexPeers(PexPeersFrame frame)
    {
        using var ms = new MemoryStream();
        int count = Math.Min(16, frame.Candidates.Count);
        ms.WriteByte((byte)count);

        for (int i = 0; i < count; i++)
        {
            DirectEndpointCandidate candidate = frame.Candidates[i];
            ms.WriteByte((byte)candidate.Kind);

            byte[] portBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, candidate.Port.Value);
            ms.Write(portBytes);

            byte[] hostBytes = Encoding.UTF8.GetBytes(candidate.Host.Value);
            byte[] hostLenBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(hostLenBytes, (ushort)hostBytes.Length);
            ms.Write(hostLenBytes);
            ms.Write(hostBytes);
        }

        return ms.ToArray();
    }

    private static PexPeersFrame DecodePexPeers(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("PexPeers payload cannot be empty.");
        }

        int count = payload[0];
        int offset = 1;
        var candidates = new List<DirectEndpointCandidate>(count);

        for (int i = 0; i < count; i++)
        {
            if (offset + 5 > payload.Length)
            {
                throw new InvalidDataException("Truncated PexPeers candidate header.");
            }

            var kind = (DirectCandidateKind)payload[offset];
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 1, 2));
            ushort hostLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 3, 2));
            offset += 5;

            if (offset + hostLen > payload.Length)
            {
                throw new InvalidDataException("Truncated PexPeers host bytes.");
            }

            string host = Encoding.UTF8.GetString(payload.Slice(offset, hostLen));
            offset += hostLen;

            candidates.Add(new DirectEndpointCandidate(
                EndpointHost.Parse(host),
                PortNumber.FromInt(port),
                kind));
        }

        return new PexPeersFrame(candidates);
    }
}
