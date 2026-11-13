namespace Percolator.Infrastructure.Transport;

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
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
    public const byte SealedMagicByte = 0x53; // 'S'
    public const int InnerHeaderSize = 1 + 16 + 16 + 4 + 12 + 4; // 53 bytes
    public const int OuterAeadHeaderSize = 1 + 12 + 16; // 29 bytes: Magic (1B) + Nonce (12B) + Tag (16B)
    public const int HeaderSize = OuterAeadHeaderSize + InnerHeaderSize; // 82 bytes

    private static readonly byte[] DefaultKeyIkm =
        SHA256.HashData("Percolator.SealedWireEnvelope.DefaultKey.v1"u8);

    public static byte[] Encode(
        WireFrameKind kind,
        ChannelId channelId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> packedWireBytes)
    {
        var plaintext = new byte[InnerHeaderSize + packedWireBytes.Length];
        plaintext[0] = (byte)kind;

        channelId.TryWriteBytes(plaintext.AsSpan(1, 16));
        senderId.TryWriteBytes(plaintext.AsSpan(17, 16));

        BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(33, 4), senderDeviceId.Value);
        plaintext.AsSpan(37, 12).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(49, 4), (uint)packedWireBytes.Length);

        packedWireBytes.Span.CopyTo(plaintext.AsSpan(InnerHeaderSize));

        var output = new byte[HeaderSize + packedWireBytes.Length];
        output[0] = SealedMagicByte;

        var nonce = output.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);

        var tag = output.AsSpan(13, 16);
        Span<byte> derivedKey = stackalloc byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            DefaultKeyIkm,
            derivedKey,
            salt: nonce,
            info: "Percolator.SealedWireFraming.AEAD.v2"u8);

        try
        {
            using var aes = new AesGcm(derivedKey, tagSizeInBytes: 16);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(OuterAeadHeaderSize),
                tag,
                associatedData: output.AsSpan(0, 1));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }

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
        if (span[0] != SealedMagicByte)
        {
            return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("UNKNOWN_FRAME_KIND", $"Unknown sealed frame magic byte: 0x{span[0]:X2}. Expected 0x{SealedMagicByte:X2}."));
        }

        var nonce = span.Slice(1, 12);
        var tag = span.Slice(13, 16);
        var ciphertext = span.Slice(OuterAeadHeaderSize);

        Span<byte> derivedKey = stackalloc byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            DefaultKeyIkm,
            derivedKey,
            salt: nonce,
            info: "Percolator.SealedWireFraming.AEAD.v2"u8);

        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(derivedKey, tagSizeInBytes: 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData: span.Slice(0, 1));
        }
        catch (CryptographicException)
        {
            return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("FRAME_TRUNCATED", "Sealed frame authentication failed or frame was truncated/corrupted."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }

        var kind = (WireFrameKind)plaintext[0];
        if (kind is not (WireFrameKind.DirectRatchet or WireFrameKind.GroupBroadcast or WireFrameKind.X3dhHandshake))
        {
            return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("UNKNOWN_FRAME_KIND", $"Unknown WireFrameKind byte: 0x{plaintext[0]:X2}."));
        }

        var channelId = ChannelId.FromBytes(plaintext.AsSpan(1, 16));
        var senderId = PublicIdentityId.FromBytes(plaintext.AsSpan(17, 16));
        uint deviceVal = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(33, 4));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(49, 4));

        if (InnerHeaderSize + payloadLength > plaintext.Length)
        {
            return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("FRAME_TRUNCATED", $"Frame payload length {payloadLength} exceeds available bytes {plaintext.Length - InnerHeaderSize}."));
        }

        var packedPayload = new ReadOnlyMemory<byte>(plaintext, InnerHeaderSize, (int)payloadLength);
        return DomainResult<(WireFrameKind, ChannelId, PublicIdentityId, DeviceId, ReadOnlyMemory<byte>)>.Success(
            (kind, channelId, senderId, new DeviceId(deviceVal), packedPayload));
    }
}

public sealed class SealedEnvelopeUnwrapper : ISealedEnvelopeUnwrapper
{
    private readonly ISessionWirePacker _wirePacker;
    private readonly IDateTimeProvider _timeProvider;
    private readonly PublicIdentityId _recipientId;

    public SealedEnvelopeUnwrapper(
        ISessionWirePacker wirePacker,
        PublicIdentityId recipientId,
        IDateTimeProvider timeProvider)
    {
        _wirePacker = wirePacker ?? throw new ArgumentNullException(nameof(wirePacker));
        _recipientId = recipientId;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope)
    {
        return UnwrapDirect(envelope.Ciphertext);
    }

    public DomainResult<InboundEnvelope> UnwrapDirect(ReadOnlyMemory<byte> payloadBytes)
    {
        try
        {
            ReadOnlyMemory<byte> rawPayload = payloadBytes;

            // Fast path: SealedWireFraming frames start with SealedMagicByte (0x53).
            // If the first byte is not SealedMagicByte, check if it is a SealedEnvelopeProto wrapper.
            if (rawPayload.Length > 0 && rawPayload.Span[0] != SealedWireFraming.SealedMagicByte)
            {
                try
                {
                    var proto = SealedEnvelopeProto.Parser.ParseFrom(rawPayload.Span);
                    if (proto != null && !proto.EncryptedPackage.IsEmpty)
                    {
                        rawPayload = proto.EncryptedPackage.Memory;
                    }
                }
                catch (InvalidProtocolBufferException)
                {
                    // Not a protobuf wrapper, proceed with raw bytes
                }
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
