namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;

public static class SwarmFrameCodec
{
    public const int MaxPayloadSizeBytes = 262_144; // 256 KB
    public const int HandshakePayloadSizeBytes = 68;

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
            throw new InvalidOperationException($"Encoded frame payload {payload.Length} exceeds 256 KB limit.");
        }

        return (opcode, payload);
    }

    public static IStreamFramePayload Decode(byte opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSizeBytes)
        {
            throw new InvalidDataException($"Frame payload length {payload.Length} exceeds maximum allowed 256 KB limit.");
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
        var senderPeerId = PublicIdentityId.FromBytes(payload.Slice(52, 16));

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
        if (payload.Length < 4)
        {
            throw new InvalidDataException($"ChunkData payload must be at least 4 bytes, received {payload.Length}.");
        }

        int chunkIndex = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(0, 4));
        var encryptedData = EncryptedChunkData.FromBytes(payload.Slice(4).ToArray());
        return new ChunkDataFrame(ChunkIndex.FromInt(chunkIndex), encryptedData);
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
