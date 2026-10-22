using System.Buffers.Binary;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public static class SealedWireFraming
{
    public const int HeaderSize = 1 + 16 + 16 + 16 + 4; // 53 bytes

    public static byte[] Encode(
        WireFrameKind kind,
        ChannelId channelId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> packedWireBytes)
    {
        var output = new byte[HeaderSize + packedWireBytes.Length];
        output[0] = (byte)kind;

        channelId.Value.TryWriteBytes(output.AsSpan(1, 16));
        senderId.Value.TryWriteBytes(output.AsSpan(17, 16));

        // SenderDeviceId: uint encoded in first 4 bytes of 16-byte slot, padded with 0
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(33, 4), senderDeviceId.Value);
        output.AsSpan(37, 12).Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(49, 4), (uint)packedWireBytes.Length);

        packedWireBytes.Span.CopyTo(output.AsSpan(HeaderSize));
        return output;
    }

    public static DomainResult<(WireFrameKind Kind, ChannelId ChannelId, PublicIdentityId SenderId, DeviceId SenderDeviceId, ReadOnlyMemory<byte> PackedWireBytes)> Decode(
        ReadOnlyMemory<byte> frameBytes)
    {
        if (frameBytes.Length < HeaderSize)
        {
            return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("FRAME_TOO_SHORT", $"Frame length {frameBytes.Length} is shorter than required header size {HeaderSize}."));
        }

        var span = frameBytes.Span;
        var kind = (WireFrameKind)span[0];
        if (kind is not (WireFrameKind.DirectRatchet or WireFrameKind.GroupBroadcast or WireFrameKind.X3dhHandshake))
        {
            return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("UNKNOWN_FRAME_KIND", $"Unknown WireFrameKind byte: 0x{span[0]:X2}."));
        }

        var channelGuid = new Guid(span.Slice(1, 16));
        var senderGuid = new Guid(span.Slice(17, 16));
        uint deviceVal = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(33, 4));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(49, 4));

        if (frameBytes.Length < HeaderSize + payloadLength)
        {
            return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("FRAME_TRUNCATED", $"Frame payload length {payloadLength} exceeds available bytes {frameBytes.Length - HeaderSize}."));
        }

        var packedPayload = frameBytes.Slice(HeaderSize, (int)payloadLength);
        return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Success(
            (kind, new ChannelId(channelGuid), new PublicIdentityId(senderGuid), new DeviceId(deviceVal), packedPayload));
    }
}
