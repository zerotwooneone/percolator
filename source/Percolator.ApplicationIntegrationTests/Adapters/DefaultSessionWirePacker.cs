using System.Buffers.Binary;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class DefaultSessionWirePacker : ISessionWirePacker
{
    public ReadOnlyMemory<byte> PackDirectRatchetMessage(RatchetHeader header, ReadOnlyMemory<byte> ciphertext)
    {
        var result = new byte[1 + 40 + ciphertext.Length];
        result[0] = (byte)WireFrameKind.DirectRatchet;
        header.EphemeralPublicKey.Span.CopyTo(result.AsSpan(1, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(33, 4), header.Counter);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(37, 4), header.PreviousChainLength);
        ciphertext.Span.CopyTo(result.AsSpan(41));
        return result;
    }

    public ReadOnlyMemory<byte> PackGroupMessage(ChannelId channelId, uint iteration, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> ciphertext)
    {
        var result = new byte[1 + 4 + signature.Length + ciphertext.Length];
        result[0] = (byte)WireFrameKind.GroupBroadcast;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1, 4), iteration);
        signature.Span.CopyTo(result.AsSpan(5, signature.Length));
        ciphertext.Span.CopyTo(result.AsSpan(5 + signature.Length));
        return result;
    }

    public ReadOnlyMemory<byte> PackHandshakeMessage(HandshakeHeader header, ReadOnlyMemory<byte> encryptedPayload)
    {
        bool hasOpk = header.OneTimePreKeyId.HasValue;
        int headerLen = 1 + 32 + 32 + 4 + 1 + (hasOpk ? 4 : 0);
        var result = new byte[headerLen + encryptedPayload.Length];
        result[0] = (byte)WireFrameKind.X3dhHandshake;

        header.SenderIdentityKey.Span.CopyTo(result.AsSpan(1, 32));
        header.SenderEphemeralKey.Span.CopyTo(result.AsSpan(33, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(65, 4), header.SignedPreKeyId);
        result[69] = (byte)(hasOpk ? 1 : 0);

        int payloadOffset = 70;
        if (hasOpk)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(70, 4), header.OneTimePreKeyId!.Value);
            payloadOffset = 74;
        }

        encryptedPayload.Span.CopyTo(result.AsSpan(payloadOffset));
        return result;
    }

    public DomainResult<InboundDirectEnvelope> UnpackDirectRatchetMessage(
        ChannelId channelId,
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.Length < 41)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short"));
        }

        if (wireBytes.Span[0] != (byte)WireFrameKind.DirectRatchet)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("INVALID_WIRE_KIND", $"Expected DirectRatchet 0x01 but got 0x{wireBytes.Span[0]:X2}"));
        }

        var payloadBytes = wireBytes.Slice(1);
        var keySpan = payloadBytes.Slice(0, 32).Span;
        if (!DhPublicKey.TryFromSpan(keySpan, out var dhKey) || dhKey is null)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("INVALID_KEY", "Invalid DH key"));
        }

        uint counter = BinaryPrimitives.ReadUInt32LittleEndian(payloadBytes.Slice(32, 4).Span);
        uint previousChainLength = BinaryPrimitives.ReadUInt32LittleEndian(payloadBytes.Slice(36, 4).Span);
        var header = new RatchetHeader(dhKey, counter, previousChainLength);
        var ciphertext = payloadBytes.Slice(40);

        return DomainResult<InboundDirectEnvelope>.Success(new InboundDirectEnvelope(
            channelId,
            recipientId,
            senderId,
            senderDeviceId,
            header,
            ciphertext,
            receivedAtUtc));
    }

    public DomainResult<InboundGroupEnvelope> UnpackGroupMessage(
        ChannelId channelId,
        PublicIdentityId recipientId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.Length < 69)
        {
            return DomainResult<InboundGroupEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short for group frame"));
        }

        if (wireBytes.Span[0] != (byte)WireFrameKind.GroupBroadcast)
        {
            return DomainResult<InboundGroupEnvelope>.Failure(new DomainError("INVALID_WIRE_KIND", $"Expected GroupBroadcast 0x02 but got 0x{wireBytes.Span[0]:X2}"));
        }

        var payloadBytes = wireBytes.Slice(1);
        uint iteration = BinaryPrimitives.ReadUInt32LittleEndian(payloadBytes.Slice(0, 4).Span);
        var sig = payloadBytes.Slice(4, 64);
        var ciphertext = payloadBytes.Slice(68);

        return DomainResult<InboundGroupEnvelope>.Success(new InboundGroupEnvelope(
            channelId,
            recipientId,
            authorId,
            authorDeviceId,
            iteration,
            ciphertext,
            sig,
            receivedAtUtc));
    }

    public DomainResult<InboundHandshakeEnvelope> UnpackHandshakeMessage(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.Length < 70)
        {
            return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short for handshake header"));
        }

        if (wireBytes.Span[0] != (byte)WireFrameKind.X3dhHandshake)
        {
            return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("INVALID_WIRE_KIND", $"Expected X3dhHandshake 0x03 but got 0x{wireBytes.Span[0]:X2}"));
        }

        var payloadBytes = wireBytes.Slice(1);

        if (!IdentityKey.TryFromSpan(payloadBytes.Slice(0, 32).Span, out var ik) || ik is null)
        {
            return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("INVALID_KEY", "Invalid sender identity key"));
        }

        if (!DhPublicKey.TryFromSpan(payloadBytes.Slice(32, 32).Span, out var ek) || ek is null)
        {
            return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("INVALID_KEY", "Invalid ephemeral DH key"));
        }

        uint spkId = BinaryPrimitives.ReadUInt32LittleEndian(payloadBytes.Slice(64, 4).Span);
        bool hasOpk = payloadBytes.Span[68] == 1;

        uint? opkId = null;
        int payloadOffset = 69;

        if (hasOpk)
        {
            if (payloadBytes.Length < 73)
            {
                return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short for one-time prekey ID"));
            }

            opkId = BinaryPrimitives.ReadUInt32LittleEndian(payloadBytes.Slice(69, 4).Span);
            payloadOffset = 73;
        }

        var encryptedPayload = payloadBytes.Slice(payloadOffset);

        return DomainResult<InboundHandshakeEnvelope>.Success(new InboundHandshakeEnvelope(
            recipientId,
            senderId,
            senderDeviceId,
            ik,
            ek,
            spkId,
            opkId,
            encryptedPayload,
            receivedAtUtc));
    }
}
