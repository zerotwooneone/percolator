namespace Percolator.Infrastructure.Serialization;

using System;
using Google.Protobuf;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;

public sealed class ProtobufSessionWirePacker : ISessionWirePacker
{
    public ReadOnlyMemory<byte> PackDirectRatchetMessage(RatchetHeader header, ReadOnlyMemory<byte> ciphertext)
    {
        var proto = new DirectSessionPackageProto
        {
            Header = new RatchetHeaderProto
            {
                RatchetPublicKey = ByteString.CopyFrom(header.EphemeralPublicKey.Span),
                Counter = header.Counter,
                PreviousChainLength = header.PreviousChainLength
            },
            Nonce = ByteString.Empty,
            Ciphertext = ByteString.CopyFrom(ciphertext.Span)
        };

        return proto.ToByteArray();
    }

    public ReadOnlyMemory<byte> PackGroupMessage(ChannelId channelId, uint iteration, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> ciphertext)
    {
        var proto = new GroupSessionPackageProto
        {
            ConversationId = ByteString.CopyFrom(channelId.Value.ToByteArray(bigEndian: true)),
            Iteration = iteration,
            Ciphertext = ByteString.CopyFrom(ciphertext.Span),
            AuthorSignature = ByteString.CopyFrom(signature.Span)
        };

        return proto.ToByteArray();
    }

    public ReadOnlyMemory<byte> PackHandshakeMessage(HandshakeHeader header, ReadOnlyMemory<byte> encryptedPayload)
    {
        var proto = new HandshakeInvitationPackageProto
        {
            InitiatorIdentityKey = ByteString.CopyFrom(header.SenderIdentityKey.Span),
            InitiatorEphemeralKey = ByteString.CopyFrom(header.SenderEphemeralKey.Span),
            SignedPrekeyId = header.SignedPreKeyId,
            EncryptedPayload = ByteString.CopyFrom(encryptedPayload.Span)
        };

        if (header.OneTimePreKeyId.HasValue)
        {
            proto.OnetimePrekeyId = header.OneTimePreKeyId.Value;
        }

        return proto.ToByteArray();
    }

    public DomainResult<InboundDirectEnvelope> UnpackDirectRatchetMessage(
        ChannelId channelId,
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.IsEmpty)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("PACKER_EMPTY_PAYLOAD", "Wire bytes cannot be empty"));
        }

        try
        {
            var proto = DirectSessionPackageProto.Parser.ParseFrom(wireBytes.Span);
            if (proto.Header == null || proto.Header.RatchetPublicKey.Length != 32)
            {
                return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("PACKER_INVALID_HEADER", "Direct session header missing or invalid DH key length"));
            }

            var dhKey = DhPublicKey.FromSpan(proto.Header.RatchetPublicKey.Span);
            var header = new RatchetHeader(dhKey, proto.Header.Counter, proto.Header.PreviousChainLength);
            var ciphertext = proto.Ciphertext.Memory;

            return DomainResult<InboundDirectEnvelope>.Success(new InboundDirectEnvelope(
                channelId,
                recipientId,
                senderId,
                senderDeviceId,
                header,
                ciphertext,
                receivedAtUtc));
        }
        catch (Exception ex)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("PACKER_CORRUPT_WIRE", ex.Message));
        }
    }

    public DomainResult<InboundGroupEnvelope> UnpackGroupMessage(
        ChannelId channelId,
        PublicIdentityId recipientId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.IsEmpty)
        {
            return DomainResult<InboundGroupEnvelope>.Failure(new DomainError("PACKER_EMPTY_PAYLOAD", "Wire bytes cannot be empty"));
        }

        try
        {
            var proto = GroupSessionPackageProto.Parser.ParseFrom(wireBytes.Span);
            var ciphertext = proto.Ciphertext.Memory;
            var signature = proto.AuthorSignature.Memory;

            return DomainResult<InboundGroupEnvelope>.Success(new InboundGroupEnvelope(
                channelId,
                recipientId,
                authorId,
                authorDeviceId,
                proto.Iteration,
                ciphertext,
                signature,
                receivedAtUtc));
        }
        catch (Exception ex)
        {
            return DomainResult<InboundGroupEnvelope>.Failure(new DomainError("PACKER_CORRUPT_WIRE", ex.Message));
        }
    }

    public DomainResult<InboundHandshakeEnvelope> UnpackHandshakeMessage(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.IsEmpty)
        {
            return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("PACKER_EMPTY_PAYLOAD", "Wire bytes cannot be empty"));
        }

        try
        {
            var proto = HandshakeInvitationPackageProto.Parser.ParseFrom(wireBytes.Span);
            if (proto.InitiatorIdentityKey.Length != 32 || proto.InitiatorEphemeralKey.Length != 32)
            {
                return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("PACKER_INVALID_KEYS", "Invalid handshake key lengths"));
            }

            var identityKey = IdentityKey.FromSpan(proto.InitiatorIdentityKey.Span);
            var ephemeralKey = DhPublicKey.FromSpan(proto.InitiatorEphemeralKey.Span);
            uint? opkId = proto.HasOnetimePrekeyId ? proto.OnetimePrekeyId : null;
            var encryptedPayload = proto.EncryptedPayload.Memory;

            return DomainResult<InboundHandshakeEnvelope>.Success(new InboundHandshakeEnvelope(
                recipientId,
                senderId,
                senderDeviceId,
                identityKey,
                ephemeralKey,
                proto.SignedPrekeyId,
                opkId,
                encryptedPayload,
                receivedAtUtc));
        }
        catch (Exception ex)
        {
            return DomainResult<InboundHandshakeEnvelope>.Failure(new DomainError("PACKER_CORRUPT_WIRE", ex.Message));
        }
    }
}
