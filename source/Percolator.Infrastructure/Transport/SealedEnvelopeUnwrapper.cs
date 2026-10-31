namespace Percolator.Infrastructure.Transport;

using System;
using System.Buffers.Binary;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;

public enum WireFrameKind : byte
{
    DirectRatchet = 0x01,
    GroupBroadcast = 0x02,
    X3dhHandshake = 0x03
}

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

public sealed class SealedEnvelopeUnwrapper : ISealedEnvelopeUnwrapper
{
    private readonly ISessionWirePacker _wirePacker;
    private readonly IDateTimeProvider _timeProvider;
    private readonly PublicIdentityId _recipientId;

    public SealedEnvelopeUnwrapper(
        ISessionWirePacker wirePacker,
        IDateTimeProvider? timeProvider = null,
        PublicIdentityId? recipientId = null)
    {
        _wirePacker = wirePacker ?? throw new ArgumentNullException(nameof(wirePacker));
        _timeProvider = timeProvider ?? new DefaultDateTimeProvider();
        _recipientId = recipientId ?? PublicIdentityId.New();
    }

    public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope)
    {
        try
        {
            ReadOnlyMemory<byte> rawPayload = envelope.Ciphertext;

            // Check if envelope.Ciphertext contains a SealedEnvelopeProto wrapper
            try
            {
                var proto = SealedEnvelopeProto.Parser.ParseFrom(envelope.Ciphertext.Span);
                if (proto != null && !proto.EncryptedPackage.IsEmpty)
                {
                    rawPayload = proto.EncryptedPackage.Memory;
                }
            }
            catch
            {
                // Not a protobuf wrapper, proceed with raw bytes
            }

            return UnwrapFrame(rawPayload, _recipientId, _timeProvider.UtcNow);
        }
        catch (Exception ex)
        {
            return DomainResult<InboundEnvelope>.Failure(new DomainError("CORRUPTED_ENVELOPE", ex.Message));
        }
    }

    public DomainResult<InboundEnvelope> UnwrapFrame(
        ReadOnlyMemory<byte> frameBytes,
        PublicIdentityId recipientId,
        DateTimeOffset receivedAtUtc)
    {
        try
        {
            var decodeResult = SealedWireFraming.Decode(frameBytes);
            if (!decodeResult.IsSuccess)
            {
                return DomainResult<InboundEnvelope>.Failure(decodeResult.Error!);
            }

            var (kind, channelId, senderId, senderDeviceId, packedWireBytes) = decodeResult.Value;

            switch (kind)
            {
                case WireFrameKind.DirectRatchet:
                {
                    var unpackResult = _wirePacker.UnpackDirectRatchetMessage(
                        channelId, recipientId, senderId, senderDeviceId, packedWireBytes, receivedAtUtc);
                    if (!unpackResult.IsSuccess)
                    {
                        return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                    }
                    return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
                }
                case WireFrameKind.GroupBroadcast:
                {
                    var unpackResult = _wirePacker.UnpackGroupMessage(
                        channelId, recipientId, senderId, senderDeviceId, packedWireBytes, receivedAtUtc);
                    if (!unpackResult.IsSuccess)
                    {
                        return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                    }
                    return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
                }
                case WireFrameKind.X3dhHandshake:
                {
                    var unpackResult = _wirePacker.UnpackHandshakeMessage(
                        recipientId, senderId, senderDeviceId, packedWireBytes, receivedAtUtc);
                    if (!unpackResult.IsSuccess)
                    {
                        return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                    }
                    return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
                }
                default:
                    return DomainResult<InboundEnvelope>.Failure(new DomainError("UNKNOWN_KIND", $"Unknown WireFrameKind: {kind}"));
            }
        }
        catch (Exception ex)
        {
            return DomainResult<InboundEnvelope>.Failure(new DomainError("CORRUPTED_FRAME", ex.Message));
        }
    }

    private sealed class DefaultDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
